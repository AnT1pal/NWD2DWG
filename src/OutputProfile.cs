// ============================================================================
//  OutputProfile.cs — профиль выдачи результатов
//  NWD2DWG | namespace NWD2DWG.Plugin
//
//  Отвечает на вопросы «куда положить», «как назвать», «в каком формате»
//  и «какие колонки включить» для всех побочных файлов конвертации.
//
//  Хранится и переносится вместе с допусками расчёта: шаблон «под задачу»
//  описывает не только как считать, но и как оформить результат.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace NWD2DWG.Plugin
{
    public class OutputProfile
    {
        // --- 1. Размещение --------------------------------------------------
        // Пусто = рядом с основным выходным файлом. Иначе — путь, в котором
        // допустимы те же подстановки, что и в шаблоне имени.
        public string OutputRoot = "";
        public bool   UseFolders = false;
        public string FolderGeometry = "01_Геометрия";
        public string FolderReports = "02_Ведомости";
        public string FolderCoordination = "03_Координация";
        public string FolderProtocol = "04_Протокол";

        // --- 2. Именование --------------------------------------------------
        // Подстановки: {base} {model} {code} {mark} {date} {suffix}
        public string ProjectCode = "";              // шифр объекта, напр. 2451-14
        public string DocMark = "";                  // марка комплекта, напр. КМ
        public string NamePattern = "{base}{suffix}";

        // --- 3. Формат ведомостей -------------------------------------------
        public string ReportFormat = "Csv";          // Csv | Xlsx | Both
        public string CsvSeparator = ";";
        public string CsvEncoding = "UTF-8";         // UTF-8 | Windows-1251
        public string DecimalSeparator = ",";        // для Excel в русской локали
        public bool   CsvSepHint = true;             // строка sep=; в начале файла

        // --- 4. Состав выдачи -----------------------------------------------
        public bool EmitAuxDxf = true;               // _pipes, _plan, _openings, _clearance...
        public bool EmitReports = true;              // ведомости CSV / XLSX
        public bool EmitProtocol = true;             // манифест прогона и опись файлов
        public bool EmitLogCopy = true;              // копия журнала рядом с результатом

        // Основная геометрия. Сметчику она не нужна: на реальной модели это
        // 2.7 ГБ и лишние минуты прогона ради файла, который никто не откроет.
        // Ведомости и протокол при этом считаются полностью — обход модели
        // идёт как обычно, не пишется только сам чертёж.
        public bool EmitGeometry = true;

        // --- 4б. Прослеживаемость -------------------------------------------
        public bool   EmitIndex = true;            // индекс ревизии для сравнения выдач
        public bool   AutoDiff = true;             // сравнить с предыдущей выдачей автоматически
        public double DiffToleranceMm = 5.0;       // ниже — элемент считается неизменным
        public double DiffTriTolerancePct = 2.0;   // порог изменения числа треугольников
        public string DeliveryLogPath = "";        // пусто = журнал рядом с выдачей

        // --- 5. Колонки ведомостей ------------------------------------------
        public bool BoqColCount = true, BoqColArea = true, BoqColVolume = true, BoqColMass = true;
        public bool CogColFragments = true, CogColDensity = true, CogColVolume = true, CogColCog = true;
        public bool SteelColGost = true, SteelColLength = true, SteelColMassPerM = true;

        // --------------------------------------------------------------------
        // Разрешение путей и имён
        // --------------------------------------------------------------------
        public enum Kind { Geometry, Report, Coordination, Protocol }

        /// <summary>Подставляет токены в строку шаблона.</summary>
        public string Expand(string pattern, string baseName, string modelName, string suffix)
        {
            string s = pattern ?? "";
            s = s.Replace("{base}", baseName ?? "");
            s = s.Replace("{model}", modelName ?? "");
            s = s.Replace("{code}", ProjectCode ?? "");
            s = s.Replace("{mark}", DocMark ?? "");
            s = s.Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            s = s.Replace("{suffix}", suffix ?? "");
            // подряд идущие разделители после пустых подстановок схлопываем
            while (s.Contains("--")) s = s.Replace("--", "-");
            while (s.Contains("__")) s = s.Replace("__", "_");
            return s.Trim(' ', '-', '_');
        }

        private string FolderFor(Kind kind)
        {
            if (!UseFolders) return "";
            switch (kind)
            {
                case Kind.Report:       return FolderReports;
                case Kind.Coordination: return FolderCoordination;
                case Kind.Protocol:     return FolderProtocol;
                default:                return FolderGeometry;
            }
        }

        /// <summary>
        /// Полный путь побочного файла. defaultDir — папка основного результата,
        /// suffix — например «_boq», ext — «.csv».
        /// </summary>
        public string ResolvePath(Kind kind, string defaultDir, string baseName,
                                  string modelName, string suffix, string ext)
        {
            string root = string.IsNullOrEmpty(OutputRoot)
                ? defaultDir
                : Expand(OutputRoot, baseName, modelName, "");

            string sub = FolderFor(kind);
            if (!string.IsNullOrEmpty(sub)) root = Path.Combine(root, sub);

            try { if (!Directory.Exists(root)) Directory.CreateDirectory(root); }
            catch { root = defaultDir; }

            string name = Expand(NamePattern, baseName, modelName, suffix);
            if (string.IsNullOrEmpty(name)) name = baseName + suffix;
            return Path.Combine(root, SafeName(name) + ext);
        }

        public static string SafeName(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }

        // --------------------------------------------------------------------
        // Параметры записи ведомостей
        // --------------------------------------------------------------------
        public Encoding ReportEncoding
        {
            get
            {
                if (string.Equals(CsvEncoding, "Windows-1251", StringComparison.OrdinalIgnoreCase))
                {
                    try { return Encoding.GetEncoding(1251); } catch { }
                }
                return new UTF8Encoding(true);   // BOM: без него Excel не распознаёт кириллицу
            }
        }

        public string Sep { get { return string.IsNullOrEmpty(CsvSeparator) ? ";" : CsvSeparator; } }

        public bool WantsCsv  { get { return ReportFormat != "Xlsx"; } }
        public bool WantsXlsx { get { return ReportFormat == "Xlsx" || ReportFormat == "Both"; } }

        /// <summary>Число в виде, который понимает Excel при выбранном разделителе.</summary>
        public string Num(double v, int digits)
        {
            string s = v.ToString("F" + digits.ToString(CultureInfo.InvariantCulture),
                                  CultureInfo.InvariantCulture);
            return DecimalSeparator == "," ? s.Replace('.', ',') : s;
        }

        public string Cell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace(Sep, " ").Replace("\r", " ").Replace("\n", " ");
        }

        // --------------------------------------------------------------------
        // Приведение готовой ведомости к профилю.
        //
        // Модули пишут CSV в UTF-8 с разделителем «;» — это их внутренний
        // формат. Здесь файл один раз переписывается под выбранные кодировку,
        // разделители и набор колонок. Так профиль действует на все ведомости,
        // а не только на те, что пишет сам конвейер.
        // --------------------------------------------------------------------
        public void NormalizeReport(string path, params string[] dropColumnsContaining)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                string[] lines = File.ReadAllLines(path, new UTF8Encoding(true));
                if (lines.Length == 0) return;

                // строка-подсказка Excel в исходнике всегда первая, если есть
                var body = new List<string>();
                foreach (string l in lines)
                    if (!l.StartsWith("sep=", StringComparison.OrdinalIgnoreCase)) body.Add(l);
                if (body.Count == 0) return;

                // шапка — первая строка, где больше одной колонки
                int headIdx = -1;
                for (int i = 0; i < body.Count; i++)
                    if (body[i].Split(';').Length > 1) { headIdx = i; break; }

                var drop = new List<int>();
                int colCount = 0;
                if (headIdx >= 0 && dropColumnsContaining != null && dropColumnsContaining.Length > 0)
                {
                    string[] head = body[headIdx].Split(';');
                    colCount = head.Length;
                    for (int c = 0; c < head.Length; c++)
                        foreach (string token in dropColumnsContaining)
                            if (!string.IsNullOrEmpty(token) &&
                                head[c].IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                            { drop.Add(c); break; }
                }

                var sb = new StringBuilder();
                if (CsvSepHint) sb.AppendLine("sep=" + Sep);

                foreach (string line in body)
                {
                    string[] cells = line.Split(';');
                    // строки-заголовки и подписи не трогаем
                    if (cells.Length <= 1) { sb.AppendLine(line); continue; }

                    var keep = new List<string>(cells.Length);
                    for (int c = 0; c < cells.Length; c++)
                    {
                        if (colCount > 0 && cells.Length == colCount && drop.Contains(c)) continue;
                        keep.Add(FixDecimal(cells[c]));
                    }
                    sb.AppendLine(string.Join(Sep, keep.ToArray()));
                }

                File.WriteAllText(path, sb.ToString(), ReportEncoding);
            }
            catch { }
        }

        // Меняет разделитель дробной части только в ячейках, которые целиком
        // являются числом: текст и обозначения профилей не затрагиваются.
        private string FixDecimal(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return cell;
            string s = cell.Trim();
            if (s.Length == 0) return cell;

            bool hasDigit = false, hasDot = false, hasComma = false;
            foreach (char ch in s)
            {
                if (char.IsDigit(ch)) { hasDigit = true; continue; }
                if (ch == '.') { hasDot = true; continue; }
                if (ch == ',') { hasComma = true; continue; }
                if (ch == '-' || ch == '+' || ch == ' ') continue;
                return cell;   // есть буквы или знаки — это не число
            }
            if (!hasDigit) return cell;

            if (DecimalSeparator == "," && hasDot && !hasComma) return s.Replace('.', ',');
            if (DecimalSeparator == "." && hasComma && !hasDot) return s.Replace(',', '.');
            return cell;
        }

        // --------------------------------------------------------------------
        // Хранилище
        // --------------------------------------------------------------------
        public static string DefaultFile
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NWD2DWG");
                if (!Directory.Exists(dir)) { try { Directory.CreateDirectory(dir); } catch { } }
                return Path.Combine(dir, "output.json");
            }
        }

        public static OutputProfile Load() { return LoadFrom(DefaultFile); }

        public static OutputProfile LoadFrom(string path)
        {
            var p = new OutputProfile();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return p;
            try { p.ParseJson(File.ReadAllText(path, Encoding.UTF8)); }
            catch { }
            return p;
        }

        public void Save() { SaveTo(DefaultFile); }

        public void SaveTo(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static FieldInfo[] Fields
        {
            get { return typeof(OutputProfile).GetFields(BindingFlags.Public | BindingFlags.Instance); }
        }

        public string ToJson()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("{");
            var f = Fields;
            for (int i = 0; i < f.Length; i++)
            {
                object v = f[i].GetValue(this);
                string s;
                if (v is bool) s = ((bool)v) ? "true" : "false";
                else if (v is double) s = ((double)v).ToString("R", inv);
                else if (v is int) s = ((int)v).ToString(inv);
                else s = "\"" + Esc(Convert.ToString(v)) + "\"";
                sb.AppendLine("  \"" + f[i].Name + "\": " + s + (i < f.Length - 1 ? "," : ""));
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }

        public void ParseJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            var map = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
            foreach (var f in Fields) map[f.Name] = f;

            foreach (string raw in json.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim().TrimEnd(',');
                int idx = line.IndexOf(':');
                if (idx < 0) continue;
                string key = line.Substring(0, idx).Trim().Trim('"');
                string val = line.Substring(idx + 1).Trim();
                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
                    val = val.Substring(1, val.Length - 2);

                FieldInfo fi;
                if (!map.TryGetValue(key, out fi)) continue;
                try
                {
                    if (fi.FieldType == typeof(bool))
                    { bool b; if (bool.TryParse(val, out b)) fi.SetValue(this, b); }
                    else if (fi.FieldType == typeof(int))
                    { int n; if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) fi.SetValue(this, n); }
                    else if (fi.FieldType == typeof(double))
                    { double d; if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) fi.SetValue(this, d); }
                    else fi.SetValue(this, val.Replace("\\\"", "\"").Replace("\\\\", "\\"));
                }
                catch { }
            }
        }
    }
}
