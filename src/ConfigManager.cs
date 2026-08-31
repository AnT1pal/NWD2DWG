// ============================================================================
//  ConfigManager.cs — Расширенная конфигурация и диалог настроек модулей
//  NWD2DWG | namespace NWD2DWG.Plugin
//
//  Все линейные величины — в МИЛЛИМЕТРАХ, как и координаты модели.
//  Прежняя версия хранила часть допусков в метрах, а модули работали в
//  миллиметрах: при подключении настроек значения разъезжались в 1000 раз,
//  и заметить это глазом было невозможно.
//
//  Сериализация идёт через рефлексию по публичным полям: раньше каждое поле
//  нужно было руками добавить в ToJson, в ParseJson и в диалог — три места,
//  которые неизбежно расходились.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NWD2DWG.Plugin
{
    // ------------------------------------------------------------------------
    // Параметры инженерных модулей.
    // В классе остаются ТОЛЬКО те поля, которые реально потребляет код.
    // ------------------------------------------------------------------------
    public class AdvancedConfig
    {
        // --- 1. Коллизии (Clash Detective) и BCF ---------------------------
        public double ClashEpsilonMm = 1500.0;   // радиус окрестности DBSCAN
        public int    ClashMinPts = 2;           // мин. точек для ядра кластера
        public double ClashMinDistanceMm = 5.0;  // игнорировать пересечения мельче
        public bool   ClashIncludeResolved = false;
        public bool   ClashIncludeApproved = false;
        public string BcfAuthor = "NWD2DWG";

        // --- 2. 4D планирование --------------------------------------------
        public string ScheduleSource = "Timeliner"; // Timeliner | File
        public string ScheduleStatusDate = "";      // пусто = сегодня, иначе гггг-ММ-дд
        public bool   ScheduleOnlyLinked = true;    // только задачи с привязкой к элементам

        // --- 3. Высота проходов в свету -------------------------------------
        public double MinHeadroomCorridorMm = 2100.0; // СП 1.13130, пути эвакуации
        public double ClearanceCellMm = 500.0;        // шаг сетки проверки

        // --- 3б. Геопривязка -------------------------------------------------
        // Дребезг графики начинается на координатах порядка километра, а не
        // пяти метров: прежний порог 5000 мм срабатывал почти на любой модели
        // и двигал её без необходимости.
        public double GeoShiftThresholdMm = 100000.0;

        // --- 4. 2D планы и помещения ----------------------------------------
        // Отметка горизонтального среза. По умолчанию — АБСОЛЮТНАЯ отметка Z
        // в координатах выгрузки. Предварительный проход по матрицам фрагментов
        // даёт лишь оценку габаритов сверху, поэтому отсчёт «от низа модели»
        // включается отдельным флагом и годится только для аккуратных моделей.
        public double SectionCutHeightMm = 1200.0;
        public bool   SectionZFromModelBottom = false;
        public double SectionDpEpsMm = 5.0;        // упрощение Дугласа-Пекера
        public string SectionLayer = "_PLAN";
        public double RoomMinAreaM2 = 2.0;         // меньше — ниша, а не помещение
        public double RoomMaxAreaM2 = 2000.0;      // больше — внешний контур здания
        public double RoomHeightMm = 3000.0;       // высота, если её нет в модели
        public bool   RoomDeductOpenings = true;

        // --- 5. Трубы, гильзы, изометрия ------------------------------------
        public double PipeMinDiameterMm = 10.0;    // отсев ложных цилиндров
        public double PipeMaxDiameterMm = 2000.0;
        public double PipeMinLengthMm = 100.0;
        public double SleeveGapSmallMm = 30.0;     // DN < 50
        public double SleeveGapMediumMm = 50.0;    // 50 <= DN <= 200, СП 73
        public double SleeveGapLargeMm = 100.0;    // DN > 200
        public double SleeveExtensionMm = 50.0;    // выпуск за конструкцию
        public double SleeveMinStructureMm = 20.0; // тоньше — облицовка, не конструкция
        public double IsoJointToleranceMm = 2.0;   // допуск стыковки участков

        // --- 6. Металл и массы ----------------------------------------------
        public double SteelTolerancePct = 2.0;
        public double SteelMinLengthMm = 300.0;    // короче — не прокат
        public double SteelMinAspect = 3.0;        // отношение габаритов
        public double SteelMinConfidence = 0.5;
        // Не подошедший под сортамент прокат модуль помечает как
        // «Индивидуальный» с фиксированной достоверностью 0.5, поэтому такие
        // позиции проходили любой фильтр, а ужесточение допуска только
        // увеличивало их число. Управляем ими отдельным флагом.
        public bool   SteelIncludeCustom = true;
        public double DensitySteel = 7850.0;
        public double DensityConcrete = 2400.0;
        public double DensityAluminum = 2700.0;
        public double DensityInsulation = 150.0;
        public double DensityEquipment = 1500.0;
        public double DensityPiping = 4500.0;      // труба с водой, эквивалентная
        public double CogMinMassKg = 1.0;          // легче — не попадает в ведомость

        // --- 7. Геометрия: децимация, распознавание тел, оболочки -----------
        public int    DecimateMinTriangles = 12;   // мельче упрощать нечего
        public double DecimateBoundaryWeight = 1000.0; // вес закрепления границы
        public bool   DecimatePreventFlips = true; // защита от выворачивания граней
        public double SolidMinConfidence = 0.7;
        public int    ShrinkwrapLevel = 2;         // 1=полости, 2=OBB, 3=выпуклая оболочка

        // --- 8. ВОР и чистка CAD --------------------------------------------
        public string BoqGroupBy = "Element";      // Element | Layer | Material
        public double BoqMinVolumeM3 = 0.0;
        public bool   PurgeLayers = true;
        public bool   PurgeLinetypes = true;
        public bool   PurgeTextStyles = true;
        public bool   PurgeBlocks = true;

        // --------------------------------------------------------------------
        // Хранилище
        // --------------------------------------------------------------------
        public static string DefaultSettingsFile
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NWD2DWG");
                if (!Directory.Exists(dir)) { try { Directory.CreateDirectory(dir); } catch { } }
                return Path.Combine(dir, "settings.json");
            }
        }

        public static AdvancedConfig Load()
        {
            string path = DefaultSettingsFile;
            if (!File.Exists(path))
            {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
                if (File.Exists(local)) path = local;
            }
            return LoadFrom(path);
        }

        public static AdvancedConfig LoadFrom(string path)
        {
            var cfg = new AdvancedConfig();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return cfg;
            try { cfg.ParseJson(File.ReadAllText(path, Encoding.UTF8)); }
            catch { }
            return cfg;
        }

        public void Save() { SaveTo(DefaultSettingsFile); }

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

        // --------------------------------------------------------------------
        // JSON через рефлексию: одно поле — одна строка, руками ничего не ведём
        // --------------------------------------------------------------------
        private static FieldInfo[] Fields
        {
            get { return typeof(AdvancedConfig).GetFields(BindingFlags.Public | BindingFlags.Instance); }
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
                else s = "\"" + EscapeJson(Convert.ToString(v)) + "\"";
                sb.AppendLine("  \"" + f[i].Name + "\": " + s + (i < f.Length - 1 ? "," : ""));
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        // Переименования при переходе на миллиметры: старый settings.json
        // не должен молча обнуляться до значений по умолчанию.
        private static readonly Dictionary<string, string> LegacyKeys =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "ClashEpsilon",        "ClashEpsilonMm" },
            { "ClashTolerance",      "ClashMinDistanceMm" },
            { "MinHeadroomCorridor", "MinHeadroomCorridorMm" },
            { "MinHeadroomBasement", "MinHeadroomBasementMm" },
            { "SectionCutHeight",    "SectionCutHeightMm" },
            { "SectionDpEps",        "SectionDpEpsMm" },
            { "SleeveGapSmall",      "SleeveGapSmallMm" },
            { "SleeveGapMedium",     "SleeveGapMediumMm" },
            { "SleeveGapLarge",      "SleeveGapLargeMm" },
            { "SleeveExtension",     "SleeveExtensionMm" },
            { "RoomMinHeight",       "RoomHeightMm" },
        };

        private void ParseJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            var inv = CultureInfo.InvariantCulture;
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

                bool legacyMetres = false;
                string mapped;
                if (LegacyKeys.TryGetValue(key, out mapped))
                {
                    legacyMetres = mapped.EndsWith("Mm", StringComparison.Ordinal);
                    key = mapped;
                }

                FieldInfo fi;
                if (!map.TryGetValue(key, out fi)) continue;

                try
                {
                    if (fi.FieldType == typeof(bool))
                    {
                        bool b;
                        if (bool.TryParse(val, out b)) fi.SetValue(this, b);
                    }
                    else if (fi.FieldType == typeof(int))
                    {
                        int n;
                        if (int.TryParse(val, NumberStyles.Integer, inv, out n)) fi.SetValue(this, n);
                    }
                    else if (fi.FieldType == typeof(double))
                    {
                        double d;
                        if (double.TryParse(val, NumberStyles.Float, inv, out d))
                            fi.SetValue(this, legacyMetres ? d * 1000.0 : d);
                    }
                    else
                    {
                        fi.SetValue(this, UnescapeJson(val));
                    }
                }
                catch { }
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        // --------------------------------------------------------------------
        // Единые точки принятия решений для всех модулей
        // --------------------------------------------------------------------
        public double DensityFor(string material)
        {
            switch ((material ?? "").Trim())
            {
                case "Concrete":   return DensityConcrete;
                case "Aluminum":   return DensityAluminum;
                case "Insulation": return DensityInsulation;
                case "Equipment":  return DensityEquipment;
                case "Piping":     return DensityPiping;
                default:           return DensitySteel;
            }
        }

        public double SleeveGapFor(double dnMm)
        {
            if (dnMm < 50.0) return SleeveGapSmallMm;
            if (dnMm <= 200.0) return SleeveGapMediumMm;
            return SleeveGapLargeMm;
        }

        public DateTime StatusDate()
        {
            DateTime d;
            if (!string.IsNullOrEmpty(ScheduleStatusDate) &&
                DateTime.TryParse(ScheduleStatusDate, CultureInfo.InvariantCulture,
                                  DateTimeStyles.None, out d)) return d;
            return DateTime.Now;
        }
    }

    // ------------------------------------------------------------------------
    // Шаблон допусков под типовую задачу.
    //
    // Значения — это типовые отправные точки для соответствующего вида работ,
    // а не цитаты пунктов нормативов. Нормы названы, чтобы было понятно, откуда
    // берётся ориентир, но проверка применимости к конкретному объекту
    // остаётся за проектировщиком.
    // ------------------------------------------------------------------------
    public class ConfigPreset
    {
        public string Name;
        public string Norms;
        public string Note;
        public bool BuiltIn = true;   // встроенные нельзя удалить или перезаписать
        public readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.Ordinal);

        public ConfigPreset Set(string field, object value) { Values[field] = value; return this; }

        // --------------------------------------------------------------------
        // Пользовательские шаблоны: по файлу на шаблон, чтобы обмен сводился
        // к копированию файла.
        // --------------------------------------------------------------------
        public static string UserDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NWD2DWG", "presets");
                if (!Directory.Exists(dir)) { try { Directory.CreateDirectory(dir); } catch { } }
                return dir;
            }
        }

        // Снимок текущих настроек целиком: допуски расчёта плюс профиль выдачи
        public static ConfigPreset FromConfig(string name, string note, AdvancedConfig cfg)
        {
            return FromConfig(name, note, cfg, null);
        }

        public static ConfigPreset FromConfig(string name, string note,
                                              AdvancedConfig cfg, OutputProfile outp)
        {
            var p = new ConfigPreset { Name = name, Note = note ?? "", Norms = "Пользовательский шаблон", BuiltIn = false };
            foreach (var f in typeof(AdvancedConfig).GetFields(BindingFlags.Public | BindingFlags.Instance))
                p.Values[f.Name] = f.GetValue(cfg);
            if (outp != null)
                foreach (var f in OutputProfile.Fields)
                    p.Values[OutPrefix + f.Name] = f.GetValue(outp);
            return p;
        }

        public string ToJson()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("{");
            // служебные ключи начинаются с $ — AdvancedConfig их игнорирует
            sb.AppendLine("  \"$name\": \"" + Esc(Name) + "\",");
            sb.AppendLine("  \"$note\": \"" + Esc(Note) + "\",");
            var keys = new List<string>(Values.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                object v = Values[keys[i]];
                string s;
                if (v is bool) s = ((bool)v) ? "true" : "false";
                else if (v is double) s = ((double)v).ToString("R", inv);
                else if (v is int) s = ((int)v).ToString(inv);
                else s = "\"" + Esc(Convert.ToString(v)) + "\"";
                sb.AppendLine("  \"" + keys[i] + "\": " + s + (i < keys.Count - 1 ? "," : ""));
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }

        public static ConfigPreset FromJson(string json, string fallbackName)
        {
            var p = new ConfigPreset { Name = fallbackName, Note = "", Norms = "Пользовательский шаблон", BuiltIn = false };
            var map = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
            foreach (var f in typeof(AdvancedConfig).GetFields(BindingFlags.Public | BindingFlags.Instance))
                map[f.Name] = f;

            foreach (string raw in (json ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim().TrimEnd(',');
                int idx = line.IndexOf(':');
                if (idx < 0) continue;
                string key = line.Substring(0, idx).Trim().Trim('"');
                string val = line.Substring(idx + 1).Trim();
                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
                    val = val.Substring(1, val.Length - 2);

                if (key == "$name") { if (!string.IsNullOrEmpty(val)) p.Name = val; continue; }
                if (key == "$note") { p.Note = val; continue; }

                FieldInfo fi = null;
                if (key.StartsWith(OutPrefix, StringComparison.Ordinal))
                    fi = typeof(OutputProfile).GetField(key.Substring(OutPrefix.Length));
                else
                    map.TryGetValue(key, out fi);
                if (fi == null) continue;
                try
                {
                    if (fi.FieldType == typeof(bool))
                    { bool b; if (bool.TryParse(val, out b)) p.Values[key] = b; }
                    else if (fi.FieldType == typeof(int))
                    { int n; if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) p.Values[key] = n; }
                    else if (fi.FieldType == typeof(double))
                    { double d; if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) p.Values[key] = d; }
                    else p.Values[key] = val;
                }
                catch { }
            }
            return p.Values.Count > 0 ? p : null;
        }

        // Имя шаблона становится именем файла — вычищаем недопустимые символы
        public static string SafeFileName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            string s = sb.ToString().Trim();
            return string.IsNullOrEmpty(s) ? "preset" : s;
        }

        public string UserPath { get { return Path.Combine(UserDir, SafeFileName(Name) + ".json"); } }

        public void SaveAsUser()
        {
            BuiltIn = false;
            File.WriteAllText(UserPath, ToJson(), new UTF8Encoding(false));
            _all = null;   // список пересоберётся при следующем обращении
        }

        public void ExportTo(string path)
        {
            File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
        }

        public static ConfigPreset ImportFrom(string path)
        {
            var p = FromJson(File.ReadAllText(path, Encoding.UTF8),
                             Path.GetFileNameWithoutExtension(path));
            if (p == null) return null;
            p.SaveAsUser();
            return p;
        }

        public static bool DeleteUser(string name)
        {
            var p = ByName(name);
            if (p == null || p.BuiltIn) return false;
            try { File.Delete(p.UserPath); } catch { return false; }
            _all = null;
            return true;
        }

        public static void Refresh() { _all = null; }

        // Поля профиля выдачи хранятся с префиксом out.
        public const string OutPrefix = "out.";

        // Перегрузки с одним аргументом больше нет намеренно. Она принимала
        // шаблон целиком, а применяла только половину — поля профиля выдачи
        // молча отбрасывались. Из-за неё все три шаблона «Выдача: …» ничего
        // не делали при запуске из командной строки, и заметить это было
        // нечем: программа отвечала «Применён шаблон», а выдача не менялась.
        public void ApplyTo(AdvancedConfig cfg, OutputProfile outp)
        {
            foreach (var kv in Values)
            {
                bool isOut = kv.Key.StartsWith(OutPrefix, StringComparison.Ordinal);
                object target = isOut ? (object)outp : cfg;
                if (target == null) continue;

                string name = isOut ? kv.Key.Substring(OutPrefix.Length) : kv.Key;
                FieldInfo fi = target.GetType().GetField(name);
                if (fi == null) continue;
                try { fi.SetValue(target, Convert.ChangeType(kv.Value, fi.FieldType, CultureInfo.InvariantCulture)); }
                catch { }
            }
        }

        public ConfigPreset SetOut(string field, object value)
        {
            Values[OutPrefix + field] = value;
            return this;
        }

        // --------------------------------------------------------------------
        private static List<ConfigPreset> _all;

        public static List<ConfigPreset> All
        {
            get
            {
                if (_all == null)
                {
                    _all = Build();
                    _all.AddRange(LoadUser());
                }
                return _all;
            }
        }

        public static List<ConfigPreset> BuiltInOnly { get { return Build(); } }

        public static List<ConfigPreset> LoadUser()
        {
            var res = new List<ConfigPreset>();
            try
            {
                foreach (string f in Directory.GetFiles(UserDir, "*.json"))
                {
                    try
                    {
                        var p = FromJson(File.ReadAllText(f, Encoding.UTF8),
                                         Path.GetFileNameWithoutExtension(f));
                        if (p != null) res.Add(p);
                    }
                    catch { }
                }
            }
            catch { }
            res.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return res;
        }

        public static ConfigPreset ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var p in All)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        private static List<ConfigPreset> Build()
        {
            var list = new List<ConfigPreset>();

            list.Add(new ConfigPreset
            {
                Name = "Жилые и общественные здания",
                Norms = "СП 54.13330, СП 118.13330, СП 1.13130, ГОСТ 21.501",
                Note = "Плотная инженерия, невысокие этажи, много помещений."
            }
                .Set("MinHeadroomCorridorMm", 2000.0)   // высота путей эвакуации в свету
                .Set("ClearanceCellMm", 300.0)
                .Set("ClashEpsilonMm", 800.0)
                .Set("ClashMinDistanceMm", 5.0)
                .Set("SectionZFromModelBottom", true)
                .Set("SectionCutHeightMm", 1200.0)      // план этажа по ГОСТ 21.501
                .Set("SectionDpEpsMm", 5.0)
                .Set("RoomMinAreaM2", 2.0)
                .Set("RoomMaxAreaM2", 500.0)
                .Set("RoomHeightMm", 2700.0)
                .Set("RoomDeductOpenings", true)
                .Set("PipeMaxDiameterMm", 600.0)
                .Set("BoqGroupBy", "Element")
                .SetOut("DocMark", "АР")
                .SetOut("ReportFormat", "Csv")
                .SetOut("UseFolders", true));

            list.Add(new ConfigPreset
            {
                Name = "Производственные здания и цеха",
                Norms = "СП 56.13330, СП 1.13130, СП 4.13130",
                Note = "Большие пролёты и высоты, крупные воздуховоды."
            }
                .Set("MinHeadroomCorridorMm", 2000.0)
                .Set("ClearanceCellMm", 500.0)
                .Set("ClashEpsilonMm", 1500.0)
                .Set("SectionZFromModelBottom", true)
                .Set("SectionCutHeightMm", 1500.0)
                .Set("SectionDpEpsMm", 10.0)
                .Set("RoomMinAreaM2", 6.0)
                .Set("RoomMaxAreaM2", 5000.0)
                .Set("RoomHeightMm", 6000.0)
                .Set("PipeMaxDiameterMm", 1600.0)
                .Set("SleeveMinStructureMm", 50.0)
                .Set("BoqGroupBy", "Element")
                .SetOut("DocMark", "ТХ")
                .SetOut("UseFolders", true));

            list.Add(new ConfigPreset
            {
                Name = "Технологические трубопроводы",
                Norms = "СП 73.13330, ГОСТ 21.605, ГОСТ 32388, ГОСТ 2.317",
                Note = "Трассировка, изометрии, гильзы. Ведомость по материалам."
            }
                .Set("PipeMinDiameterMm", 15.0)
                .Set("PipeMaxDiameterMm", 1400.0)
                .Set("PipeMinLengthMm", 150.0)
                .Set("SleeveGapSmallMm", 30.0)
                .Set("SleeveGapMediumMm", 50.0)
                .Set("SleeveGapLargeMm", 100.0)
                .Set("SleeveExtensionMm", 50.0)
                .Set("SleeveMinStructureMm", 50.0)
                .Set("IsoJointToleranceMm", 2.0)
                .Set("SolidMinConfidence", 0.65)
                .Set("ClashEpsilonMm", 600.0)
                .Set("DensityPiping", 4500.0)
                .Set("BoqGroupBy", "Material")
                .SetOut("DocMark", "ТХ")
                .SetOut("NamePattern", "{code}-{mark}{suffix}")
                .SetOut("UseFolders", true));

            list.Add(new ConfigPreset
            {
                Name = "Металлоконструкции КМ / КМД",
                Norms = "СП 16.13330, ГОСТ 23118, ГОСТ 21.502, сортамент ГОСТ 8240 / 26020 / 8509 / 30245",
                Note = "Только позиции по сортаменту, массы для строповки."
            }
                .Set("SteelTolerancePct", 3.0)
                .Set("SteelMinLengthMm", 500.0)
                .Set("SteelMinAspect", 4.0)
                .Set("SteelMinConfidence", 0.6)
                .Set("SteelIncludeCustom", false)
                .Set("DensitySteel", 7850.0)
                .Set("CogMinMassKg", 5.0)
                .Set("DecimateMinTriangles", 24)
                .Set("DecimatePreventFlips", true)
                .Set("BoqGroupBy", "Element")
                .SetOut("DocMark", "КМ")
                .SetOut("NamePattern", "{code}-{mark}{suffix}")
                .SetOut("UseFolders", true)
                .SetOut("EmitAuxDxf", false));

            list.Add(new ConfigPreset
            {
                Name = "Наружные сети и генплан",
                Norms = "СП 42.13330, СП 47.13330, ГОСТ 21.508",
                Note = "Модель в геодезических координатах: сдвиг к нулю обязателен."
            }
                .Set("GeoShiftThresholdMm", 50000.0)
                .Set("ClashEpsilonMm", 3000.0)
                .Set("SectionZFromModelBottom", true)
                .Set("SectionCutHeightMm", 500.0)
                .Set("SectionDpEpsMm", 20.0)
                .Set("PipeMinDiameterMm", 50.0)
                .Set("PipeMaxDiameterMm", 2000.0)
                .Set("PipeMinLengthMm", 500.0)
                .Set("RoomMinAreaM2", 20.0)
                .Set("RoomMaxAreaM2", 50000.0)
                .SetOut("DocMark", "ГП")
                .SetOut("UseFolders", true));

            list.Add(new ConfigPreset
            {
                Name = "Обмерные и сканированные модели",
                Norms = "ГОСТ Р 57563 / ИСО 19650 (информационное моделирование)",
                Note = "Грязная геометрия: допуски ослаблены, распознавание мягче."
            }
                .Set("SolidMinConfidence", 0.5)
                .Set("SteelTolerancePct", 8.0)
                .Set("SteelIncludeCustom", true)
                .Set("SteelMinConfidence", 0.4)
                .Set("SectionDpEpsMm", 20.0)
                .Set("ClashMinDistanceMm", 20.0)
                .Set("ClashEpsilonMm", 2000.0)
                .Set("DecimateBoundaryWeight", 2000.0)
                .Set("DecimatePreventFlips", true)
                .Set("DecimateMinTriangles", 24)
                .SetOut("UseFolders", false)
                .SetOut("EmitProtocol", true));

            // Профили, меняющие только выдачу: расчёт не трогают
            list.Add(new ConfigPreset
            {
                Name = "Выдача: комплект по СПДС",
                Norms = "ГОСТ Р 21.101",
                Note = "Папки по разделам, имена по шифру и марке комплекта."
            }
                .SetOut("UseFolders", true)
                .SetOut("NamePattern", "{code}-{mark}{suffix}")
                .SetOut("EmitProtocol", true)
                .SetOut("EmitLogCopy", true));

            list.Add(new ConfigPreset
            {
                Name = "Выдача: только ведомости сметчику",
                Norms = "ГОСТ 21.110",
                Note = "Без вспомогательных DXF, кодировка под русский Excel."
            }
                .SetOut("EmitAuxDxf", false)
                .SetOut("EmitReports", true)
                .SetOut("UseFolders", false)
                .SetOut("CsvEncoding", "Windows-1251")
                .SetOut("DecimalSeparator", ",")
                .SetOut("EmitGeometry", false));

            list.Add(new ConfigPreset
            {
                Name = "Выдача: плоская, для скриптов",
                Norms = "—",
                Note = "Всё рядом, без папок и протокола: удобно для автоматизации."
            }
                .SetOut("UseFolders", false)
                .SetOut("EmitProtocol", false)
                .SetOut("EmitLogCopy", false)
                .SetOut("CsvEncoding", "UTF-8")
                .SetOut("DecimalSeparator", ".")
                .SetOut("CsvSepHint", false));

            return list;
        }
    }

    // ------------------------------------------------------------------------
    // Тема оформления MultiCAD для диалогов
    // ------------------------------------------------------------------------
    public static class CadTheme
    {
        public static readonly Color ColBg = Color.FromArgb(33, 37, 43);             // #21252B
        public static readonly Color ColHeader = Color.FromArgb(44, 48, 56);         // #2C3038
        public static readonly Color ColPanel = Color.FromArgb(42, 46, 53);          // #2A2E35
        public static readonly Color ColPanelHeader = Color.FromArgb(55, 60, 68);    // #373C44
        public static readonly Color ColBorder = Color.FromArgb(76, 82, 92);         // #4C525C
        public static readonly Color ColBorderDark = Color.FromArgb(44, 50, 61);     // #2C323D
        public static readonly Color ColSeparator = Color.FromArgb(71, 77, 87);      // #474D57
        public static readonly Color ColInput = Color.FromArgb(20, 22, 26);          // #14161A
        public static readonly Color ColAccent = Color.FromArgb(60, 143, 212);       // #3C8FD4
        public static readonly Color ColCyan = Color.FromArgb(111, 212, 245);        // #6FD4F5
        public static readonly Color ColText = Color.FromArgb(230, 232, 235);        // #E6E8EB
        public static readonly Color ColTextMuted = Color.FromArgb(154, 160, 172);   // #9AA0AC
        public static readonly Color ColBtnPrimary = Color.FromArgb(14, 94, 158);    // #0E5E9E
        public static readonly Color ColBtnPrimaryHover = Color.FromArgb(20, 115, 190);
        public static readonly Color ColBtnSec = Color.FromArgb(55, 60, 68);
        public static readonly Color ColBtnSecHover = Color.FromArgb(76, 82, 92);
        public static readonly Color ColHoverBg = Color.FromArgb(53, 59, 69);
        public static readonly Color ColChecked = Color.FromArgb(19, 78, 140);
        public static readonly Color ColOk = Color.FromArgb(106, 190, 120);        // #6ABE78
        public static readonly Color ColErr = Color.FromArgb(224, 108, 117);       // #E06C75

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void ApplyDwmDarkTheme(Form form)
        {
            try
            {
                if (Environment.OSVersion.Version.Major >= 10 && form.IsHandleCreated)
                {
                    int dark = 1;
                    DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
                    DwmSetWindowAttribute(form.Handle, 19, ref dark, sizeof(int));
                    int borderCol = 0x5C524C;
                    DwmSetWindowAttribute(form.Handle, 34, ref borderCol, sizeof(int));
                    int captionCol = 0x38302C;
                    DwmSetWindowAttribute(form.Handle, 35, ref captionCol, sizeof(int));
                    int textCol = 0xEBE8E6;
                    DwmSetWindowAttribute(form.Handle, 36, ref textCol, sizeof(int));
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------------
    // Однострочный ввод: в WinForms нет штатного InputBox
    // ------------------------------------------------------------------------
    public class PromptDialog : Form
    {
        private readonly TextBox _tb;
        public string Value { get { return _tb.Text.Trim(); } }

        public PromptDialog(string title, string prompt, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(420, 116);
            BackColor = CadTheme.ColBg;
            ForeColor = CadTheme.ColText;
            Font = new Font("Segoe UI", 9f);
            HandleCreated += (s, e) => CadTheme.ApplyDwmDarkTheme(this);

            Controls.Add(new Label
            {
                Text = prompt, ForeColor = CadTheme.ColText,
                AutoSize = true, Location = new Point(12, 14)
            });

            _tb = new TextBox
            {
                Text = initial ?? "",
                BackColor = CadTheme.ColInput, ForeColor = CadTheme.ColText,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(12, 38), Width = 396
            };
            Controls.Add(_tb);

            var ok = new Button
            {
                Text = "OK", DialogResult = DialogResult.OK,
                Location = new Point(232, 72), Size = new Size(84, 30),
                FlatStyle = FlatStyle.Flat, BackColor = CadTheme.ColBtnPrimary,
                ForeColor = Color.White, Cursor = Cursors.Hand
            };
            ok.FlatAppearance.BorderColor = CadTheme.ColAccent;

            var cancel = new Button
            {
                Text = "Отмена", DialogResult = DialogResult.Cancel,
                Location = new Point(324, 72), Size = new Size(84, 30),
                FlatStyle = FlatStyle.Flat, BackColor = CadTheme.ColBtnSec,
                ForeColor = CadTheme.ColText, Cursor = Cursors.Hand
            };
            cancel.FlatAppearance.BorderColor = CadTheme.ColBorder;

            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    // ------------------------------------------------------------------------
    // Диалог параметров модулей
    // ------------------------------------------------------------------------
    public class ModuleSettingsDialog : Form
    {
        private readonly AdvancedConfig _config;
        private readonly OutputProfile _out;

        // Каждому полю конфига соответствует ровно один контрол; связь ведётся
        // по имени поля, поэтому диалог и сериализация не могут разойтись.
        private readonly Dictionary<string, Control> _binds =
            new Dictionary<string, Control>(StringComparer.Ordinal);

        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };

        // Настройки ИИ живут отдельным файлом: в них есть секреты, и мешать
        // их с параметрами расчёта, которые передают друг другу коллеги,
        // нельзя — шаблон с чужим ключом уехал бы вместе с настройками.
        private readonly AiSettings _ai = AiSettings.Load();
        private readonly List<Control[]> _aiRows = new List<Control[]>();
        private CheckBox _aiOn, _aiLocal, _aiData;
        private TabControl _tabs;
        private NumericUpDown _aiMaxNames;
        private ComboBox _cbPreset;
        private Label _lbPresetNorms;
        private Button _btnPresetDel, _btnPresetExp;
        private bool _loadingPresets;

        private const string NoPreset = "— не применять —";
        private const string UserHeader = "———  мои шаблоны  ———";

        private Button PresetButton(string text, int w, Action onClick, string hint)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(w, 24),
                Margin = new Padding(0, 1, 4, 1),
                FlatStyle = FlatStyle.Flat,
                BackColor = CadTheme.ColBtnSec,
                ForeColor = CadTheme.ColText,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.25f)
            };
            b.FlatAppearance.BorderColor = CadTheme.ColBorder;
            b.Click += (s, e) => onClick();
            _tips.SetToolTip(b, hint);
            return b;
        }

        // Перечитывает список шаблонов и по возможности восстанавливает выбор
        private void ReloadPresets(string select)
        {
            _loadingPresets = true;
            ConfigPreset.Refresh();
            _cbPreset.Items.Clear();
            _cbPreset.Items.Add(NoPreset);
            foreach (var pr in ConfigPreset.All) if (pr.BuiltIn) _cbPreset.Items.Add(pr.Name);
            var user = new List<ConfigPreset>();
            foreach (var pr in ConfigPreset.All) if (!pr.BuiltIn) user.Add(pr);
            if (user.Count > 0)
            {
                _cbPreset.Items.Add(UserHeader);
                foreach (var pr in user) _cbPreset.Items.Add(pr.Name);
            }
            int idx = string.IsNullOrEmpty(select) ? 0 : _cbPreset.Items.IndexOf(select);
            _cbPreset.SelectedIndex = idx >= 0 ? idx : 0;
            _loadingPresets = false;
            UpdatePresetButtons();
        }

        private ConfigPreset SelectedPreset()
        {
            string name = Convert.ToString(_cbPreset.SelectedItem);
            if (string.IsNullOrEmpty(name) || name == NoPreset || name == UserHeader) return null;
            return ConfigPreset.ByName(name);
        }

        private void UpdatePresetButtons()
        {
            var pr = SelectedPreset();
            _btnPresetDel.Enabled = pr != null && !pr.BuiltIn;
            _btnPresetExp.Enabled = pr != null;
        }

        private void SavePresetAs()
        {
            var pr0 = SelectedPreset();
            using (var dlg = new PromptDialog("Сохранить шаблон",
                       "Имя шаблона (существующий с тем же именем будет перезаписан):",
                       pr0 != null && !pr0.BuiltIn ? pr0.Name : "Мой шаблон"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string name = dlg.Value;
                if (string.IsNullOrEmpty(name)) return;

                var clash = ConfigPreset.ByName(name);
                if (clash != null && clash.BuiltIn)
                {
                    MessageBox.Show(this,
                        "«" + name + "» — встроенный шаблон, его нельзя перезаписать.\n" +
                        "Выберите другое имя.",
                        "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                ReadFromUi(false);   // забираем то, что сейчас в форме
                try
                {
                    ConfigPreset.FromConfig(name, "Сохранено из окна параметров", _config, _out).SaveAsUser();
                    ReloadPresets(name);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Не удалось сохранить шаблон:\n" + ex.Message,
                        "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // Копия шаблона под новым именем: удобно взять встроенный за основу
        // и подправить под своё бюро, не теряя оригинал.
        private void ClonePreset()
        {
            var pr = SelectedPreset();
            if (pr == null)
            {
                MessageBox.Show(this, "Сначала выберите шаблон, который нужно скопировать.",
                    "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new PromptDialog("Дублировать шаблон", "Имя копии:", pr.Name + " (копия)"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string name = dlg.Value;
                if (string.IsNullOrEmpty(name)) return;

                var clash = ConfigPreset.ByName(name);
                if (clash != null && clash.BuiltIn)
                {
                    MessageBox.Show(this, "«" + name + "» — встроенный шаблон, выберите другое имя.",
                        "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    // копию делаем полным снимком: у встроенного шаблона задана
                    // лишь часть полей, остальные берём из значений по умолчанию
                    var cfg = new AdvancedConfig();
                    var outp = new OutputProfile();
                    pr.ApplyTo(cfg, outp);
                    ConfigPreset.FromConfig(name, "Копия шаблона «" + pr.Name + "»", cfg, outp).SaveAsUser();
                    ReloadPresets(name);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Не удалось создать копию:\n" + ex.Message,
                        "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void DeletePreset()
        {
            var pr = SelectedPreset();
            if (pr == null || pr.BuiltIn) return;
            if (MessageBox.Show(this, "Удалить шаблон «" + pr.Name + "»?",
                    "NWD2DWG", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (!ConfigPreset.DeleteUser(pr.Name))
                MessageBox.Show(this, "Не удалось удалить файл шаблона.",
                    "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ReloadPresets(null);
        }

        private void ImportPreset()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Импорт шаблона параметров",
                Filter = "Шаблон NWD2DWG (*.json)|*.json|Все файлы (*.*)|*.*"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var pr = ConfigPreset.ImportFrom(dlg.FileName);
                    if (pr == null)
                    {
                        MessageBox.Show(this,
                            "В файле не найдено ни одного известного параметра.\n" +
                            "Похоже, это не шаблон NWD2DWG.",
                            "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    ReloadPresets(pr.Name);
                    MessageBox.Show(this,
                        "Шаблон «" + pr.Name + "» добавлен (" + pr.Values.Count + " параметров).",
                        "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Не удалось прочитать файл:\n" + ex.Message,
                        "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ExportPreset()
        {
            var pr = SelectedPreset();
            if (pr == null) return;
            using (var dlg = new SaveFileDialog
            {
                Title = "Экспорт шаблона параметров",
                Filter = "Шаблон NWD2DWG (*.json)|*.json",
                FileName = ConfigPreset.SafeFileName(pr.Name) + ".json"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    // встроенный шаблон задаёт лишь часть полей — выгружаем
                    // полный снимок, иначе на другой машине результат разойдётся
                    var cfg = new AdvancedConfig();
                    var outp = new OutputProfile();
                    pr.ApplyTo(cfg, outp);
                    ConfigPreset.FromConfig(pr.Name, pr.Note, cfg, outp).ExportTo(dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Не удалось сохранить файл:\n" + ex.Message,
                        "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ApplyPreset()
        {
            if (_loadingPresets) return;
            UpdatePresetButtons();

            string sel = Convert.ToString(_cbPreset.SelectedItem);
            if (sel == UserHeader) { _cbPreset.SelectedIndex = 0; return; }

            if (_cbPreset.SelectedIndex <= 0)
            {
                _lbPresetNorms.Text = "Выберите шаблон, чтобы подставить типовые допуски";
                return;
            }
            var pr = ConfigPreset.ByName(sel);
            if (pr == null) return;

            // читаем текущие значения, накладываем шаблон, возвращаем в форму:
            // параметры, которых шаблон не касается, остаются как были
            ReadFromUi(false);
            pr.ApplyTo(_config, _out);
            ApplyToUi(_config);
            _lbPresetNorms.Text = (pr.BuiltIn ? "Ориентир: " : "Мой шаблон · ") + pr.Norms
                                + (string.IsNullOrEmpty(pr.Note) ? "" : " · " + pr.Note);
            _tips.SetToolTip(_lbPresetNorms, pr.BuiltIn
                ? pr.Note + Environment.NewLine + "Значения типовые; применимость к объекту проверяет проектировщик."
                : "Пользовательский шаблон: " + ConfigPreset.UserDir);
        }

        public ModuleSettingsDialog(AdvancedConfig config)
            : this(config, null) { }

        public ModuleSettingsDialog(AdvancedConfig config, OutputProfile output)
        {
            _config = config ?? new AdvancedConfig();
            _out = output ?? OutputProfile.Load();
            InitUi();
        }

        // Поля профиля выдачи адресуются с префиксом out.
        private object TargetOf(string key)
        {
            return key.StartsWith(ConfigPreset.OutPrefix, StringComparison.Ordinal)
                ? (object)_out : _config;
        }

        private static FieldInfo FieldOf(object target, string key)
        {
            string name = key.StartsWith(ConfigPreset.OutPrefix, StringComparison.Ordinal)
                ? key.Substring(ConfigPreset.OutPrefix.Length) : key;
            return target.GetType().GetField(name);
        }

        private void InitUi()
        {
            Text = "Параметры инженерных модулей — NWD2DWG";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            // Ширина подобрана так, чтобы все семь вкладок помещались в ряд
            // без стрелок прокрутки: при 760 последняя уезжала за край.
            ClientSize = new Size(880, 600);
            MinimumSize = new Size(820, 540);
            BackColor = CadTheme.ColBg;
            ForeColor = CadTheme.ColText;
            Font = new Font("Segoe UI", 9f);
            HandleCreated += (s, e) => CadTheme.ApplyDwmDarkTheme(this);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var pRoot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12),
                BackColor = CadTheme.ColBg
            };
            pRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            pRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

            var pHead = new Panel { Dock = DockStyle.Fill, BackColor = CadTheme.ColHeader };
            pHead.Controls.Add(new Label
            {
                Text = "Допуски и параметры расчёта",
                ForeColor = CadTheme.ColText,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(12, 6)
            });
            pHead.Controls.Add(new Label
            {
                Text = "Все линейные величины — в миллиметрах, как и координаты модели",
                ForeColor = CadTheme.ColTextMuted,
                AutoSize = true,
                Location = new Point(13, 26)
            });

            // Шаблон подставляет типовые допуски под вид работ; дальше их
            // можно править вручную — шаблон ничего не блокирует.
            // Строка собрана компоновщиком: при любой ширине окна поле выбора
            // и кнопки не наезжают друг на друга.
            var pPreset = new TableLayoutPanel
            {
                Location = new Point(12, 40),
                Height = 26,
                ColumnCount = 7,
                RowCount = 1,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            pPreset.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pPreset.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int ci = 0; ci < 5; ci++) pPreset.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var lbPreset = new Label
            {
                Text = "Шаблон:",
                ForeColor = CadTheme.ColText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 4, 6, 0)
            };

            _cbPreset = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CadTheme.ColInput,
                ForeColor = CadTheme.ColText,
                FlatStyle = FlatStyle.Flat,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 1, 8, 1)
            };

            _lbPresetNorms = new Label
            {
                Text = "Выберите шаблон, чтобы подставить типовые допуски",
                ForeColor = CadTheme.ColTextMuted,
                AutoSize = true,
                Location = new Point(13, 72),
                MaximumSize = new Size(900, 16)
            };

            _cbPreset.SelectedIndexChanged += (s, e) => ApplyPreset();
            pPreset.Controls.Add(lbPreset, 0, 0);
            pPreset.Controls.Add(_cbPreset, 1, 0);
            pPreset.Controls.Add(PresetButton("Сохранить…", 92, SavePresetAs,
                "Сохранить текущие значения как собственный шаблон"), 2, 0);
            pPreset.Controls.Add(PresetButton("Дублировать", 92, ClonePreset,
                "Создать копию выбранного шаблона под новым именем"), 3, 0);
            _btnPresetDel = PresetButton("Удалить", 74, DeletePreset,
                "Удалить выбранный пользовательский шаблон");
            pPreset.Controls.Add(_btnPresetDel, 4, 0);
            pPreset.Controls.Add(PresetButton("Импорт…", 78, ImportPreset,
                "Загрузить шаблон из файла и добавить в список"), 5, 0);
            _btnPresetExp = PresetButton("Экспорт…", 82, ExportPreset,
                "Сохранить выбранный шаблон в файл для передачи коллегам");
            pPreset.Controls.Add(_btnPresetExp, 6, 0);

            pHead.Controls.Add(pPreset);
            pHead.Controls.Add(_lbPresetNorms);
            pHead.Resize += (s, e) => pPreset.Width = Math.Max(420, pHead.Width - 24);

            ReloadPresets(null);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs = tabs;

            var t1 = NewTab("Коллизии и 4D");
            var t2 = NewTab("2D планы и помещения");
            var t3 = NewTab("Трубы и гильзы");
            var t4 = NewTab("Металл и массы");
            var t5 = NewTab("Геометрия и чистка");
            var t6 = NewTab("Выдача файлов");
            var t7 = NewTab("ИИ-помощник");
            InitClash(t1); InitPlans(t2); InitPipes(t3); InitSteel(t4); InitGeom(t5);
            InitOutput(t6); InitAi(t7);
            tabs.TabPages.AddRange(new[] { t1, t2, t3, t4, t5, t6, t7 });

            var pBottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
            pBottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            pBottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pBottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            pBottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

            var btnReset = MakeButton("⟲ По умолчанию", false);
            btnReset.Click += (s, e) =>
            {
                var d = new OutputProfile();
                foreach (var f in OutputProfile.Fields) f.SetValue(_out, f.GetValue(d));
                ApplyToUi(new AdvancedConfig());
            };

            var btnSave = MakeButton("💾 Сохранить", true);
            btnSave.DialogResult = DialogResult.OK;
            btnSave.Click += (s, e) => { ReadFromUi(); ReadAiFromUi(); };

            var btnCancel = MakeButton("Отмена", false);
            btnCancel.DialogResult = DialogResult.Cancel;

            pBottom.Controls.Add(btnReset, 0, 0);
            pBottom.Controls.Add(btnSave, 2, 0);
            pBottom.Controls.Add(btnCancel, 3, 0);

            pRoot.Controls.Add(pHead, 0, 0);
            pRoot.Controls.Add(tabs, 0, 1);
            pRoot.Controls.Add(pBottom, 0, 2);
            Controls.Add(pRoot);
            AcceptButton = btnSave;
            CancelButton = btnCancel;

            ApplyToUi(_config);
        }

        /// <summary>Открыть вкладку по номеру — используется для снимков экрана.</summary>
        public void SelectTab(int index)
        {
            if (_tabs != null && index >= 0 && index < _tabs.TabPages.Count)
                _tabs.SelectedIndex = index;
        }

        private static TabPage NewTab(string title)
        {
            return new TabPage(title) { BackColor = CadTheme.ColPanel, Padding = new Padding(10) };
        }

        private Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? CadTheme.ColBtnPrimary : CadTheme.ColBtnSec,
                ForeColor = primary ? Color.White : CadTheme.ColText,
                Cursor = Cursors.Hand,
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 6, 4, 0)
            };
            b.FlatAppearance.BorderColor = primary ? CadTheme.ColAccent : CadTheme.ColBorder;
            return b;
        }

        // --------------------------------------------------------------------
        // Конструкторы строк. Имя поля конфига = ключ привязки.
        // --------------------------------------------------------------------
        private TableLayoutPanel NewGrid(TabPage tab, int rows)
        {
            var p = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = rows + 1,
                AutoScroll = true,
                GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
                BackColor = CadTheme.ColPanel
            };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            for (int i = 0; i < rows; i++) p.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            p.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // распорка снизу
            tab.Controls.Add(p);
            return p;
        }

        private void Num(TableLayoutPanel p, int row, string field, string label,
                         decimal min, decimal max, decimal step, int dec, string hint = null)
        {
            var lb = new Label { Text = label, ForeColor = CadTheme.ColText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 0) };
            var nud = new NumericUpDown
            {
                Minimum = min, Maximum = max, Increment = step, DecimalPlaces = dec,
                BackColor = CadTheme.ColInput, ForeColor = CadTheme.ColText,
                BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill,
                Margin = new Padding(3, 4, 3, 4)
            };
            if (!string.IsNullOrEmpty(hint)) { _tips.SetToolTip(lb, hint); _tips.SetToolTip(nud, hint); }
            p.Controls.Add(lb, 0, row);
            p.Controls.Add(nud, 1, row);
            _binds[field] = nud;
        }

        private void Check(TableLayoutPanel p, int row, string field, string label, string hint = null)
        {
            Check(p, row, 0, field, label, hint);
        }

        private void Check(TableLayoutPanel p, int row, int col, string field, string label, string hint = null)
        {
            var cb = new CheckBox { Text = label, ForeColor = CadTheme.ColText, AutoSize = true, Margin = new Padding(3, 7, 3, 0) };
            if (!string.IsNullOrEmpty(hint)) _tips.SetToolTip(cb, hint);
            p.Controls.Add(cb, col, row);
            _binds[field] = cb;
        }

        private void Combo(TableLayoutPanel p, int row, string field, string label,
                           string[] values, string[] captions, string hint = null)
        {
            var lb = new Label { Text = label, ForeColor = CadTheme.ColText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 0) };
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = CadTheme.ColInput, ForeColor = CadTheme.ColText,
                FlatStyle = FlatStyle.Flat, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4),
                Tag = values
            };
            cb.Items.AddRange(captions);
            if (!string.IsNullOrEmpty(hint)) { _tips.SetToolTip(lb, hint); _tips.SetToolTip(cb, hint); }
            p.Controls.Add(lb, 0, row);
            p.Controls.Add(cb, 1, row);
            _binds[field] = cb;
        }

        private void Str(TableLayoutPanel p, int row, string field, string label, string hint = null)
        {
            Str(p, row, field, label, hint, false);
        }

        // browseFolder — рядом с полем появляется кнопка выбора папки в проводнике
        private void Str(TableLayoutPanel p, int row, string field, string label,
                         string hint, bool browseFolder)
        {
            var lb = new Label { Text = label, ForeColor = CadTheme.ColText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 0) };
            var tb = new TextBox
            {
                BackColor = CadTheme.ColInput, ForeColor = CadTheme.ColText,
                BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill
            };
            if (!string.IsNullOrEmpty(hint)) { _tips.SetToolTip(lb, hint); _tips.SetToolTip(tb, hint); }
            p.Controls.Add(lb, 0, row);

            if (!browseFolder)
            {
                tb.Margin = new Padding(3, 4, 3, 4);
                p.Controls.Add(tb, 1, row);
            }
            else
            {
                var host = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                    Margin = new Padding(3, 4, 3, 4), BackColor = Color.Transparent
                };
                host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));

                var btn = new Button
                {
                    Text = "…", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
                    BackColor = CadTheme.ColBtnSec, ForeColor = CadTheme.ColText,
                    Cursor = Cursors.Hand, Margin = new Padding(4, 0, 0, 0)
                };
                btn.FlatAppearance.BorderColor = CadTheme.ColBorder;
                _tips.SetToolTip(btn, "Выбрать папку в проводнике");
                btn.Click += (s, e) =>
                {
                    using (var fb = new FolderBrowserDialog
                    {
                        Description = label,
                        ShowNewFolderButton = true,
                        SelectedPath = Directory.Exists(tb.Text) ? tb.Text : ""
                    })
                    {
                        if (fb.ShowDialog(this) == DialogResult.OK) tb.Text = fb.SelectedPath;
                    }
                };

                host.Controls.Add(tb, 0, 0);
                host.Controls.Add(btn, 1, 0);
                p.Controls.Add(host, 1, row);
            }
            _binds[field] = tb;
        }

        // --------------------------------------------------------------------
        // Вкладки
        // --------------------------------------------------------------------
        private void InitClash(TabPage tab)
        {
            var p = NewGrid(tab, 11);
            Num(p, 0, "ClashEpsilonMm", "Радиус кластеризации коллизий ε (мм):", 50, 20000, 50, 0,
                "DBSCAN: коллизии ближе этого расстояния считаются одной проблемой.\nДля труб 500–1500 мм, для конструкций 2000–3000 мм.");
            Num(p, 1, "ClashMinPts", "Мин. коллизий в кластере:", 1, 50, 1, 0,
                "Кластеры меньшего размера уходят в шум.");
            Num(p, 2, "ClashMinDistanceMm", "Игнорировать пересечения мельче (мм):", 0, 200, 1, 1,
                "Отсекает касания и толщину изоляции.");
            Check(p, 3, "ClashIncludeResolved", "Включать снятые коллизии (Resolved)");
            Check(p, 4, "ClashIncludeApproved", "Включать согласованные коллизии (Approved)");
            Str(p, 5, "BcfAuthor", "Автор в пакете BCF:");

            Combo(p, 6, "ScheduleSource", "Источник графика 4D:",
                new[] { "Timeliner", "File" },
                new[] { "TimeLiner из модели Navisworks", "Внешний файл (ключ --schedule)" },
                "TimeLiner берёт задачи прямо из модели.\nВнешний файл: MS Project XML или CSV.");
            Str(p, 7, "ScheduleStatusDate", "Дата среза (гггг-ММ-дд, пусто = сегодня):",
                "На эту дату оценивается отставание от графика.");
            Check(p, 8, "ScheduleOnlyLinked", "Только задачи с привязанными элементами модели");

            Num(p, 9, "MinHeadroomCorridorMm", "Мин. высота путей эвакуации (мм):", 1500, 4000, 50, 0,
                "СП 1.13130. Ниже этой отметки формируется нарушение.");
            Num(p, 10, "ClearanceCellMm", "Шаг сетки проверки высоты (мм):", 100, 5000, 100, 0,
                "Мельче — точнее и медленнее.");
        }

        private void InitPlans(TabPage tab)
        {
            var p = NewGrid(tab, 9);
            Num(p, 0, "GeoShiftThresholdMm", "Сдвигать к нулю, если модель дальше (мм):", 1000, 100000000, 10000, 0,
                "Ниже порога сдвиг не выполняется: на таких координатах дребезга нет.");
            Num(p, 1, "SectionCutHeightMm", "Отметка горизонтального среза Z (мм):", -1000000, 1000000, 100, 0,
                "ГОСТ 21.501 — обычно +1200 мм от уровня чистого пола.\nПо умолчанию это абсолютная отметка в координатах выгрузки.");
            Check(p, 2, "SectionZFromModelBottom", "Отсчитывать отметку среза от низа модели",
                "Низ определяется предварительным проходом и является оценкой:\nна сложных моделях плоскость может не попасть в геометрию.");
            Num(p, 3, "SectionDpEpsMm", "Упрощение полилиний, Дуглас-Пекер (мм):", 0.5m, 200, 0.5m, 1,
                "Больше значение — меньше точек в плане.");
            Str(p, 4, "SectionLayer", "Слой 2D-плана в DXF:");
            Num(p, 5, "RoomMinAreaM2", "Мин. площадь помещения (м²):", 0.5m, 100, 0.5m, 1,
                "Замкнутые контуры меньшей площади считаются нишами и отбрасываются.");
            Num(p, 6, "RoomMaxAreaM2", "Макс. площадь помещения (м²):", 10, 100000, 10, 0,
                "Отсекает внешний контур здания, который тоже замкнут.");
            Num(p, 7, "RoomHeightMm", "Высота помещения по умолчанию (мм):", 1500, 20000, 100, 0,
                "Используется, если высота не извлекается из модели.");
            Check(p, 8, "RoomDeductOpenings", "Вычитать проёмы дверей и окон из площади стен (ГОСТ 21.501)");
        }

        private void InitPipes(TabPage tab)
        {
            var p = NewGrid(tab, 9);
            Num(p, 0, "PipeMinDiameterMm", "Мин. диаметр распознаваемой трубы (мм):", 1, 500, 5, 0,
                "Отсекает ложные цилиндры на скруглениях.");
            Num(p, 1, "PipeMaxDiameterMm", "Макс. диаметр распознаваемой трубы (мм):", 100, 5000, 50, 0,
                "Отсекает корпуса аппаратов, принятые за трубу.");
            Num(p, 2, "PipeMinLengthMm", "Мин. длина участка трубы (мм):", 10, 5000, 10, 0);
            Num(p, 3, "SleeveGapSmallMm", "Зазор гильзы, DN < 50 (мм):", 5, 200, 5, 0);
            Num(p, 4, "SleeveGapMediumMm", "Зазор гильзы, DN 50…200 (мм):", 5, 300, 5, 0,
                "СП 73.13330 — обычно +50 мм.");
            Num(p, 5, "SleeveGapLargeMm", "Зазор гильзы, DN > 200 (мм):", 10, 500, 10, 0);
            Num(p, 6, "SleeveExtensionMm", "Выпуск гильзы за конструкцию (мм):", 0, 300, 5, 0);
            Num(p, 7, "SleeveMinStructureMm", "Мин. толщина конструкции под гильзу (мм):", 5, 1000, 5, 0,
                "Тоньше — облицовка или лист, гильза не ставится.");
            Num(p, 8, "IsoJointToleranceMm", "Допуск стыковки участков в изометрии (мм):", 0.1m, 100, 0.5m, 1);
        }

        private void InitSteel(TabPage tab)
        {
            var p = NewGrid(tab, 12);
            Num(p, 0, "SteelTolerancePct", "Допуск подбора профиля (±%):", 0.5m, 20, 0.5m, 1,
                "Расхождение габаритов сечения с сортаментом ГОСТ 8240, 26020, 8509, 30245, 8732.");
            Num(p, 1, "SteelMinLengthMm", "Мин. длина элемента проката (мм):", 50, 10000, 50, 0);
            Num(p, 2, "SteelMinAspect", "Мин. отношение длины к сечению:", 1.5m, 50, 0.5m, 1,
                "Ниже этого элемент не считается вытянутым и не проверяется.");
            Num(p, 3, "SteelMinConfidence", "Мин. достоверность подбора (0…1):", 0.1m, 1.0m, 0.05m, 2);
            Check(p, 4, "SteelIncludeCustom", "Включать в ведомость профили вне сортамента",
                "Такие позиции идут как «Индивидуальный», а их погонная масса — оценка по периметру при толщине стенки 6 мм.");
            Num(p, 5, "DensitySteel", "Плотность стали (кг/м³):", 1000, 15000, 50, 0);
            Num(p, 6, "DensityConcrete", "Плотность бетона (кг/м³):", 500, 5000, 50, 0);
            Num(p, 7, "DensityAluminum", "Плотность алюминия (кг/м³):", 500, 5000, 50, 0);
            Num(p, 8, "DensityInsulation", "Плотность изоляции (кг/м³):", 10, 1000, 10, 0);
            Num(p, 9, "DensityEquipment", "Плотность оборудования (кг/м³):", 100, 10000, 50, 0,
                "Средняя эквивалентная для насосов, ёмкостей, шкафов.");
            Num(p, 10, "DensityPiping", "Плотность трубопровода с водой (кг/м³):", 100, 10000, 50, 0);
            Num(p, 11, "CogMinMassKg", "Мин. масса элемента в ведомости (кг):", 0, 10000, 1, 1);
        }

        private void InitGeom(TabPage tab)
        {
            var p = NewGrid(tab, 11);
            Num(p, 0, "DecimateMinTriangles", "Не упрощать фрагменты мельче (треугольников):", 4, 5000, 4, 0,
                "На мелких фрагментах QEM стоит дороже выигрыша.");
            Num(p, 1, "DecimateBoundaryWeight", "Вес закрепления границы сетки:", 0, 100000, 100, 0,
                "Больше — сильнее удерживаются края открытых оболочек.\n0 отключает закрепление.");
            Check(p, 2, "DecimatePreventFlips", "Запрещать схлопывания, выворачивающие грани");
            Num(p, 3, "SolidMinConfidence", "Мин. достоверность распознавания тел (0…1):", 0.1m, 1.0m, 0.05m, 2);
            Combo(p, 4, "ShrinkwrapLevel", "Уровень защиты ноу-хау (Shrinkwrap):",
                new[] { "1", "2", "3" },
                new[] { "1 — удалить внутренние полости", "2 — габаритный OBB-параллелепипед", "3 — выпуклая оболочка" });

            Combo(p, 5, "BoqGroupBy", "Группировка ведомости объёмов:",
                new[] { "Element", "Layer", "Material" },
                new[] { "По имени элемента", "По слою / разделу", "По материалу" },
                "При выгрузке одним слоем группировка по слою даёт одну строку на всю модель.");
            Num(p, 6, "BoqMinVolumeM3", "Не включать в ВОР позиции объёмом менее (м³):", 0, 100, 0.001m, 3);

            Check(p, 7, "PurgeLayers", "Чистка DXF: удалять неиспользуемые слои");
            Check(p, 8, "PurgeLinetypes", "Чистка DXF: удалять неиспользуемые типы линий");
            Check(p, 9, "PurgeTextStyles", "Чистка DXF: удалять неиспользуемые текстовые стили");
            Check(p, 10, "PurgeBlocks", "Чистка DXF: удалять неиспользуемые блоки");
        }

        // --------------------------------------------------------------------
        // Вкладка ИИ. Здесь только подключение: адрес, модель, ключ, порядок
        // перебора. Сама программа считает без языковой модели и продолжит
        // считать, если её отключить, — ИИ добавляется поверх, а не внутрь
        // расчёта. Иначе результат нельзя было бы воспроизвести и защитить.
        // --------------------------------------------------------------------
        private void InitAi(TabPage tab)
        {
            var p = NewGrid(tab, 22);

            var warn = new Label
            {
                Text = "Обращения к модели уходят на указанный адрес. Для работы в закрытом\n" +
                       "контуре оставьте режим «только локальные адреса»: тогда программа\n" +
                       "физически не сможет обратиться никуда, кроме этой машины.",
                ForeColor = CadTheme.ColTextMuted, AutoSize = true,
                Margin = new Padding(3, 2, 3, 8)
            };
            p.Controls.Add(warn, 0, 0);
            p.SetColumnSpan(warn, 2);
            // Строки сетки фиксированы по 31 px — предупреждению нужно больше.
            p.RowStyles[0] = new RowStyle(SizeType.Absolute, 62);

            _aiOn = new CheckBox { Text = "Включить ИИ-помощника", ForeColor = CadTheme.ColText, AutoSize = true, Margin = new Padding(3, 7, 3, 0) };
            _tips.SetToolTip(_aiOn, "Пока выключено, ни одного сетевого обращения не выполняется.");
            p.Controls.Add(_aiOn, 0, 2);

            _aiLocal = new CheckBox { Text = "Только локальные адреса (закрытый контур)", ForeColor = CadTheme.ColText, AutoSize = true, Margin = new Padding(3, 7, 3, 0) };
            _tips.SetToolTip(_aiLocal, "Разрешены только localhost и 127.0.0.1. Внешние шлюзы отключаются.");
            p.Controls.Add(_aiLocal, 0, 3);

            _aiData = new CheckBox { Text = "Разрешить передавать имена элементов модели", ForeColor = CadTheme.ColText, AutoSize = true, Margin = new Padding(3, 7, 3, 0) };
            _tips.SetToolTip(_aiData, "Имена элементов раскрывают состав проекта. Без этой галки\nмодели передаются только обезличенные сводки.");
            p.Controls.Add(_aiData, 0, 4);

            var lbN = new Label { Text = "Не более имён за один запрос:", ForeColor = CadTheme.ColText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 0) };
            _aiMaxNames = new NumericUpDown
            {
                Minimum = 10, Maximum = 5000, Increment = 10, DecimalPlaces = 0,
                BackColor = CadTheme.ColInput, ForeColor = CadTheme.ColText,
                BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill,
                Margin = new Padding(3, 4, 3, 4)
            };
            p.Controls.Add(lbN, 0, 5);
            p.Controls.Add(_aiMaxNames, 1, 5);

            int row = 6;
            for (int i = 0; i < _ai.Providers.Count && row + 5 < 22; i++)
            {
                var pr = _ai.Providers[i];

                var use = new CheckBox { Text = "Использовать провайдер " + (i + 1), ForeColor = CadTheme.ColText, AutoSize = true, Margin = new Padding(3, 10, 3, 0) };
                _tips.SetToolTip(use, "Провайдеры перебираются сверху вниз: первый ответивший выигрывает.");
                p.Controls.Add(use, 0, row);
                var name = MakeAiBox(false);
                p.Controls.Add(name, 1, row); row++;

                p.Controls.Add(AiLabel("Адрес (base URL):"), 0, row);
                var url = MakeAiBox(false);
                _tips.SetToolTip(url, "Например http://localhost:11434/v1 для Ollama\nили http://localhost:1234/v1 для LM Studio.");
                p.Controls.Add(url, 1, row); row++;

                p.Controls.Add(AiLabel("Модель:"), 0, row);
                var model = MakeAiBox(false);
                p.Controls.Add(model, 1, row); row++;

                p.Controls.Add(AiLabel("Ключ API (для локальных не нужен):"), 0, row);
                var key = MakeAiBox(true);
                _tips.SetToolTip(key, "Хранится зашифрованным средствами Windows под вашей учётной\nзаписью, в открытом виде на диск не попадает.");
                p.Controls.Add(key, 1, row); row++;

                var status = new Label { Text = "", ForeColor = CadTheme.ColTextMuted, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 9, 3, 0) };
                p.Controls.Add(status, 0, row);
                var test = new Button
                {
                    Text = "Проверить связь", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
                    BackColor = CadTheme.ColBtnSec, ForeColor = CadTheme.ColText,
                    Cursor = Cursors.Hand, Margin = new Padding(3, 4, 3, 8)
                };
                test.FlatAppearance.BorderColor = CadTheme.ColBorder;
                p.Controls.Add(test, 1, row); row++;

                var row4 = new Control[] { use, name, url, model, key, status, test };
                _aiRows.Add(row4);

                int idx = i;
                test.Click += (s, e) => TestProvider(idx);
            }

            ApplyAiToUi();
        }

        private Label AiLabel(string text)
        {
            return new Label { Text = text, ForeColor = CadTheme.ColText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 0) };
        }

        private TextBox MakeAiBox(bool secret)
        {
            var tb = new TextBox
            {
                BackColor = CadTheme.ColInput, ForeColor = CadTheme.ColText,
                BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill,
                Margin = new Padding(3, 4, 3, 4)
            };
            if (secret) tb.UseSystemPasswordChar = true;
            return tb;
        }

        private void ApplyAiToUi()
        {
            _aiOn.Checked = _ai.Enabled;
            _aiLocal.Checked = _ai.LocalOnly;
            _aiData.Checked = _ai.AllowModelData;
            _aiMaxNames.Value = Math.Max(_aiMaxNames.Minimum,
                                Math.Min(_aiMaxNames.Maximum, _ai.MaxNamesPerRequest));
            for (int i = 0; i < _aiRows.Count; i++)
            {
                var pr = _ai.Providers[i];
                var c = _aiRows[i];
                ((CheckBox)c[0]).Checked = pr.Enabled;
                ((TextBox)c[1]).Text = pr.Name;
                ((TextBox)c[2]).Text = pr.BaseUrl;
                ((TextBox)c[3]).Text = pr.Model;
                // Сохранённый ключ не показываем даже звёздочками правильной длины.
                ((TextBox)c[4]).Text = pr.HasKey ? "········" : "";
            }
        }

        private void ReadAiFromUi()
        {
            if (_aiOn == null) return;
            _ai.Enabled = _aiOn.Checked;
            _ai.LocalOnly = _aiLocal.Checked;
            _ai.AllowModelData = _aiData.Checked;
            _ai.MaxNamesPerRequest = (int)_aiMaxNames.Value;
            for (int i = 0; i < _aiRows.Count; i++)
            {
                var pr = _ai.Providers[i];
                var c = _aiRows[i];
                pr.Enabled = ((CheckBox)c[0]).Checked;
                pr.Name = ((TextBox)c[1]).Text.Trim();
                pr.BaseUrl = ((TextBox)c[2]).Text.Trim();
                pr.Model = ((TextBox)c[3]).Text.Trim();

                string typed = ((TextBox)c[4]).Text;
                if (typed == "········") continue;          // не трогали — оставляем как было
                if (typed.Trim().Length == 0) pr.SetKey("");  // очистили — стираем
                else pr.SetKey(typed.Trim());
            }
            _ai.Save();
        }

        // Проверка идёт в фоновом потоке: обращение к модели занимает секунды,
        // и замораживать на это диалог нельзя.
        private void TestProvider(int idx)
        {
            ReadAiFromUi();
            var c = _aiRows[idx];
            var status = (Label)c[5];
            var btn = (Button)c[6];
            var pr = _ai.Providers[idx];

            status.ForeColor = CadTheme.ColTextMuted;
            status.Text = "проверяю…";
            btn.Enabled = false;

            var th = new Thread(() =>
            {
                string info;
                bool ok = AiClient.Test(_ai, pr, out info);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        status.ForeColor = ok ? CadTheme.ColOk : CadTheme.ColErr;
                        status.Text = (ok ? "связь есть: " : "не отвечает: ") +
                                      (info.Length > 70 ? info.Substring(0, 70) + "…" : info);
                        btn.Enabled = true;
                    });
                }
                catch (InvalidOperationException) { }   // диалог закрыли раньше ответа
            });
            th.IsBackground = true;
            th.Start();
        }

        private void InitOutput(TabPage tab)
        {
            const string O = ConfigPreset.OutPrefix;
            var p = NewGrid(tab, 26);

            Str(p, 0, O + "OutputRoot", "Папка выдачи (пусто = рядом с результатом):",
                "Допустимы подстановки {code}, {mark}, {model}, {date}.", true);
            Check(p, 1, O + "UseFolders", "Раскладывать по подпапкам разделов",
                "01_Геометрия, 02_Ведомости, 03_Координация, 04_Протокол");
            Str(p, 2, O + "ProjectCode", "Шифр объекта (например 2451-14):",
                "Подставляется в имена файлов как {code}.");
            Str(p, 3, O + "DocMark", "Марка комплекта (АР, КР, ОВ, ВК, КМ, ТХ):",
                "Подставляется в имена файлов как {mark}.");
            Str(p, 4, O + "NamePattern", "Шаблон имени файла:",
                "Токены: {base} {model} {code} {mark} {date} {suffix}.\nПример: {code}-{mark}{suffix}");

            Combo(p, 5, O + "ReportFormat", "Формат ведомостей:",
                new[] { "Csv", "Xlsx", "Both" },
                new[] { "CSV (открывается везде)", "XLSX (таблица Excel)", "CSV и XLSX" });
            Combo(p, 6, O + "CsvEncoding", "Кодировка CSV:",
                new[] { "UTF-8", "Windows-1251" },
                new[] { "UTF-8 с BOM (универсально)", "Windows-1251 (старый Excel)" });
            Combo(p, 7, O + "CsvSeparator", "Разделитель колонок CSV:",
                new[] { ";", ",", "\t" },
                new[] { "Точка с запятой  ;", "Запятая  ,", "Табуляция" });
            Combo(p, 8, O + "DecimalSeparator", "Десятичный разделитель:",
                new[] { ",", "." },
                new[] { "Запятая (русский Excel)", "Точка (расчёты и скрипты)" });
            Check(p, 9, O + "CsvSepHint", "Добавлять строку sep= в начало CSV",
                "Подсказка Excel о разделителе. Мешает автоматическому разбору скриптами.");

            Str(p, 10, O + "FolderGeometry", "Подпапка геометрии:",
                "Используется, если включена раскладка по разделам.");
            Str(p, 11, O + "FolderReports", "Подпапка ведомостей:");
            Str(p, 12, O + "FolderCoordination", "Подпапка координации (DXF, BCF):");
            Str(p, 13, O + "FolderProtocol", "Подпапка протокола:");

            Check(p, 14, 0, O + "EmitReports", "Выгружать ведомости");
            Check(p, 14, 1, O + "EmitAuxDxf", "Выгружать вспомогательные DXF");
            Check(p, 20, 0, O + "EmitGeometry", "Писать основную геометрию (DXF/DWG)",
                "Снимите, если нужны только ведомости и отчёты: на крупной модели\nэто экономит гигабайты и минуты прогона. Расчёты выполняются полностью.");

            Check(p, 15, 0, O + "EmitProtocol", "Протокол расчёта и опись файлов",
                "Фиксирует исходную модель, все применённые допуски и перечень выданных файлов.");
            Check(p, 15, 1, O + "EmitLogCopy", "Копия журнала рядом с результатом");

            Check(p, 16, 0, O + "BoqColCount", "ВОР: число геометрических фрагментов");
            Check(p, 16, 1, O + "BoqColArea", "ВОР: площадь поверхности");
            Check(p, 17, 0, O + "BoqColVolume", "ВОР: объём");
            Check(p, 17, 1, O + "BoqColMass", "ВОР: расчётная масса");
            Check(p, 18, 0, O + "CogColFragments", "Массы: число фрагментов");
            Check(p, 18, 1, O + "CogColDensity", "Массы: плотность материала");
            Check(p, 19, 0, O + "CogColVolume", "Массы: объём");
            Check(p, 19, 1, O + "CogColCog", "Массы: координаты центра тяжести");
            Check(p, 20, 0, O + "SteelColGost", "Сортамент: стандарт ГОСТ");
            Check(p, 20, 1, O + "SteelColLength", "Сортамент: общая длина");
            Check(p, 21, 0, O + "SteelColMassPerM", "Сортамент: погонная масса");
        }

        // --------------------------------------------------------------------
        // Связывание конфига и контролов (одна таблица на оба направления)
        // --------------------------------------------------------------------
        private void ApplyToUi(AdvancedConfig cfg)
        {
            foreach (var kv in _binds)
            {
                object target = kv.Key.StartsWith(ConfigPreset.OutPrefix, StringComparison.Ordinal)
                    ? (object)_out : cfg;
                FieldInfo fi = FieldOf(target, kv.Key);
                if (fi == null) continue;
                object v = fi.GetValue(target);

                var nud = kv.Value as NumericUpDown;
                if (nud != null)
                {
                    decimal d = Convert.ToDecimal(Convert.ToDouble(v, CultureInfo.InvariantCulture));
                    nud.Value = Math.Max(nud.Minimum, Math.Min(nud.Maximum, d));
                    continue;
                }
                var cb = kv.Value as CheckBox;
                if (cb != null) { cb.Checked = Convert.ToBoolean(v); continue; }
                var combo = kv.Value as ComboBox;
                if (combo != null)
                {
                    var values = (string[])combo.Tag;
                    string cur = Convert.ToString(v, CultureInfo.InvariantCulture);
                    int idx = Array.IndexOf(values, cur);
                    combo.SelectedIndex = idx >= 0 ? idx : 0;
                    continue;
                }
                var tb = kv.Value as TextBox;
                if (tb != null) tb.Text = Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        private void ReadFromUi() { ReadFromUi(true); }

        private void ReadFromUi(bool persist)
        {
            foreach (var kv in _binds)
            {
                object target = TargetOf(kv.Key);
                FieldInfo fi = FieldOf(target, kv.Key);
                if (fi == null) continue;

                var nud = kv.Value as NumericUpDown;
                if (nud != null)
                {
                    if (fi.FieldType == typeof(int)) fi.SetValue(target, (int)nud.Value);
                    else fi.SetValue(target, (double)nud.Value);
                    continue;
                }
                var cb = kv.Value as CheckBox;
                if (cb != null) { fi.SetValue(target, cb.Checked); continue; }
                var combo = kv.Value as ComboBox;
                if (combo != null)
                {
                    var values = (string[])combo.Tag;
                    int i = Math.Max(0, combo.SelectedIndex);
                    string val = values[Math.Min(i, values.Length - 1)];
                    if (fi.FieldType == typeof(int))
                    {
                        int n;
                        if (int.TryParse(val, out n)) fi.SetValue(target, n);
                    }
                    else fi.SetValue(target, val);
                    continue;
                }
                var tb = kv.Value as TextBox;
                if (tb != null) fi.SetValue(target, tb.Text.Trim());
            }
            if (persist) { _config.Save(); _out.Save(); }
        }
    }
}
