// ============================================================================
//  EngineeringPipeline.cs — подключение инженерных модулей к конвейеру
//  NWD2DWG | namespace NWD2DWG.Plugin
//
//  Модули собирают данные по ходу извлечения геометрии и на выходе пишут
//  побочные файлы: ВОР, массы и центр тяжести, сортамент проката, оси труб,
//  изометрии, гильзы, клиренс, 2D-план, ведомость отделки, коллизии BCF,
//  4D-статус по графику, геопривязку.
//
//  Все допуски приходят из AdvancedConfig (файл modules.json, который пишет
//  exe в папку прогона) — в миллиметрах, как и координаты модели.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    /// <summary>
    /// Общий признак прекращения работы.
    ///
    /// Остановить одну лишь запись оказалось мало: файл переставал расти, но
    /// обход продолжался. На конструктивной модели он провёл в одном элементе
    /// больше получаса — тесселяция шла дальше и копилась уже в памяти, что
    /// хуже, чем расход диска. Признак читают все три уровня: приём
    /// треугольников, цикл по фрагментам и цикл по элементам.
    /// </summary>
    public static class ConvertAbort
    {
        private static volatile bool _abort;
        public static bool Requested { get { return _abort; } }
        public static void Request() { _abort = true; }
        public static void Reset() { _abort = false; }
    }

    // ------------------------------------------------------------------------
    // Флаги включения модулей (позиционные аргументы плагина)
    // ------------------------------------------------------------------------
    public class EngineeringOptions
    {
        public bool GeoShift, ExportGrids, TracePipes, ExportBoq, ExportBcf, Anonymize;
        public bool ClusterClashes, SectionPlan, PurgeDxf, BuildPenetrations,
                    ValidateClearance, MatchSteel, CalcCog, GenerateIso,
                    MapSchedule4D, Shrinkwrap, RoomFinish;

        public string ScheduleFile = "";
        public AdvancedConfig Cfg = new AdvancedConfig();
        public OutputProfile Out = new OutputProfile();

        private static bool B(string[] p, int i) { return p.Length > i && p[i] == "1"; }

        public static EngineeringOptions FromArgs(string[] p)
        {
            var o = new EngineeringOptions();
            o.GeoShift          = B(p, 15);
            o.ExportGrids       = B(p, 16);
            o.TracePipes        = B(p, 17);
            o.ExportBoq         = B(p, 18);
            o.ExportBcf         = B(p, 19);
            o.Anonymize         = B(p, 20);
            o.ClusterClashes    = B(p, 21);
            o.SectionPlan       = B(p, 22);
            o.PurgeDxf          = B(p, 23);
            o.BuildPenetrations = B(p, 24);
            o.ValidateClearance = B(p, 25);
            o.MatchSteel        = B(p, 26);
            o.CalcCog           = B(p, 27);
            o.GenerateIso       = B(p, 28);
            o.MapSchedule4D     = B(p, 29);
            o.Shrinkwrap        = B(p, 30);
            o.RoomFinish        = B(p, 31);
            o.Cfg               = AdvancedConfig.LoadFrom(p.Length > 32 ? p[32] : null);
            o.ScheduleFile      = p.Length > 33 ? p[33] : "";
            o.Out               = OutputProfile.LoadFrom(p.Length > 34 ? p[34] : null);
            return o;
        }

        // Нужен ли обход геометрии по элементам
        public bool AnyGeometryConsumer
        {
            get
            {
                return ExportBoq || CalcCog || MatchSteel || TracePipes || GenerateIso
                    || BuildPenetrations || ValidateClearance || SectionPlan
                    || RoomFinish || MapSchedule4D;
            }
        }

        public bool NeedsClashData { get { return ExportBcf || ClusterClashes; } }
    }

    // ------------------------------------------------------------------------
    // Накопитель данных и запись побочных файлов
    // ------------------------------------------------------------------------
    public class EngineeringPipeline
    {
        public EngineeringOptions Opt;
        public Action<string> Log = delegate { };
        public string OutBasePath;
        public string SourceModel = "";
        public int InsUnits = 4;

        private AdvancedConfig C { get { return Opt.Cfg; } }
        private OutputProfile O { get { return Opt.Out; } }
        // перечень выданных файлов для описи и протокола
        private readonly List<string[]> _produced = new List<string[]>();

        private void Note(string path, string what)
        {
            // В опись попадает только то, что действительно лежит на диске.
            // Модуль может отработать вхолостую — например, в модели нет
            // металлопроката, — и файла не будет. Раньше опись его всё равно
            // обещала, и в папке недоставало обещанной ведомости.
            if (string.IsNullOrEmpty(path)) return;
            try { if (!File.Exists(path)) return; } catch { return; }
            _produced.Add(new[] { path, what });
        }

        // Ведомость: приводим к профилю (кодировка, разделители, набор колонок)
        // и заносим в опись. Возвращает имя файла для строки журнала.
        private string Csv(string path, string what, params string[] dropCols)
        {
            O.NormalizeReport(path, dropCols);

            // Формат ведомостей выбирается в профиле выдачи. Раньше вариант
            // «Xlsx» в настройках был, а книга не создавалась — писался всё тот
            // же CSV. Теперь книга собирается из уже приведённой ведомости,
            // чтобы обе не разошлись по составу колонок.
            if (O.WantsXlsx)
            {
                string xlsx = Path.ChangeExtension(path, ".xlsx");
                if (XlsxWriter.FromCsv(path, xlsx, O.Sep[0], O.ReportEncoding, what))
                {
                    Note(xlsx, what + " (книга Excel)");
                    if (!O.WantsCsv)
                    {
                        // Просили только книгу — CSV был промежуточным.
                        try { File.Delete(path); } catch { }
                        return Path.GetFileName(xlsx);
                    }
                    Note(path, what);
                    return Path.GetFileName(xlsx) + " и " + Path.GetFileName(path);
                }
                Log("ведомость " + Path.GetFileName(path) +
                    ": книгу Excel собрать не удалось, остаётся CSV");
            }

            Note(path, what);
            return File.Exists(path)
                ? Path.GetFileName(path)
                : Path.GetFileName(path) + " (нет данных — файл не создан)";
        }

        private string Dxf(string path, string what)
        {
            Note(path, what);
            return Path.GetFileName(path);
        }

        // Какие колонки выбросить из ведомости по настройкам профиля
        private string[] BoqDrops()
        {
            var d = new List<string>();
            if (!O.BoqColCount) d.Add("Геом. фрагментов");
            if (!O.BoqColArea) d.Add("Площадь");
            if (!O.BoqColVolume) d.Add("Объем");
            if (!O.BoqColMass) d.Add("масса");
            return d.ToArray();
        }

        private string[] CogDrops()
        {
            var d = new List<string>();
            if (!O.CogColFragments) d.Add("Фрагментов");
            if (!O.CogColDensity) d.Add("Плотность");
            if (!O.CogColVolume) d.Add("Объём");
            if (!O.CogColCog) { d.Add("CoG X"); d.Add("CoG Y"); d.Add("CoG Z"); }
            return d.ToArray();
        }

        private string[] SteelDrops()
        {
            var d = new List<string>();
            if (!O.SteelColGost) d.Add("Стандарт");
            if (!O.SteelColLength) d.Add("Общая длина");
            if (!O.SteelColMassPerM) d.Add("Масса 1 м");
            return d.ToArray();
        }

        // --- геопривязка ---
        public GeoTransformResult Geo;
        public bool ShiftActive;

        // --- накопители геометрии ---
        private readonly BoqCalculator _boq = new BoqCalculator();
        // Элемент модели приходит десятками фрагментов (отвод, фланец, патрубок).
        // Ведомости строятся по ЭЛЕМЕНТАМ, иначе CSV раздувается до сотен тысяч
        // бессмысленных строк, а один швеллер попадает в ведомость КМ по 10 раз.
        private readonly Dictionary<string, CogAgg> _cog = new Dictionary<string, CogAgg>(StringComparer.Ordinal);
        private readonly Dictionary<string, SteelMatchResult> _steel = new Dictionary<string, SteelMatchResult>(StringComparer.Ordinal);
        private readonly List<PipeSegment> _pipes = new List<PipeSegment>();
        private readonly List<SceneBox> _boxes = new List<SceneBox>();
        private readonly List<double> _planVerts = new List<double>();
        private readonly List<int> _planFaces = new List<int>();
        private double _sliceZ = double.NaN;
        // фактический диапазон Z по извлечённой геометрии: нужен, чтобы при
        // промахе плоскости среза подсказать пользователю реальные отметки
        private double _geomMinZ = double.MaxValue, _geomMaxZ = double.MinValue;

        // --- данные из Navisworks (заполняет плагин) ---
        public readonly List<ClashPoint> ClashPoints = new List<ClashPoint>();
        public readonly List<BcfTopic> BcfTopics = new List<BcfTopic>();
        public readonly List<ScheduleTask> Tasks4D = new List<ScheduleTask>();
        public readonly Dictionary<string, int> TaskLinkCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        public string ScheduleOrigin = "";

        private int _steelChecked, _cogFragments, _steelCustom;

        // Индекс ревизии собирается всегда: он почти ничего не стоит,
        // а без него сравнить выдачи нечем.
        private readonly Dictionary<string, IndexEntry> _index =
            new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        public int ElementCount { get { return _index.Count; } }
        public long TriangleCount;
        public string PresetName = "";

        private class CogAgg
        {
            public string Name, Material;
            public double Density, VolumeM3, MassKg;
            public double Mx, My, Mz;
            public int Fragments;
        }

        // --------------------------------------------------------------------
        // Геосдвиг и отметка среза
        // --------------------------------------------------------------------
        public void InitBounds(double minX, double minY, double minZ,
                               double maxX, double maxY, double maxZ)
        {
            // Вырожденные габариты (все матрицы фрагментов единичные) — не
            // повод двигать модель: сдвинули бы на случайную величину.
            bool degenerate = (maxX - minX) < 1.0 && (maxY - minY) < 1.0 && (maxZ - minZ) < 1.0;

            var probe = new List<double> { minX, minY, minZ, maxX, maxY, maxZ };
            Geo = GeoTransform.AnalyzeBounds(probe, C.GeoShiftThresholdMm);
            ShiftActive = Opt.GeoShift && !degenerate && Geo != null && Geo.IsShifted;

            if (degenerate && Opt.GeoShift)
                Log("[GeoShift] габариты модели вырождены — сдвиг пропущен");

            if (ShiftActive)
                Log(string.Format(CultureInfo.InvariantCulture,
                    "[GeoShift] сдвиг модели к нулю: dX={0:F1} dY={1:F1} dZ={2:F1}",
                    Geo.OffsetX, Geo.OffsetY, Geo.OffsetZ));
            else if (Opt.GeoShift)
                Log("[GeoShift] координаты в пределах нормы, сдвиг не требуется");

            if (Opt.SectionPlan || Opt.RoomFinish)
            {
                if (C.SectionZFromModelBottom)
                {
                    double baseZ = ShiftActive ? minZ - Geo.OffsetZ : minZ;
                    _sliceZ = baseZ + C.SectionCutHeightMm;
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "[Section2Plan] отметка среза Z = {0:F0} мм (низ модели {1:F0} + {2:F0})",
                        _sliceZ, baseZ, C.SectionCutHeightMm));
                }
                else
                {
                    _sliceZ = C.SectionCutHeightMm;
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "[Section2Plan] отметка среза Z = {0:F0} мм (абсолютная)", _sliceZ));
                }
            }
        }

        public void ApplyShift(List<double> verts)
        {
            if (!ShiftActive || verts == null) return;
            GeoTransform.ApplyShift(verts, Geo.OffsetX, Geo.OffsetY, Geo.OffsetZ);
        }

        public void ShiftPoint(ref double x, ref double y, ref double z)
        {
            if (!ShiftActive) return;
            x += Geo.OffsetX; y += Geo.OffsetY; z += Geo.OffsetZ;
        }

        public Dictionary<string, string> FilterProps(Dictionary<string, string> props)
        {
            if (props == null || !Opt.Anonymize) return props;
            return BimAnonymizer.SanitizeProperties(props);
        }

        // --------------------------------------------------------------------
        // Элемент модели (после сдвига и децимации)
        // --------------------------------------------------------------------
        public void OnElement(string name, string layer, string material,
                              List<double> verts, List<int> quads, SolidResult solid)
        {
            if (verts == null || quads == null || verts.Count < 9 || quads.Count < 4) return;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i + 2 < verts.Count; i += 3)
            {
                double x = verts[i], y = verts[i + 1], z = verts[i + 2];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            if (minZ < _geomMinZ) _geomMinZ = minZ;
            if (maxZ > _geomMaxZ) _geomMaxZ = maxZ;

            string elName = string.IsNullOrEmpty(name) ? (layer ?? "Элемент") : name;

            // Геометрия, не привязанная к именованному элементу, приходит из
            // Navisworks под именем самого файла модели. В ведомости это
            // выглядит обычной позицией: на реальном объекте одна такая строка
            // собрала 42% всей массы, и итог по ведомости становился
            // непригоден. Называем вещи своими именами.
            if (IsModelFileName(elName)) elName = NoNameLabel;

            if (O.EmitIndex)
            {
                // Индекс собирается по имени. Для безымянной геометрии этого
                // мало: на модели, где имён нет вовсе, 2006 элементов давали
                // одну строку индекса, и сравнение ревизий теряло смысл —
                // оно показывало «изменился один элемент» на весь объект.
                //
                // Различать безымянное можно только по месту. Сетка взята
                // грубой, 2 метра: мелкая правка остаётся в той же ячейке и
                // элемент по-прежнему сопоставляется, а переезд дальше двух
                // метров читается как «удалено и добавлено». Для геометрии
                // без имени это честный предел: связать её между ревизиями
                // больше не по чему.
                string idxName = elName;
                if (IsUnnamed(elName))
                    idxName = string.Format(CultureInfo.InvariantCulture, "{0} @{1};{2};{3}",
                        elName,
                        Math.Round((minX + maxX) * 0.5 / 2000),
                        Math.Round((minY + maxY) * 0.5 / 2000),
                        Math.Round((minZ + maxZ) * 0.5 / 2000));

                IndexEntry ie;
                if (!_index.TryGetValue(idxName, out ie))
                {
                    ie = new IndexEntry { Name = idxName };
                    _index[idxName] = ie;
                }
                ie.Expand(minX, minY, minZ, maxX, maxY, maxZ);
                ie.Fragments++;
                ie.Triangles += quads.Count / 4;
            }
            TriangleCount += quads.Count / 4;

            if (Opt.ExportBoq)
            {
                string cat;
                switch (C.BoqGroupBy)
                {
                    case "Layer":    cat = layer ?? "Прочее"; break;
                    case "Material": cat = string.IsNullOrEmpty(material) ? "Не задан" : material; break;
                    default:         cat = elName; break;
                }
                _boq.AddMesh(cat, elName, material ?? "", verts, quads);
            }

            if (Opt.CalcCog)
            {
                string mat = GuessMaterial(material, layer);
                var r = CogCalculator.CalculateElement(elName, verts, quads, mat, C.DensityFor(mat));
                if (r.VolumeM3 > 0)
                {
                    _cogFragments++;
                    CogAgg agg;
                    if (!_cog.TryGetValue(elName, out agg))
                    {
                        agg = new CogAgg { Name = elName, Material = mat, Density = r.DensityKgM3 };
                        _cog[elName] = agg;
                    }
                    agg.VolumeM3 += r.VolumeM3;
                    agg.MassKg += r.MassKg;
                    agg.Mx += r.CogX * r.MassKg;
                    agg.My += r.CogY * r.MassKg;
                    agg.Mz += r.CogZ * r.MassKg;
                    agg.Fragments++;
                }
            }

            if (Opt.MatchSteel && LooksLikeProfile(minX, minY, minZ, maxX, maxY, maxZ))
            {
                _steelChecked++;
                var m = SteelProfileMatcher.MatchMesh(verts, C.SteelTolerancePct);
                if (m != null && !m.Matched && !C.SteelIncludeCustom) m = null;
                if (m != null && m.Confidence >= C.SteelMinConfidence)
                {
                    string key = string.IsNullOrEmpty(name)
                        ? string.Format(CultureInfo.InvariantCulture, "{0:F0}_{1:F0}_{2:F0}", minX, minY, minZ)
                        : name;
                    SteelMatchResult prev;
                    if (!_steel.TryGetValue(key, out prev) || m.Confidence > prev.Confidence)
                    {
                        if (prev != null && !prev.Matched) _steelCustom--;
                        if (!m.Matched) _steelCustom++;
                        _steel[key] = m;
                    }
                }
            }

            if ((Opt.TracePipes || Opt.GenerateIso || Opt.BuildPenetrations) &&
                solid != null && solid.Type == SolidType.Cylinder)
            {
                var seg = PipeTracer.TraceFromSolid(solid, layer ?? "Piping");
                // Отсев мусора распознавания: без него в ведомость гильз
                // попадали DN1 со скруглений и DN2300 с корпусов аппаратов
                if (seg != null && seg.Length >= C.PipeMinLengthMm &&
                    seg.Diameter >= C.PipeMinDiameterMm && seg.Diameter <= C.PipeMaxDiameterMm)
                    _pipes.Add(seg);
            }

            if (Opt.ValidateClearance || Opt.BuildPenetrations || Opt.RoomFinish)
            {
                _boxes.Add(new SceneBox
                {
                    MinX = minX, MinY = minY, MinZ = minZ,
                    MaxX = maxX, MaxY = maxY, MaxZ = maxZ,
                    Name = elName
                });
            }

            if ((Opt.SectionPlan || Opt.RoomFinish) && !double.IsNaN(_sliceZ) &&
                minZ <= _sliceZ && maxZ >= _sliceZ)
                CollectSliceTriangles(verts, quads);
        }

        // В памяти держим только треугольники, пересекающие плоскость среза
        private void CollectSliceTriangles(List<double> verts, List<int> quads)
        {
            for (int q = 0; q + 3 < quads.Count; q += 4)
            {
                int a = quads[q], b = quads[q + 1], c = quads[q + 2];
                if (a * 3 + 2 >= verts.Count || b * 3 + 2 >= verts.Count || c * 3 + 2 >= verts.Count) continue;

                double za = verts[a * 3 + 2], zb = verts[b * 3 + 2], zc = verts[c * 3 + 2];
                if (Math.Min(za, Math.Min(zb, zc)) > _sliceZ) continue;
                if (Math.Max(za, Math.Max(zb, zc)) < _sliceZ) continue;

                int baseIdx = _planVerts.Count / 3;
                foreach (int vi in new[] { a, b, c })
                {
                    _planVerts.Add(verts[vi * 3]);
                    _planVerts.Add(verts[vi * 3 + 1]);
                    _planVerts.Add(verts[vi * 3 + 2]);
                }
                _planFaces.Add(baseIdx); _planFaces.Add(baseIdx + 1);
                _planFaces.Add(baseIdx + 2); _planFaces.Add(baseIdx + 2);
            }
        }

        private bool LooksLikeProfile(double minX, double minY, double minZ,
                                      double maxX, double maxY, double maxZ)
        {
            double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            double lo = Math.Min(dx, Math.Min(dy, dz));
            double hi = Math.Max(dx, Math.Max(dy, dz));
            return lo > 1.0 && hi / lo >= C.SteelMinAspect && hi >= C.SteelMinLengthMm;
        }

        /// <summary>Подпись, которой помечена геометрия без собственного имени.</summary>
        internal const string NoNameLabel = "Без имени (не отнесено к элементу)";

        private static bool IsUnnamed(string name)
        {
            return name == NoNameLabel;
        }

        /// <summary>Имя совпадает с именем файла модели — значит элемента нет.</summary>
        private bool IsModelFileName(string name)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(SourceModel)) return false;
            string file = Path.GetFileName(SourceModel);
            string stem = Path.GetFileNameWithoutExtension(SourceModel);
            return string.Equals(name, file, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, stem, StringComparison.OrdinalIgnoreCase);
        }

        private static string GuessMaterial(string material, string layer)
        {
            string s = ((material ?? "") + " " + (layer ?? "")).ToLowerInvariant();
            if (s.Contains("бетон") || s.Contains("concrete") || s.Contains("жб")) return "Concrete";
            if (s.Contains("алюмин") || s.Contains("alumin")) return "Aluminum";
            if (s.Contains("изоляц") || s.Contains("insul")) return "Insulation";
            if (s.Contains("насос") || s.Contains("pump") || s.Contains("оборуд") || s.Contains("equip")) return "Equipment";
            if (s.Contains("труб") || s.Contains("pipe") || s.Contains("ов-") || s.Contains("вк-")) return "Piping";
            return "Steel";
        }

        // --------------------------------------------------------------------
        // Приём коллизий из Clash Detective
        // --------------------------------------------------------------------
        public void AddClash(double x, double y, double z, double distanceMm,
                             string name, string testName, string status,
                             string guid, DateTime created, string assignedTo)
        {
            ShiftPoint(ref x, ref y, ref z);
            ClashPoints.Add(new ClashPoint(x, y, z, name));

            if (!Opt.ExportBcf) return;
            var topic = new BcfTopic
            {
                Title = string.IsNullOrEmpty(name) ? "Коллизия" : name,
                Description = string.Format(CultureInfo.InvariantCulture,
                    "Проверка: {0}. Глубина пересечения: {1:F1} мм.", testName, Math.Abs(distanceMm)),
                Status = MapBcfStatus(status),
                AssignedTo = assignedTo ?? "",
                CreationDate = created,
                CameraPosX = x, CameraPosY = y - 8000.0, CameraPosZ = z + 4000.0,
                CameraDirX = 0, CameraDirY = 1, CameraDirZ = -0.4,
                CameraUpX = 0, CameraUpY = 0, CameraUpZ = 1
            };
            if (!string.IsNullOrEmpty(guid)) topic.Guid = guid;
            BcfTopics.Add(topic);
        }

        private static string MapBcfStatus(string navisStatus)
        {
            string s = (navisStatus ?? "").ToLowerInvariant();
            if (s.Contains("resolved")) return "Resolved";
            if (s.Contains("approved")) return "Closed";
            return "Active";
        }

        // --------------------------------------------------------------------
        // Финализация: запись побочных файлов
        // --------------------------------------------------------------------
        public string Finish()
        {
            var sb = new StringBuilder();
            string dir = Path.GetDirectoryName(Path.GetFullPath(OutBasePath));
            if (string.IsNullOrEmpty(dir)) dir = ".";
            string baseName = Path.GetFileNameWithoutExtension(OutBasePath);
            string modelName = Path.GetFileNameWithoutExtension(SourceModel ?? OutBasePath);

            // Путь и имя побочного файла определяет профиль выдачи:
            // папка, подпапка раздела и шаблон имени с подстановками.
            Func<string, string> P = suffix =>
            {
                string ext = Path.GetExtension(suffix);
                string stem = string.IsNullOrEmpty(ext) ? suffix
                            : suffix.Substring(0, suffix.Length - ext.Length);
                OutputProfile.Kind kind =
                    ext.Equals(".dxf", StringComparison.OrdinalIgnoreCase) ? OutputProfile.Kind.Coordination :
                    ext.Equals(".bcfzip", StringComparison.OrdinalIgnoreCase) ? OutputProfile.Kind.Coordination :
                    OutputProfile.Kind.Report;
                return O.ResolvePath(kind, dir, baseName, modelName, stem, ext);
            };

            if (ShiftActive)
                Safe(sb, "GeoShift", () =>
                {
                    GeoTransform.SaveGeoreferenceFiles(OutBasePath, Geo, InsUnits);
                    Note(Path.Combine(dir, baseName + ".wld"), "Файл мировой геопривязки");
                    Note(Path.Combine(dir, baseName + "_georef.json"), "Параметры сдвига к нулю");
                    return "записаны " + baseName + ".wld и " + baseName + "_georef.json";
                });

            if (Opt.ExportBoq && O.EmitReports)
                Safe(sb, "BoqCalculator", () =>
                {
                    string p = P("_boq.csv");
                    _boq.ExportCsv(p, C.BoqMinVolumeM3);
                    return "ведомость объёмов (группировка: " + C.BoqGroupBy + ") -> "
                         + Csv(p, "Ведомость объёмов работ", BoqDrops());
                });

            if (Opt.CalcCog && O.EmitReports)
                Safe(sb, "CogCalculator", () =>
                {
                    string p = P("_cog.csv");
                    int written = WriteCogCsv(p, _cog, C.CogMinMassKg);
                    double mass = 0;
                    foreach (var c in _cog.Values) if (c.MassKg >= C.CogMinMassKg) mass += c.MassKg;
                    return string.Format(CultureInfo.InvariantCulture,
                        "элементов {0} (фрагментов {1}), итого {2:F1} т -> {3}",
                        written, _cogFragments, mass / 1000.0,
                        Csv(p, "Массы и центры тяжести", CogDrops()));
                });

            if (Opt.MatchSteel && O.EmitReports)
                Safe(sb, "SteelProfileMatcher", () =>
                {
                    string p = P("_steel_km.csv");
                    SteelProfileMatcher.WriteSteelBomCsv(p, new List<SteelMatchResult>(_steel.Values));
                    return string.Format(CultureInfo.InvariantCulture,
                        "по сортаменту ГОСТ {0}, вне сортамента {1} (из {2} проверенных фрагментов) -> {3}",
                        _steel.Count - _steelCustom, _steelCustom, _steelChecked,
                        Csv(p, "Ведомость металлопроката КМ/КМД", SteelDrops()));
                });

            if (Opt.TracePipes && O.EmitAuxDxf)
                Safe(sb, "PipeTracer", () =>
                {
                    string p = P("_pipes.dxf");
                    WritePipesDxf(p, _pipes);
                    return string.Format(CultureInfo.InvariantCulture,
                        "осевых линий труб: {0} -> {1}", _pipes.Count, Dxf(p, "Осевые линии трубопроводов"));
                });

            if (Opt.GenerateIso && O.EmitAuxDxf)
                Safe(sb, "IsoGenerator", () =>
                {
                    var axes = ToPipeAxes(_pipes);
                    if (axes.Count == 0) return "цилиндрических участков не найдено (нужен ключ --solid)";
                    var net = IsoGenerator.GenerateIsoNetwork(axes);
                    var joints = IsoGenerator.DetectJoints(net, C.IsoJointToleranceMm);
                    string p = P("_iso.dxf");
                    IsoGenerator.WriteIsoDxf(p, net, joints);
                    Dxf(p, "Монтажные изометрии трубопроводов");
                    if (O.EmitReports)
                    {
                        string sp = P("_iso_spools.csv");
                        IsoGenerator.WriteSpoolListCsv(sp, net, joints);
                        Csv(sp, "Журнал трубных заготовок");
                    }
                    return string.Format(CultureInfo.InvariantCulture,
                        "участков {0}, стыков {1} -> {2}", net.Count, joints.Count, Path.GetFileName(p));
                });

            if (Opt.BuildPenetrations)
                Safe(sb, "PenetrationBuilder", () =>
                {
                    var axes = ToPipeAxes(_pipes);
                    var planes = ToConstructionPlanes(_boxes, C.SleeveMinStructureMm);
                    if (axes.Count == 0 || planes.Count == 0)
                        return string.Format(CultureInfo.InvariantCulture,
                            "недостаточно данных: осей труб {0}, конструкций {1}", axes.Count, planes.Count);
                    var pens = PenetrationBuilder.Build(axes, planes, C.SleeveGapFor, C.SleeveExtensionMm);
                    string p = P("_openings.dxf");
                    if (O.EmitAuxDxf) { PenetrationBuilder.WriteStandaloneDxf(p, pens); Dxf(p, "Гильзы и проёмы"); }
                    if (O.EmitReports)
                    {
                        string c = P("_openings.csv");
                        PenetrationBuilder.WriteCsv(c, pens);
                        Csv(c, "Ведомость гильз и проёмов");
                    }
                    return string.Format(CultureInfo.InvariantCulture,
                        "гильз/проёмов: {0} -> {1}", pens.Count, Path.GetFileName(p));
                });

            if (Opt.ValidateClearance)
                Safe(sb, "ClearanceValidator", () =>
                {
                    var viol = ClearanceValidator.Validate(_boxes, C.MinHeadroomCorridorMm, C.ClearanceCellMm);
                    string p = P("_clearance.dxf");
                    if (O.EmitAuxDxf) { ClearanceValidator.WriteStandaloneDxf(p, viol); Dxf(p, "Нарушения высоты проходов"); }
                    if (O.EmitReports)
                    {
                        string c = P("_clearance.csv");
                        ClearanceValidator.WriteCsv(c, viol);
                        Csv(c, "Ведомость нарушений высоты");
                    }
                    return string.Format(CultureInfo.InvariantCulture,
                        "нарушений высоты (<{0:F0} мм): {1} -> {2}",
                        C.MinHeadroomCorridorMm, viol.Count, Path.GetFileName(p));
                });

            // 2D-план и ведомость отделки используют один и тот же срез
            List<List<double[]>> polys = null;
            if (Opt.SectionPlan || Opt.RoomFinish)
            {
                if (_planFaces.Count == 0)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "[Section2Plan] плоскость Z={0:F0} не пересекла геометрию. " +
                        "Фактический диапазон модели: Z от {1:F0} до {2:F0} мм — " +
                        "задайте отметку в этих пределах (параметры модулей или ключ --section-z).",
                        _sliceZ,
                        _geomMinZ == double.MaxValue ? 0 : _geomMinZ,
                        _geomMaxZ == double.MinValue ? 0 : _geomMaxZ));
                else
                    polys = Section2Plan.Slice(_planVerts, _planFaces, _sliceZ, C.SectionDpEpsMm);
            }

            if (Opt.SectionPlan && polys != null && O.EmitAuxDxf)
            {
                var pl = polys;
                Safe(sb, "Section2Plan", () =>
                {
                    string p = P("_plan.dxf");
                    Section2Plan.WriteStandaloneDxf(p, pl, C.SectionLayer);
                    Dxf(p, "2D поэтажный план");
                    return string.Format(CultureInfo.InvariantCulture,
                        "полилиний плана: {0} (Z={1:F0}) -> {2}", pl.Count, _sliceZ, Path.GetFileName(p));
                });
            }

            if (Opt.RoomFinish)
            {
                var pl = polys;
                Safe(sb, "RoomFinishSchedule", () =>
                {
                    if (pl == null) return "нет замкнутых контуров на отметке среза";
                    var rooms = BuildRooms(pl);
                    if (rooms.Count == 0)
                        return string.Format(CultureInfo.InvariantCulture,
                            "замкнутых контуров площадью {0:F1}…{1:F0} м² не найдено",
                            C.RoomMinAreaM2, C.RoomMaxAreaM2);
                    string p = P("_rooms.csv");
                    RoomFinishSchedule.WriteFinishScheduleCsv(p, rooms);
                    Csv(p, "Ведомость отделки помещений");
                    if (O.EmitAuxDxf)
                    {
                        string d = P("_rooms.dxf");
                        RoomFinishSchedule.WriteRoomsDxf(d, rooms);
                        Dxf(d, "Контуры помещений");
                    }
                    double area = 0;
                    foreach (var r in rooms) area += r.FloorAreaM2;
                    return string.Format(CultureInfo.InvariantCulture,
                        "помещений {0}, суммарная площадь пола {1:F1} м² -> {2}",
                        rooms.Count, area, Path.GetFileName(p));
                });
            }

            if (Opt.NeedsClashData) FinishClashes(sb, P);
            if (Opt.MapSchedule4D && O.EmitReports) Finish4D(sb, P);

            if (Opt.Shrinkwrap)
                sb.AppendLine("[Shrinkwrap] Оболочки уровня " + C.ShrinkwrapLevel + " построены при записи геометрии.");

            if (O.EmitIndex)
                Safe(sb, "Индекс ревизии", () => WriteIndexAndDiff(dir, baseName, modelName));

            if (O.EmitProtocol)
                Safe(sb, "Протокол", () => WriteProtocol(dir, baseName, modelName));

            Safe(sb, "Журнал выдач", () => AppendDeliveryLog(dir, baseName, modelName));

            if (ConvertAbort.Requested)
            {
                MarkTruncated();
                sb.AppendLine("ПРОГОН ОСТАНОВЛЕН ДОСРОЧНО: модель обойдена не целиком, " +
                              "все ведомости и отчёты неполные.");
            }

            return sb.ToString();
        }

        // --------------------------------------------------------------------
        // Индекс ревизии и автоматическое сравнение с предыдущей выдачей.
        //
        // Предыдущий индекс ищется по тому же имени: если он есть, рядом
        // ложится отчёт «что изменилось». Именно этот вопрос звучит при
        // каждой еженедельной выдаче.
        // --------------------------------------------------------------------
        private string WriteIndexAndDiff(string dir, string baseName, string modelName)
        {
            string path = O.ResolvePath(OutputProfile.Kind.Protocol, dir, baseName, modelName,
                                        "_index", ".csv");

            List<IndexEntry> previous = null;
            if (O.AutoDiff && File.Exists(path))
            {
                try { previous = RevisionIndex.Read(path); }
                catch { previous = null; }
            }

            var entries = new List<IndexEntry>(_index.Values);
            entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            RevisionIndex.Write(path, entries, modelName);
            Note(path, "Индекс ревизии для сравнения выдач");

            if (previous == null || previous.Count == 0)
                return string.Format(CultureInfo.InvariantCulture,
                    "элементов {0} -> {1} (предыдущей выдачи не найдено, сравнивать не с чем)",
                    entries.Count, Path.GetFileName(path));

            var diff = RevisionIndex.Compare(previous, entries,
                                             O.DiffToleranceMm, O.DiffTriTolerancePct);
            string csv = O.ResolvePath(OutputProfile.Kind.Report, dir, baseName, modelName, "_diff", ".csv");
            RevisionIndex.WriteCsv(csv, diff, "предыдущая выдача", modelName);
            O.NormalizeReport(csv);
            Note(csv, "Отчёт об изменениях с прошлой выдачи");

            if (O.EmitAuxDxf)
            {
                string dxf = O.ResolvePath(OutputProfile.Kind.Coordination, dir, baseName, modelName, "_diff", ".dxf");
                RevisionIndex.WriteDxf(dxf, diff);
                Note(dxf, "Метки изменений в модели");
            }

            string shiftNote = RevisionIndex.BaseShiftNote(diff);
            return RevisionIndex.Summary(diff)
                 + (shiftNote.Length > 0 ? " | " + shiftNote : "")
                 + " -> " + Path.GetFileName(csv);
        }

        // --------------------------------------------------------------------
        // Журнал выдач по объекту
        // --------------------------------------------------------------------
        private string AppendDeliveryLog(string dir, string baseName, string modelName)
        {
            string path = string.IsNullOrEmpty(O.DeliveryLogPath)
                ? Path.Combine(dir, "_журнал_выдач.csv")
                : O.Expand(O.DeliveryLogPath, baseName, modelName, "");

            var rec = new DeliveryRecord
            {
                ProjectCode = O.ProjectCode,
                DocMark = O.DocMark,
                Model = SourceModel ?? "",
                Elements = ElementCount,
                Triangles = TriangleCount,
                FilesOut = _produced.Count,
                Note = ConvertAbort.Requested
                     ? "ПРЕРВАНО ПО ПРЕДЕЛУ ОБЪЁМА — данные неполные" : "",
                Preset = PresetName
            };
            try
            {
                if (!string.IsNullOrEmpty(SourceModel) && File.Exists(SourceModel))
                {
                    var fi = new FileInfo(SourceModel);
                    rec.ModelMb = fi.Length / 1048576.0;
                    rec.ModelChanged = fi.LastWriteTime;
                }
            }
            catch { }

            if (!DeliveryLog.Append(path, rec)) return "не удалось записать: " + path;
            return "запись добавлена -> " + Path.GetFileName(path);
        }

        // --------------------------------------------------------------------
        // Протокол расчёта и опись выданных файлов.
        //
        // Отвечает на вопрос проверяющего «на каких допусках получены цифры»:
        // без этого автоматическую ведомость приходится пересчитывать вручную.
        // --------------------------------------------------------------------
        /// <summary>
        /// Прогон остановлен предохранителем — значит модель обойдена не
        /// целиком, и все ведомости неполные. Молчать об этом нельзя: строк
        /// в ведомости объёмов оказалось 3066 вместо 4417, а файл выглядел
        /// как обычная законченная выдача. По такой сметчик посчитает смету.
        /// </summary>
        private void MarkTruncated()
        {
            const string warn = "ВНИМАНИЕ: ПРОГОН ОСТАНОВЛЕН ДОСРОЧНО — " +
                                "МОДЕЛЬ ОБОЙДЕНА НЕ ЦЕЛИКОМ, ДАННЫЕ НЕПОЛНЫЕ";

            foreach (var it in _produced)
            {
                string path = it[0];
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var enc = O.ReportEncoding;
                    var lines = new List<string>(File.ReadAllLines(path, enc));
                    // Строка sep= должна остаться первой, иначе Excel её не поймёт.
                    int at = (lines.Count > 0 && lines[0].StartsWith("sep=",
                              StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
                    lines.Insert(at, warn);
                    File.WriteAllLines(path, lines, enc);
                }
                catch { }
            }
        }

        private string WriteProtocol(string dir, string baseName, string modelName)
        {
            string path = O.ResolvePath(OutputProfile.Kind.Protocol, dir, baseName, modelName,
                                        "_протокол", ".txt");
            var ci = CultureInfo.InvariantCulture;
            var w = new StringBuilder();

            w.AppendLine("ПРОТОКОЛ РАСЧЁТА И ОПИСЬ ВЫДАННЫХ ФАЙЛОВ");
            w.AppendLine(new string('=', 78));
            w.AppendLine();
            w.AppendLine("Исходная модель      : " + (SourceModel ?? ""));
            try
            {
                if (!string.IsNullOrEmpty(SourceModel) && File.Exists(SourceModel))
                {
                    var fi = new FileInfo(SourceModel);
                    w.AppendLine(string.Format(ci, "Размер / изменена    : {0:F2} МБ / {1:yyyy-MM-dd HH:mm}",
                                 fi.Length / 1048576.0, fi.LastWriteTime));
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(O.ProjectCode)) w.AppendLine("Шифр объекта         : " + O.ProjectCode);
            if (!string.IsNullOrEmpty(O.DocMark))     w.AppendLine("Марка комплекта      : " + O.DocMark);
            w.AppendLine(string.Format(ci, "Дата расчёта         : {0:yyyy-MM-dd HH:mm}", DateTime.Now));
            w.AppendLine("Программа            : NWD2DWG v3.5 (GNU GPL v3)");
            if (ShiftActive)
                w.AppendLine(string.Format(ci, "Сдвиг к нулю         : dX={0:F1} dY={1:F1} dZ={2:F1} мм",
                             Geo.OffsetX, Geo.OffsetY, Geo.OffsetZ));
            else
                w.AppendLine("Сдвиг к нулю         : не выполнялся");
            w.AppendLine();

            w.AppendLine("ПРИМЕНЁННЫЕ ДОПУСКИ РАСЧЁТА");
            w.AppendLine(new string('-', 78));
            foreach (var f in typeof(AdvancedConfig).GetFields(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                object v = f.GetValue(C);
                string s = v is double ? ((double)v).ToString("0.####", ci)
                         : v is bool ? (((bool)v) ? "да" : "нет")
                         : Convert.ToString(v, ci);
                w.AppendLine("  " + f.Name.PadRight(28) + " " + s);
            }
            w.AppendLine();

            w.AppendLine("ОПИСЬ ВЫДАННЫХ ФАЙЛОВ");
            w.AppendLine(new string('-', 78));
            if (ConvertAbort.Requested)
            {
                w.AppendLine();
                w.AppendLine("==============================================================================");
                w.AppendLine("  ПРОГОН ОСТАНОВЛЕН ДОСРОЧНО");
                w.AppendLine("  Модель обойдена не целиком: сработал предел объёма выдачи либо");
                w.AppendLine("  закончилось место на диске. Все ведомости ниже — НЕПОЛНЫЕ и для");
                w.AppendLine("  выпуска документации непригодны.");
                w.AppendLine("  Что делать: включить упрощение сетки, габаритные оболочки либо");
                w.AppendLine("  отказаться от записи геометрии, после чего повторить прогон.");
                w.AppendLine("==============================================================================");
                w.AppendLine();
            }

            if (_produced.Count == 0) w.AppendLine("  (побочные файлы не выгружались)");
            int n = 0;
            foreach (var it in _produced)
            {
                n++;
                long size = 0;
                try { if (File.Exists(it[0])) size = new FileInfo(it[0]).Length; } catch { }
                w.AppendLine(string.Format(ci, "{0,3}. {1}", n, Path.GetFileName(it[0])));
                // «0 КБ» у ведомости на две строки выглядит как пустой файл
                w.AppendLine(string.Format(ci, "     {0} · {1}", it[1],
                    size >= 1024 ? string.Format(ci, "{0:F0} КБ", size / 1024.0)
                                 : string.Format(ci, "{0} Б", size)));
            }
            w.AppendLine();
            w.AppendLine(new string('=', 78));
            w.AppendLine("Значения получены автоматически и подлежат проверке проектировщиком.");

            File.WriteAllText(path, w.ToString(), O.ReportEncoding);
            return string.Format(ci, "записан {0} (файлов в описи: {1})", Path.GetFileName(path), n);
        }

        // --------------------------------------------------------------------
        // Коллизии: кластеризация DBSCAN и пакет BCF 2.1
        // --------------------------------------------------------------------
        private void FinishClashes(StringBuilder sb, Func<string, string> P)
        {
            if (ClashPoints.Count == 0)
            {
                sb.AppendLine("[Коллизии] В модели нет сохранённых проверок Clash Detective " +
                              "(или все результаты отфильтрованы настройками).");
                return;
            }

            if (Opt.ClusterClashes)
                Safe(sb, "ClashClusterer", () =>
                {
                    var clusters = ClashClusterer.Cluster(ClashPoints, C.ClashEpsilonMm, C.ClashMinPts);
                    string p = P("_clashes.dxf");
                    if (O.EmitAuxDxf) { ClashClusterer.WriteStandaloneDxf(p, clusters); Dxf(p, "Кластеры коллизий"); }
                    if (O.EmitReports)
                    {
                        string c = P("_clashes.csv");
                        WriteClusterCsv(c, clusters);
                        Csv(c, "Ведомость кластеров коллизий");
                    }
                    int noise = 0;
                    foreach (var pt in ClashPoints) if (pt.ClusterId <= 0) noise++;
                    return string.Format(CultureInfo.InvariantCulture,
                        "коллизий {0} -> кластеров {1} (шум {2}, ε={3:F0} мм) -> {4}",
                        ClashPoints.Count, clusters.Count, noise, C.ClashEpsilonMm, Path.GetFileName(p));
                });

            if (Opt.ExportBcf)
                Safe(sb, "BcfExporter", () =>
                {
                    string p = P("_clashes.bcfzip");
                    BcfExporter.ExportBcfZip(p, BcfTopics, C.BcfAuthor);
                    Dxf(p, "Пакет коллизий BCF 2.1");
                    return string.Format(CultureInfo.InvariantCulture,
                        "топиков BCF 2.1: {0} -> {1}", BcfTopics.Count, Path.GetFileName(p));
                });
        }

        private static void WriteClusterCsv(string path, List<ClashCluster> clusters)
        {
            var ci = CultureInfo.InvariantCulture;
            using (var w = new StreamWriter(path, false, Encoding.UTF8))
            {
                w.WriteLine("sep=;");
                w.WriteLine("Кластер;Коллизий;Центр X;Центр Y;Центр Z;Представитель");
                foreach (var cl in clusters)
                    w.WriteLine(string.Format(ci, "{0};{1};{2:F0};{3:F0};{4:F0};{5}",
                        cl.Id, cl.Points.Count, cl.Cx, cl.Cy, cl.Cz,
                        cl.Points.Count > 0 ? (cl.Points[0].Name ?? "").Replace(';', ',') : ""));
            }
        }

        // --------------------------------------------------------------------
        // 4D: статус по графику на дату среза
        // --------------------------------------------------------------------
        private void Finish4D(StringBuilder sb, Func<string, string> P)
        {
            if (Tasks4D.Count == 0)
            {
                sb.AppendLine(C.ScheduleSource == "File"
                    ? "[4D] Не выполнено: файл графика не задан или пуст (ключ --schedule <файл>)."
                    : "[4D] Не выполнено: в модели нет задач TimeLiner. " +
                      "Переключите источник на внешний файл в параметрах модулей.");
                return;
            }

            Safe(sb, "ScheduleMapper", () =>
            {
                DateTime date = C.StatusDate();
                string p = P("_4d.csv");
                var ci = CultureInfo.InvariantCulture;
                int done = 0, run = 0, late = 0, future = 0, rows = 0;

                using (var w = new StreamWriter(p, false, Encoding.UTF8))
                {
                    w.WriteLine("sep=;");
                    w.WriteLine(string.Format(ci, "Дата среза;{0:yyyy-MM-dd};Источник;{1}", date, ScheduleOrigin));
                    w.WriteLine("Задача;WBS;План начало;План окончание;Факт начало;Факт окончание;" +
                                "Готовность %;Элементов;Статус;Отставание, дн.");
                    foreach (var t in Tasks4D)
                    {
                        int links;
                        TaskLinkCounts.TryGetValue(t.Uid ?? "", out links);
                        if (C.ScheduleOnlyLinked && links == 0) continue;

                        Task4DStatus st = Status(t, date);
                        switch (st)
                        {
                            case Task4DStatus.Completed: done++; break;
                            case Task4DStatus.InProgress: run++; break;
                            case Task4DStatus.Delayed: late++; break;
                            default: future++; break;
                        }
                        double drift = st == Task4DStatus.Delayed
                            ? (date - t.PlannedFinish).TotalDays : 0;
                        rows++;

                        w.WriteLine(string.Format(ci,
                            "{0};{1};{2:yyyy-MM-dd};{3:yyyy-MM-dd};{4};{5};{6:F0};{7};{8};{9:F0}",
                            (t.Name ?? "").Replace(';', ','), (t.Wbs ?? "").Replace(';', ','),
                            t.PlannedStart, t.PlannedFinish,
                            t.ActualStart.HasValue ? t.ActualStart.Value.ToString("yyyy-MM-dd", ci) : "",
                            t.ActualFinish.HasValue ? t.ActualFinish.Value.ToString("yyyy-MM-dd", ci) : "",
                            t.PercentComplete, links, StatusText(st), drift));
                    }
                    w.WriteLine(string.Format(ci,
                        "ИТОГО;{0} задач;;;;;;;выполнено {1} / в работе {2} / отставание {3} / не начато {4};",
                        rows, done, run, late, future));
                }

                return string.Format(ci,
                    "задач {0} на {1:yyyy-MM-dd} ({2}): выполнено {3}, в работе {4}, отставание {5} -> {6}",
                    rows, date, ScheduleOrigin, done, run, late,
                    Csv(p, "Статус по календарному графику"));
            });
        }

        private static Task4DStatus Status(ScheduleTask t, DateTime date)
        {
            bool finished = t.ActualFinish.HasValue || t.PercentComplete >= 99.0;
            if (date >= t.PlannedFinish)
                return finished ? Task4DStatus.Completed : Task4DStatus.Delayed;
            if (date >= t.PlannedStart)
                return finished ? Task4DStatus.Completed
                     : ((t.PercentComplete > 0 || t.ActualStart.HasValue)
                        ? Task4DStatus.InProgress : Task4DStatus.Delayed);
            return Task4DStatus.NotStarted;
        }

        private static string StatusText(Task4DStatus s)
        {
            switch (s)
            {
                case Task4DStatus.Completed:  return "Выполнено";
                case Task4DStatus.InProgress: return "В работе";
                case Task4DStatus.Delayed:    return "Отставание";
                default:                      return "Не начато";
            }
        }

        // --------------------------------------------------------------------
        // Помещения из замкнутых контуров среза
        // --------------------------------------------------------------------
        private List<RoomData> BuildRooms(List<List<double[]>> polys)
        {
            var rooms = new List<RoomData>();
            int n = 0;
            foreach (var poly in polys)
            {
                if (poly.Count < 4) continue;

                // контур должен быть замкнут: начало и конец в одной точке
                double dx = poly[0][0] - poly[poly.Count - 1][0];
                double dy = poly[0][1] - poly[poly.Count - 1][1];
                if (dx * dx + dy * dy > 4.0) continue;

                double areaM2 = Math.Abs(ShoelaceArea(poly)) * 1e-6;
                if (areaM2 < C.RoomMinAreaM2 || areaM2 > C.RoomMaxAreaM2) continue;

                n++;
                var room = new RoomData
                {
                    Number = n.ToString(CultureInfo.InvariantCulture),
                    Name = "Помещение " + n,
                    HeightMm = C.RoomHeightMm,
                    FloorType = "по проекту",
                    WallType = "по проекту",
                    CeilingType = "по проекту"
                };
                foreach (var pt in poly) room.Contour2D.Add(new[] { pt[0], pt[1] });

                if (C.RoomDeductOpenings) AddOpenings(room, poly);
                room.Calculate();
                rooms.Add(room);
            }
            return rooms;
        }

        // Проёмы ищем среди элементов, чьё имя похоже на дверь или окно и чей
        // центр лежит на границе контура помещения.
        private void AddOpenings(RoomData room, List<double[]> poly)
        {
            foreach (var b in _boxes)
            {
                string s = (b.Name ?? "").ToLowerInvariant();
                bool isDoor = s.Contains("двер") || s.Contains("door") || s.Contains("ворот");
                bool isWindow = s.Contains("окн") || s.Contains("window") || s.Contains("витраж");
                if (!isDoor && !isWindow) continue;

                double cx = (b.MinX + b.MaxX) / 2, cy = (b.MinY + b.MaxY) / 2;
                if (DistanceToBoundary(poly, cx, cy) > 800.0) continue;

                double w = Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY);
                double h = b.MaxZ - b.MinZ;
                if (w < 100 || h < 100) continue;

                room.Openings.Add(new RoomOpening { WidthMm = w, HeightMm = h, IsDoor = isDoor });
            }
        }

        private static double ShoelaceArea(List<double[]> poly)
        {
            double a = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                double[] p1 = poly[i], p2 = poly[(i + 1) % poly.Count];
                a += p1[0] * p2[1] - p2[0] * p1[1];
            }
            return a / 2.0;
        }

        private static double DistanceToBoundary(List<double[]> poly, double x, double y)
        {
            double best = double.MaxValue;
            for (int i = 0; i < poly.Count - 1; i++)
            {
                double d = PointSegDistance(x, y, poly[i][0], poly[i][1], poly[i + 1][0], poly[i + 1][1]);
                if (d < best) best = d;
            }
            return best;
        }

        private static double PointSegDistance(double px, double py,
                                               double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-9 ? 0 : ((px - ax) * dx + (py - ay) * dy) / len2;
            t = Math.Max(0, Math.Min(1, t));
            double qx = ax + t * dx - px, qy = ay + t * dy - py;
            return Math.Sqrt(qx * qx + qy * qy);
        }

        // --------------------------------------------------------------------
        // Служебное
        // --------------------------------------------------------------------
        private static void Safe(StringBuilder sb, string tag, Func<string> action)
        {
            try { sb.AppendLine("[" + tag + "] " + action()); }
            catch (Exception ex) { sb.AppendLine("[" + tag + "] ОШИБКА: " + ex.Message); }
        }

        private static List<PipeAxis> ToPipeAxes(List<PipeSegment> pipes)
        {
            var res = new List<PipeAxis>(pipes.Count);
            foreach (var s in pipes)
                res.Add(new PipeAxis
                {
                    Ax = s.StartX, Ay = s.StartY, Az = s.StartZ,
                    Bx = s.EndX,   By = s.EndY,   Bz = s.EndZ,
                    DN = s.Diameter,
                    SystemName = s.SystemName
                });
            return res;
        }

        // Стены и перекрытия опознаём по форме габаритного параллелепипеда
        private static List<ConstructionPlane> ToConstructionPlanes(List<SceneBox> boxes, double minThickness)
        {
            var res = new List<ConstructionPlane>();
            foreach (var b in boxes)
            {
                double dx = b.MaxX - b.MinX, dy = b.MaxY - b.MinY, dz = b.MaxZ - b.MinZ;
                if (dx <= 0 || dy <= 0 || dz <= 0) continue;

                double nx = 0, ny = 0, nz = 0, thick = 0;
                if (dz <= 400 && dx >= 1000 && dy >= 1000) { nz = 1; thick = dz; }        // перекрытие
                else if (dx <= 600 && dy >= 1000 && dz >= 1500) { nx = 1; thick = dx; }   // стена вдоль Y
                else if (dy <= 600 && dx >= 1000 && dz >= 1500) { ny = 1; thick = dy; }   // стена вдоль X
                else continue;

                // тоньше порога — облицовка или лист, гильза не ставится
                if (thick < minThickness) continue;

                double cx = (b.MinX + b.MaxX) / 2, cy = (b.MinY + b.MaxY) / 2, cz = (b.MinZ + b.MaxZ) / 2;
                res.Add(new ConstructionPlane
                {
                    Nx = nx, Ny = ny, Nz = nz,
                    D = nx * cx + ny * cy + nz * cz,
                    Thickness = thick,
                    ElementName = b.Name,
                    ElementType = nz > 0 ? "Floor" : "Wall",
                    MinX = b.MinX, MinY = b.MinY, MinZ = b.MinZ,
                    MaxX = b.MaxX, MaxY = b.MaxY, MaxZ = b.MaxZ
                });
            }
            return res;
        }

        private static int WriteCogCsv(string path, Dictionary<string, CogAgg> items, double minMass)
        {
            var ci = CultureInfo.InvariantCulture;
            var list = new List<CogAgg>(items.Values);
            list.Sort((a, b) => b.MassKg.CompareTo(a.MassKg));
            int written = 0;
            using (var w = new StreamWriter(path, false, Encoding.UTF8))
            {
                w.WriteLine("sep=;");
                w.WriteLine("Элемент;Материал;Плотность кг/м3;Фрагментов;Объём м3;Масса кг;CoG X;CoG Y;CoG Z");
                double totalMass = 0, totalVol = 0, mx = 0, my = 0, mz = 0;
                foreach (var c in list)
                {
                    if (c.MassKg < minMass) continue;
                    w.WriteLine(string.Format(ci, "{0};{1};{2:F0};{3};{4:F4};{5:F2};{6:F1};{7:F1};{8:F1}",
                        (c.Name ?? "").Replace(';', ','), c.Material, c.Density, c.Fragments,
                        c.VolumeM3, c.MassKg, c.Mx / c.MassKg, c.My / c.MassKg, c.Mz / c.MassKg));
                    written++;
                    totalMass += c.MassKg; totalVol += c.VolumeM3;
                    mx += c.Mx; my += c.My; mz += c.Mz;
                }
                if (totalMass > 0)
                    w.WriteLine(string.Format(ci, "ИТОГО СБОРКА;;;;{0:F4};{1:F2};{2:F1};{3:F1};{4:F1}",
                        totalVol, totalMass, mx / totalMass, my / totalMass, mz / totalMass));
            }
            return written;
        }

        private static void WritePipesDxf(string path, List<PipeSegment> pipes)
        {
            using (var w = new StreamWriter(path, false, Encoding.Default))
            {
                w.WriteLine("0\nSECTION\n2\nHEADER");
                w.WriteLine("9\n$ACADVER\n1\nAC1015");
                w.WriteLine("0\nENDSEC");
                w.WriteLine("0\nSECTION\n2\nTABLES");
                w.WriteLine("0\nTABLE\n2\nLAYER\n70\n2");
                w.WriteLine("0\nLAYER\n2\n0\n70\n0\n62\n7\n6\nCONTINUOUS");
                w.WriteLine("0\nLAYER\n2\n_PIPE_AXES\n70\n0\n62\n4\n6\nCONTINUOUS");
                w.WriteLine("0\nENDTAB");
                w.WriteLine("0\nENDSEC");
                w.WriteLine("0\nSECTION\n2\nENTITIES");
                PipeTracer.WritePipeAxesToDxf(w, pipes);
                w.WriteLine("0\nENDSEC");
                w.WriteLine("0\nEOF");
            }
        }
    }
}
