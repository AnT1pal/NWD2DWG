// ============================================================================
//  CadPurger.cs — Глубокая чистка DXF от мусора (слои, блоки, типы линий)
//  NWD2DWG v3.1 | namespace NWD2DWG.Plugin
//
//  Замещает: CAD Doctor / встроенный PURGE AutoCAD
//
//  Алгоритм (два потоковых прохода, файл целиком в память не читается):
//    Проход 1: сканируем ENTITIES и BLOCKS, собираем реально используемые
//              слои, типы линий, текстовые стили и имена блоков.
//    Проход 2: переписываем файл, выбрасывая из TABLES неиспользуемые записи
//              (LAYER / LTYPE / STYLE / BLOCK_RECORD) и из BLOCKS —
//              определения блоков, на которые нет ни одной ссылки.
//
//  Типичная экономия: 20–40% размера файла для NWD→DXF конвертов
//  с большим количеством дисциплинарных слоёв-пустышек.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    public static class CadPurger
    {
        // ISO-8859-1 отображает байты 0..255 на U+0000..U+00FF один в один,
        // поэтому чтение + запись возвращают файл байт в байт. Прежняя версия
        // читала и писала в ASCII: любая кириллица в именах слоёв и в TEXT
        // молча превращалась в '?', то есть чистка портила данные.
        private static readonly Encoding Raw = Encoding.GetEncoding(28591);

        // Имена, которые нельзя удалять ни при каких условиях
        private static HashSet<string> NewNameSet(params string[] seed)
        {
            return new HashSet<string>(seed, StringComparer.OrdinalIgnoreCase);
        }

        // -------------------------------------------------------------------------
        // Главный метод: чистит DXF-файл на месте (или в новый путь)
        // -------------------------------------------------------------------------
        /// <returns>Строка статистики для лога</returns>
        // Набор таблиц, которые разрешено чистить (из настроек модулей)
        public class PurgeScope
        {
            public bool Layers = true, Linetypes = true, TextStyles = true, Blocks = true;
            public bool Allows(string table)
            {
                switch (table)
                {
                    case "LAYER":        return Layers;
                    case "LTYPE":        return Linetypes;
                    case "STYLE":        return TextStyles;
                    case "BLOCK_RECORD": return Blocks;
                    default:             return false;
                }
            }
        }

        [ThreadStatic] private static PurgeScope _scope;
        private static PurgeScope Scope { get { return _scope ?? (_scope = new PurgeScope()); } }

        public static string Purge(string inputDxf, string outputDxf, PurgeScope scope)
        {
            _scope = scope ?? new PurgeScope();
            try { return Purge(inputDxf, outputDxf); }
            finally { _scope = null; }
        }

        public static string Purge(string inputDxf, string outputDxf = null)
        {
            if (!File.Exists(inputDxf))
                return "[CadPurger] Файл не найден: " + inputDxf;

            bool inPlace = string.IsNullOrEmpty(outputDxf) ||
                           string.Equals(Path.GetFullPath(outputDxf),
                                         Path.GetFullPath(inputDxf),
                                         StringComparison.OrdinalIgnoreCase);

            // Пишем во временный файл и подменяем оригинал только после успеха:
            // прежняя версия перезаписывала исходник напрямую и при сбое
            // посреди записи уничтожала его.
            string target = inPlace ? inputDxf + ".purge.tmp" : outputDxf;

            long sizeBefore;
            try { sizeBefore = new FileInfo(inputDxf).Length; }
            catch (Exception ex) { return "[CadPurger] Ошибка доступа: " + ex.Message; }

            var usedLayers = NewNameSet("0");
            var usedLtypes = NewNameSet("Continuous", "ByLayer", "ByBlock");
            var usedStyles = NewNameSet("Standard");
            var usedBlocks = NewNameSet("*Model_Space", "*Paper_Space", "*Model_space", "*Paper_space");

            try { Collect(inputDxf, usedLayers, usedLtypes, usedStyles, usedBlocks); }
            catch (Exception ex) { return "[CadPurger] Ошибка чтения: " + ex.Message; }

            // Собственный вывод конвертера объявляет ровно те слои, которые
            // использует, — чистить нечего. Проверяем это по секции TABLES
            // (она в начале файла), чтобы не перегонять впустую сотни мегабайт.
            if (!AnyRemovable(inputDxf, usedLayers, usedLtypes, usedStyles, usedBlocks))
                return string.Format(CultureInfo.InvariantCulture,
                    "[CadPurger] Неиспользуемых слоёв, типов линий, стилей и блоков не найдено — " +
                    "файл не переписывался ({0:F0} КБ)", sizeBefore / 1024.0);

            var stat = new PurgeStat();
            try { Rewrite(inputDxf, target, usedLayers, usedLtypes, usedStyles, usedBlocks, stat); }
            catch (Exception ex)
            {
                try { if (inPlace && File.Exists(target)) File.Delete(target); } catch { }
                return "[CadPurger] Ошибка записи: " + ex.Message;
            }

            if (inPlace)
            {
                string bak = inputDxf + ".purge.bak";
                try
                {
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(inputDxf, bak);
                    File.Move(target, inputDxf);
                    File.Delete(bak);
                }
                catch (Exception ex)
                {
                    return "[CadPurger] Ошибка подмены файла: " + ex.Message +
                           " (исходник сохранён: " + bak + ")";
                }
                target = inputDxf;
            }

            long sizeAfter = 0;
            try { sizeAfter = new FileInfo(target).Length; } catch { }
            double savings = sizeBefore > 0 ? 100.0 * (sizeBefore - sizeAfter) / sizeBefore : 0;

            return string.Format(CultureInfo.InvariantCulture,
                "[CadPurger] Очищено: слоёв={0} типов линий={1} стилей={2} блоков={3} (BLOCK_RECORD={4}) | " +
                "{5:F0} КБ → {6:F0} КБ (экономия {7:F1}%)",
                stat.Layers, stat.Ltypes, stat.Styles, stat.Blocks, stat.BlockRecords,
                sizeBefore / 1024.0, sizeAfter / 1024.0, savings);
        }

        private class PurgeStat
        {
            public int Layers, Ltypes, Styles, Blocks, BlockRecords;
        }

        // -------------------------------------------------------------------------
        // Проход 1: сбор используемых имён из ENTITIES и BLOCKS
        // -------------------------------------------------------------------------
        private static void Collect(string path,
            HashSet<string> layers, HashSet<string> ltypes,
            HashSet<string> styles, HashSet<string> blocks)
        {
            using (var r = new PairReader(path, Raw))
            {
                string section = null;
                string entity = null;
                string code, value;

                while (r.Read(out code, out value))
                {
                    if (code == "0")
                    {
                        if (value == "SECTION")
                        {
                            string c2, v2;
                            if (r.Read(out c2, out v2) && c2 == "2") section = v2;
                            entity = null;
                            continue;
                        }
                        if (value == "ENDSEC") { section = null; entity = null; continue; }
                        entity = value;
                        continue;
                    }

                    if (section != "ENTITIES" && section != "BLOCKS") continue;

                    switch (code)
                    {
                        case "8": if (!string.IsNullOrEmpty(value)) layers.Add(value); break;
                        case "6": if (!string.IsNullOrEmpty(value)) ltypes.Add(value); break;
                        case "7": if (!string.IsNullOrEmpty(value)) styles.Add(value); break;
                        case "2":
                            // Группа 2 — это имя блока только для ссылок (INSERT,
                            // DIMENSION). Внутри BLOCKS та же группа несёт имя
                            // самого определения и ссылкой не является.
                            if (entity == "INSERT" || entity == "DIMENSION")
                                if (!string.IsNullOrEmpty(value)) blocks.Add(value);
                            break;
                        case "3":
                            // имя блока стрелки/размерного стиля тоже держим
                            if (entity == "DIMENSION" && !string.IsNullOrEmpty(value))
                                blocks.Add(value);
                            break;
                    }
                }
            }
        }

        // Быстрая проверка: есть ли вообще что удалять. Читает только секцию
        // TABLES и останавливается на её конце.
        private static bool AnyRemovable(string path,
            HashSet<string> layers, HashSet<string> ltypes,
            HashSet<string> styles, HashSet<string> blocks)
        {
            using (var r = new PairReader(path, Raw))
            {
                string section = null, table = null, code, value;
                var stat = new PurgeStat();
                while (r.Read(out code, out value))
                {
                    if (code == "0" && value == "SECTION")
                    {
                        string c2, v2;
                        if (r.Read(out c2, out v2) && c2 == "2") section = v2;
                        continue;
                    }
                    if (code == "0" && value == "ENDSEC")
                    {
                        if (section == "TABLES") return false; // дошли до конца таблиц
                        section = null; table = null;
                        continue;
                    }
                    if (section != "TABLES") continue;

                    if (code == "0" && value == "TABLE")
                    {
                        string c2, v2;
                        if (r.Read(out c2, out v2) && c2 == "2") table = v2;
                        continue;
                    }
                    if (code == "0" && value == "ENDTAB") { table = null; continue; }

                    if (code == "0" && IsPurgeableTable(table) && value == table)
                    {
                        string name = null, nc, nv;
                        while (r.Read(out nc, out nv))
                        {
                            if (nc == "0") { r.PushBack(nc, nv); break; }
                            if (nc == "2" && name == null) name = nv;
                        }
                        if (!KeepTableRecord(table, name, layers, ltypes, styles, blocks, stat))
                            return true;
                    }
                }
            }
            return false;
        }

        // -------------------------------------------------------------------------
        // Проход 2: потоковая перезапись с отбрасыванием неиспользуемых записей
        // -------------------------------------------------------------------------
        private static void Rewrite(string inPath, string outPath,
            HashSet<string> layers, HashSet<string> ltypes,
            HashSet<string> styles, HashSet<string> blocks, PurgeStat stat)
        {
            using (var r = new PairReader(inPath, Raw))
            using (var w = new StreamWriter(outPath, false, Raw))
            {
                string section = null;
                string table = null;
                string code, value;

                while (r.Read(out code, out value))
                {
                    if (code == "0" && value == "SECTION")
                    {
                        WritePair(w, code, value);
                        string c2, v2;
                        if (r.Read(out c2, out v2))
                        {
                            if (c2 == "2") section = v2;
                            WritePair(w, c2, v2);
                        }
                        table = null;
                        continue;
                    }

                    if (code == "0" && value == "ENDSEC")
                    {
                        section = null; table = null;
                        WritePair(w, code, value);
                        continue;
                    }

                    // --- TABLES: отслеживаем, какая таблица открыта ---
                    if (section == "TABLES" && code == "0" && value == "TABLE")
                    {
                        WritePair(w, code, value);
                        string c2, v2;
                        if (r.Read(out c2, out v2))
                        {
                            if (c2 == "2") table = v2;
                            WritePair(w, c2, v2);
                        }
                        continue;
                    }
                    if (section == "TABLES" && code == "0" && value == "ENDTAB")
                    {
                        table = null;
                        WritePair(w, code, value);
                        continue;
                    }

                    // --- Запись таблицы: буферизуем и решаем, оставлять ли ---
                    if (section == "TABLES" && code == "0" && IsPurgeableTable(table) && value == table)
                    {
                        var rec = new List<string[]>();
                        string name = null;
                        string nc, nv;
                        string pendingCode = null, pendingValue = null;

                        while (r.Read(out nc, out nv))
                        {
                            if (nc == "0") { pendingCode = nc; pendingValue = nv; break; }
                            if (nc == "2" && name == null) name = nv;
                            rec.Add(new[] { nc, nv });
                        }

                        if (KeepTableRecord(table, name, layers, ltypes, styles, blocks, stat))
                        {
                            WritePair(w, code, value);
                            foreach (var p in rec) WritePair(w, p[0], p[1]);
                        }

                        if (pendingCode != null)
                        {
                            // повторно обрабатываем встреченный маркер "0"
                            if (pendingValue == "ENDTAB") { table = null; WritePair(w, pendingCode, pendingValue); }
                            else if (pendingValue == "ENDSEC") { section = null; table = null; WritePair(w, pendingCode, pendingValue); }
                            else if (IsPurgeableTable(table) && pendingValue == table)
                            {
                                // следующая запись той же таблицы — вернём её в цикл
                                r.PushBack(pendingCode, pendingValue);
                            }
                            else WritePair(w, pendingCode, pendingValue);
                        }
                        continue;
                    }

                    // --- BLOCKS: определение блока целиком ---
                    if (section == "BLOCKS" && code == "0" && value == "BLOCK")
                    {
                        var body = new List<string[]>();
                        string name = null;
                        string nc, nv;

                        while (r.Read(out nc, out nv))
                        {
                            body.Add(new[] { nc, nv });
                            if (nc == "2" && name == null) name = nv;
                            if (nc == "0" && nv == "ENDBLK")
                            {
                                // дочитываем хвост ENDBLK до следующего "0"
                                string tc, tv;
                                while (r.Read(out tc, out tv))
                                {
                                    if (tc == "0") { r.PushBack(tc, tv); break; }
                                    body.Add(new[] { tc, tv });
                                }
                                break;
                            }
                        }

                        bool keep = !Scope.Blocks || name == null || name.StartsWith("*") || blocks.Contains(name);
                        if (keep)
                        {
                            WritePair(w, code, value);
                            foreach (var p in body) WritePair(w, p[0], p[1]);
                        }
                        else stat.Blocks++;
                        continue;
                    }

                    WritePair(w, code, value);
                }

                w.Flush();
            }
        }

        private static bool IsPurgeableTable(string table)
        {
            if (table != "LAYER" && table != "LTYPE" && table != "STYLE" && table != "BLOCK_RECORD")
                return false;
            return Scope.Allows(table);
        }

        private static bool KeepTableRecord(string table, string name,
            HashSet<string> layers, HashSet<string> ltypes,
            HashSet<string> styles, HashSet<string> blocks, PurgeStat stat)
        {
            if (string.IsNullOrEmpty(name)) return true; // без имени — не трогаем

            switch (table)
            {
                case "LAYER":
                    if (layers.Contains(name)) return true;
                    stat.Layers++; return false;
                case "LTYPE":
                    if (ltypes.Contains(name)) return true;
                    stat.Ltypes++; return false;
                case "STYLE":
                    if (styles.Contains(name)) return true;
                    stat.Styles++; return false;
                case "BLOCK_RECORD":
                    if (name.StartsWith("*") || blocks.Contains(name)) return true;
                    stat.BlockRecords++; return false;
            }
            return true;
        }

        // -------------------------------------------------------------------------
        // Потоковый читатель пар "код / значение" с возвратом одной пары назад
        // -------------------------------------------------------------------------
        private class PairReader : IDisposable
        {
            private readonly StreamReader _r;
            private string _pbCode, _pbValue;

            public PairReader(string path, Encoding enc)
            {
                _r = new StreamReader(path, enc);
            }

            public void PushBack(string code, string value)
            {
                _pbCode = code; _pbValue = value;
            }

            public bool Read(out string code, out string value)
            {
                if (_pbCode != null)
                {
                    code = _pbCode; value = _pbValue;
                    _pbCode = null; _pbValue = null;
                    return true;
                }

                code = null; value = null;
                string a = _r.ReadLine();
                if (a == null) return false;
                string b = _r.ReadLine();
                code = a.Trim();
                value = b ?? "";
                return true;
            }

            public void Dispose() { _r.Dispose(); }
        }

        private static void WritePair(StreamWriter w, string code, string value)
        {
            w.Write(code); w.Write("\r\n");
            w.Write(value); w.Write("\r\n");
        }

        // -------------------------------------------------------------------------
        // Пакетная чистка всех DXF в папке
        // -------------------------------------------------------------------------
        public static string PurgeFolder(string folder, bool recursive = false)
        {
            if (!Directory.Exists(folder))
                return "[CadPurger] Папка не найдена: " + folder;

            var sb = new StringBuilder();
            int n = 0;
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (string f in Directory.GetFiles(folder, "*.dxf", opt))
            {
                sb.AppendLine(Purge(f));
                n++;
            }
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "[CadPurger] Обработано файлов: {0}", n));
            return sb.ToString();
        }
    }
}
