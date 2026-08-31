// ============================================================================
//  NWD2DWG — конвертер Navisworks (.nwd/.nwc/.nwf) -> AutoCAD (.dxf/.dwg)
//  v1.0
//
//  Работает через официальный Navisworks API (Automation + ComApi).
//  Требования на машине пользователя:
//    - Windows 10/11 x64 (.NET Framework 4.7.2+ уже встроен в систему)
//    - Navisworks Manage/Simulate (2017-2024), установлен и лицензирован
//    - AutoCAD (2013+, для режима "DWG через AutoCAD") — опционально
//
//  Режимы вывода:
//    - DXF PolyfaceMesh (рекомендуется: компактно, без AutoCAD)
//    - DXF 3DFACE     (максимальная совместимость, крупнее файл)
//    - DWG через COM-автоматизацию установленного AutoCAD
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using NWD2DWG.Plugin;

namespace NWD2DWG
{
    // ------------------------------------------------------------------------
    // Глобальные опции
    // ------------------------------------------------------------------------
    public enum OutFormat { DxfPolyface = 0, Dxf3dFace = 1, Dwg = 2, Gltf = 3, Glb = 4, Ifc = 5 }

    public class AppOptions
    {
        public string Input;
        public string OutputDir = "";
        public bool Batch;
        public OutFormat Format = OutFormat.DxfPolyface;
        public bool ShowNavisworks = true;
        public bool ShowAutoCad = true;
        public bool SkipHidden = true;
        public bool WithColors = false;
        public bool LayersPerItem = false;
        public bool SplitDisciplines = false;
        public string NavisworksDir = ""; // ручное указание (CLI: --navis)

        // === v2.0 ===
        public int DecimatePercent = 0;        // 0-90, степень упрощения меша
        public bool SolidDetect = false;       // пытаться распознать цилиндры/коробки
        public bool TransferXData = false;     // BIM-свойства в XData
        public string SelectionSets = "";      // фильтр по Selection Sets (через запятую)
        public double[] SectionBox = null;     // AABB [minX,minY,minZ,maxX,maxY,maxZ]
        public bool TransferMaterials = false; // прозрачность и материалы
        public int ParallelThreads = 0;        // 0 = auto, 1 = single
        public string WatchFolder = "";        // папка для Watchdog
        public int WatchInterval = 5;          // интервал опроса (сек)

        // === v3.0 ===
        public bool GeoShift = true;           // сдвиг к (0,0,0) + файл привязки .wld
        public bool ExportGrids = true;        // экспорт осей и уровней (_GRIDS / _LEVELS)
        public bool TracePipes = false;        // трассировка осевых линий трубопроводов
        public bool ExportBoq = false;         // расчет сметы ВОР в Excel/CSV
        public bool ExportBcf = false;         // экспорт коллизий в BCF 2.1 (.bcfzip)
        public bool Anonymize = false;         // очистка конфиденциальных атрибутов
        public double TileSize = 0.0;          // 0 = нет нарезки, иначе размер куба (мм)

        // === v3.1 – v3.4 (Полный стек экспертизы, EPC и 4D) ===
        public bool ClusterClashes = false;    // кластеризация коллизий DBSCAN 3D
        public bool SectionPlan = false;       // генерация 2D поэтажных планов / Z-срез
        public bool PurgeDxf = false;          // глубокая бинарная чистка DXF от мусора
        public bool BuildPenetrations = false; // авторасстановка гильз и проемов (DN+50)
        public bool ValidateClearance = false; // проверка высоты проходов (СП 118.13330)
        public bool MatchSteel = false;        // сортамент стали ГОСТ (КМ/КМД)
        public bool CalcCog = false;           // расчет центра масс блока (CoG Гаусс)
        public bool GenerateIso = false;       // изометрические монтажные схемы ГОСТ 2.317
        public bool MapSchedule4D = false;     // 4D календарное планирование XML/CSV
        public bool Shrinkwrap = false;        // защита IP и OBB-оболочки оборудования
        public bool RoomFinish = false;        // ведомость отделки помещений ГОСТ 21.501
        public string ScheduleFile = "";        // файл графика для 4D (--schedule)
        public AdvancedConfig AdvConfig = AdvancedConfig.Load(); // допуски расчёта
        public OutputProfile OutProfile = OutputProfile.Load();  // куда и как писать результат
    }

    // ------------------------------------------------------------------------
    // Логирование
    // ------------------------------------------------------------------------
    public static class Log
    {
        private static readonly object Lock = new object();
        private static readonly List<Action<string>> Sinks = new List<Action<string>>();
        private static readonly StringBuilder Buffer = new StringBuilder();
        private static string _file;

        public static void AddSink(Action<string> sink) { lock (Lock) Sinks.Add(sink); }
        public static void RemoveSink(Action<string> sink) { lock (Lock) Sinks.Remove(sink); }

        public static void SetFile(string path)
        {
            lock (Lock)
            {
                _file = path;
                try { if (!string.IsNullOrEmpty(path)) { Directory.CreateDirectory(Path.GetDirectoryName(path)); } } catch { }
                if (Buffer.Length > 0 && !string.IsNullOrEmpty(path))
                {
                    try { File.AppendAllText(path, Buffer.ToString(), Encoding.UTF8); Buffer.Length = 0; } catch { }
                }
            }
        }

        public static string FilePath { get { lock (Lock) return _file; } }

        public static void Write(string s)
        {
            lock (Lock)
            {
                string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s;
                Buffer.AppendLine(line);
                foreach (var sink in Sinks.ToArray())
                {
                    try { sink(line); } catch { }
                }
                if (!string.IsNullOrEmpty(_file) && Buffer.Length > 4096)
                {
                    try { File.AppendAllText(_file, Buffer.ToString(), Encoding.UTF8); Buffer.Length = 0; } catch { }
                }
            }
        }

        public static void Flush()
        {
            lock (Lock)
            {
                if (!string.IsNullOrEmpty(_file) && Buffer.Length > 0)
                {
                    try { File.AppendAllText(_file, Buffer.ToString(), Encoding.UTF8); Buffer.Length = 0; } catch { }
                }
            }
        }
    }

    // ------------------------------------------------------------------------
    // Вспомогательный доступ к управляемым и COM-объектам Navisworks
    // (сборки Navisworks грузятся во время выполнения — здесь нет ссылок
    //  времени компиляции, всё через reflection/dynamic)
    // ------------------------------------------------------------------------
    public static class Dyn
    {
        public static bool IsCom(object o)
        {
            if (o == null) return false;
            try { return Marshal.IsComObject(o); }
            catch { return false; }
        }

        public static object Get(object o, string name)
        {
            if (o == null) return null;
            try
            {
                if (IsCom(o))
                {
                    dynamic d = o;
                    switch (name)
                    {
                        case "Document": return d.Document;
                        case "Models": return d.Models;
                        case "Paths": return d.Paths;
                        case "Fragments": return d.Fragments;
                        case "coord": return d.coord;
                        case "normal": return d.normal;
                        case "path": return d.path;
                        case "ArrayData": return d.ArrayData;
                        case "Matrix": return d.Matrix;
                        case "IsHidden": return d.IsHidden;
                        default: return null;
                    }
                }
                PropertyInfo pi = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (pi == null) return null;
                return pi.GetValue(o, null);
            }
            catch { return null; }
        }

        public static object Call(object o, string name, params object[] args)
        {
            if (o == null) return null;
            try
            {
                if (IsCom(o))
                {
                    dynamic d = o;
                    switch (name)
                    {
                        case "Paths": return d.Paths();
                        case "Fragments": return d.Fragments();
                        case "GetLocalToWorldMatrix": return d.GetLocalToWorldMatrix();
                        case "GetLocalToWorldTransformMatrix": return d.GetLocalToWorldTransformMatrix();
                        case "GenerateSimplePrimitives":
                            d.GenerateSimplePrimitives(args[0], args[1]);
                            return true;
                        default: return null;
                    }
                }
                // managed: ищем метод по имени и количеству параметров
                MethodInfo found = null;
                foreach (MethodInfo mi in o.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (mi.Name == name && mi.GetParameters().Length == args.Length)
                    { found = mi; break; }
                }
                if (found == null) return null;
                return found.Invoke(o, args);
            }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------------
    // Поиск установленного Navisworks
    // ------------------------------------------------------------------------
    public class NwInstall
    {
        public string Dir;
        public string DisplayName;
        public bool HasAutomation;
        public bool HasApi;

        public override string ToString()
        {
            return DisplayName + "  [" + Dir + "]"
                 + (HasAutomation ? "  automation:OK" : "  automation:НЕТ")
                 + (HasApi ? "  api:OK" : "  api:НЕТ");
        }
    }

    public static class NwDetect
    {
        public static List<NwInstall> Find()
        {
            var result = new List<NwInstall>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) реестр: записи установки
            foreach (string hive in new[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(hive))
                    {
                        if (rk == null) continue;
                        foreach (string sub in rk.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey sk = rk.OpenSubKey(sub))
                                {
                                    if (sk == null) continue;
                                    string dn = sk.GetValue("DisplayName") as string;
                                    string loc = sk.GetValue("InstallLocation") as string;
                                    if (string.IsNullOrEmpty(dn) || !dn.ToLowerInvariant().Contains("navisworks")) continue;
                                    if (string.IsNullOrEmpty(loc)) loc = @"C:\Program Files\Autodesk\" + dn;
                                    if (!Directory.Exists(loc)) continue;
                                    AddCandidate(result, seen, loc, dn);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            // 2) стандартные пути
            foreach (string root in new[] { @"C:\Program Files\Autodesk", @"C:\Program Files (x86)\Autodesk" })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        string name = Path.GetFileName(dir);
                        if (name.ToLowerInvariant().StartsWith("navisworks"))
                        {
                            if (name.ToLowerInvariant().Contains("freedom")) continue; // нет Automation API
                            AddCandidate(result, seen, dir, name);
                        }
                    }
                }
                catch { }
            }

            // сортировка: с Automation API первыми, затем по убыванию версии в имени
            result.Sort((a, b) =>
            {
                int c = b.HasAutomation.CompareTo(a.HasAutomation);
                if (c != 0) return c;
                return CompareNames(b.DisplayName, a.DisplayName);
            });
            return result;
        }

        static int CompareNames(string a, string b)
        {
            int va = VersionOf(a), vb = VersionOf(b);
            return va.CompareTo(vb);
        }

        static int VersionOf(string s)
        {
            int v = 0;
            foreach (Match m in System.Text.RegularExpressions.Regex.Matches(s ?? "", @"(\d{4})"))
            {
                int x; if (int.TryParse(m.Groups[1].Value, out x)) { if (x > 1900 && x < 2100) { v = Math.Max(v, x); } }
            }
            return v;
        }

        static void AddCandidate(List<NwInstall> result, HashSet<string> seen, string dir, string displayName)
        {
            string full;
            try { full = Path.GetFullPath(dir.TrimEnd('\\', '/')); }
            catch { return; }
            if (!seen.Add(full)) return;
            var ni = new NwInstall
            {
                Dir = full,
                DisplayName = displayName,
                HasAutomation = File.Exists(Path.Combine(full, "Autodesk.Navisworks.Automation.dll")),
                HasApi = File.Exists(Path.Combine(full, "Autodesk.Navisworks.Api.dll"))
            };
            result.Add(ni);
        }
    }

    // ------------------------------------------------------------------------
    // Загрузчик сборок Navisworks и доступ к типам API
    // ------------------------------------------------------------------------
    public class NwLoader
    {
        public string Dir;
        public Type AutomationType;      // Autodesk.Navisworks.Api.Automation.NavisworksApplication
        public Type ApiApplicationType;  // Autodesk.Navisworks.Api.Application (static)
        public MethodInfo ToInwOaPath;   // ComApiBridge.ToInwOaPath
        public object NormalEnum;        // nwEVertexProperty.eNORMAL
        public Type CallbackIface;       // InwSimplePrimitivesCB
        public Type CallbackProxyType;   // динамическая реализация InwSimplePrimitivesCB
        public string LastError;

        public bool Load(string dir)
        {
            Dir = dir;
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
                {
                    string name = new AssemblyName(e.Name).Name;
                    foreach (string candidate in new[] {
                        Path.Combine(dir, name + ".dll"),
                        Path.Combine(Path.GetDirectoryName(dir ?? ".") ?? ".", name + ".dll") })
                    {
                        try { if (File.Exists(candidate)) return Assembly.LoadFrom(candidate); } catch { }
                    }
                    return null;
                };

                int loaded = 0, skipped = 0;
                var dlls = Directory.GetFiles(dir, "Autodesk.Navisworks*.dll");
                Array.Sort(dlls);
                foreach (string dll in dlls)
                {
                    try { Assembly.LoadFrom(dll); loaded++; }
                    catch (Exception ex) { skipped++; Log.Write("пропущена сборка " + Path.GetFileName(dll) + ": " + ex.GetType().Name); }
                }
                Log.Write("загружено сборок Navisworks: " + loaded + ", пропущено: " + skipped);

                AutomationType = FindType("NavisworksApplication", ns => ns != null && ns.Contains("Automation"));
                ApiApplicationType = FindType("Application", ns => ns == "Autodesk.Navisworks.Api");

                Type comBridge = FindType("ComApiBridge", ns => ns != null && ns.Contains("ComApi"));
                if (comBridge != null)
                {
                    try
                    {
                        Type modelItemType = FindType("ModelItem", null);
                        ToInwOaPath = modelItemType != null
                            ? comBridge.GetMethod("ToInwOaPath",
                                BindingFlags.Public | BindingFlags.Static, null, new[] { modelItemType }, null)
                            : null;
                        if (ToInwOaPath == null)
                            ToInwOaPath = comBridge.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                .FirstOrDefault(m => m.Name == "ToInwOaPath" && m.GetParameters().Length == 1);
                    }
                    catch (Exception ex) { Log.Write("поиск ToInwOaPath: " + ex.Message); }
                }

                Type enumType = FindType("nwEVertexProperty", null);
                if (enumType != null)
                {
                    try { NormalEnum = Enum.Parse(enumType, "eNORMAL"); }
                    catch { try { NormalEnum = Enum.ToObject(enumType, 1); } catch { } }
                }

                CallbackIface = FindType("InwSimplePrimitivesCB", null);

                return Check();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Write("ОШИБКА загрузки API: " + ex);
                return false;
            }
        }

        Type FindType(string name, Func<string, bool> nsFilter)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!(asm.FullName ?? "").Contains("Autodesk.Navisworks")) continue;
                try
                {
                    foreach (Type t in asm.GetTypes())
                    {
                        if (t.Name == name && (nsFilter == null || nsFilter(t.Namespace)))
                            return t;
                    }
                }
                catch { }
            }
            return null;
        }

        public bool Check()
        {
            var problems = new List<string>();
            if (AutomationType == null) problems.Add("не найден тип NavisworksApplication (Autodesk.Navisworks.Automation.dll)");
            if (ApiApplicationType == null) problems.Add("не найден тип Autodesk.Navisworks.Api.Application");
            if (ToInwOaPath == null) problems.Add("не найден ComApiBridge.ToInwOaPath");
            if (NormalEnum == null) problems.Add("не найден nwEVertexProperty.eNORMAL");
            if (CallbackIface == null) problems.Add("не найден интерфейс InwSimplePrimitivesCB");
            if (problems.Count > 0)
            {
                LastError = string.Join("; ", problems);
                foreach (string p in problems) Log.Write("ПРОБЛЕМА: " + p);
                return false;
            }
            return true;
        }
    }

    // ------------------------------------------------------------------------
    // Приёмник примитивов (реализация InwSimplePrimitivesCB, создаётся
    // динамически в CallbackFactory) и построитель сетки
    // ------------------------------------------------------------------------
    public class PrimitiveSink
    {
        public double[] Matrix = new double[16];
        public readonly List<double> Verts = new List<double>();
        public readonly List<int> Quads = new List<int>();
        private readonly Dictionary<ulong, int> _index = new Dictionary<ulong, int>();
        public int TriCount;
        public int SkippedDegenerate;
        public int VertexReadErrors;
        public int HashCollisions;

        public void Reset(double[] m)
        {
            Array.Copy(m, Matrix, 16);
            Verts.Clear();
            Quads.Clear();
            _index.Clear();
            TriCount = 0;
            SkippedDegenerate = 0;
            VertexReadErrors = 0;
            HashCollisions = 0;
        }

        // вызывается из динамически созданной реализации InwSimplePrimitivesCB
        public void Handle(string method, object[] args)
        {
            if (method == "Triangle" && args != null && args.Length >= 3)
            {
                double x1, y1, z1, x2, y2, z2, x3, y3, z3;
                if (!VertexToWorld(args[0], out x1, out y1, out z1)) return;
                if (!VertexToWorld(args[1], out x2, out y2, out z2)) return;
                if (!VertexToWorld(args[2], out x3, out y3, out z3)) return;
                AddTriangle(x1, y1, z1, x2, y2, z2, x3, y3, z3);
            }
        }

        bool VertexToWorld(object vertex, out double x, out double y, out double z)
        {
            x = y = z = 0;
            try
            {
                object c = Dyn.Get(vertex, "coord");
                Array a = c as Array;
                if (a == null) { VertexReadErrors++; return false; }
                int lb = a.GetLowerBound(0);
                double vx = Convert.ToDouble(a.GetValue(lb));
                double vy = Convert.ToDouble(a.GetValue(lb + 1));
                double vz = Convert.ToDouble(a.GetValue(lb + 2));
                double[] m = Matrix;
                // v' = M * v (колоночный вектор; формула из официальных примеров API)
                double t1 = vx * m[3] + vy * m[7] + vz * m[11] + m[15];
                if (Math.Abs(t1) < 1e-12) t1 = 1.0;
                x = (vx * m[0] + vy * m[4] + vz * m[8] + m[12]) / t1;
                y = (vx * m[1] + vy * m[5] + vz * m[9] + m[13]) / t1;
                z = (vx * m[2] + vy * m[6] + vz * m[10] + m[14]) / t1;
                return true;
            }
            catch
            {
                VertexReadErrors++;
                return false;
            }
        }

        // splitmix64: проверенный финализатор (FNV-1a на 64-битных блоках даёт
        // коллизии вида Key(2,2,0)==Key(0,0,0) — выяснено самотестом)
        static ulong Mix64(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        static ulong Key(double x, double y, double z)
        {
            unchecked
            {
                ulong a = (ulong)BitConverter.DoubleToInt64Bits(x);
                ulong b = (ulong)BitConverter.DoubleToInt64Bits(y);
                ulong c = (ulong)BitConverter.DoubleToInt64Bits(z);
                ulong k = a;
                k ^= b + 0x9E3779B97F4A7C15UL + (k << 6) + (k >> 2);
                k ^= c + 0x9E3779B97F4A7C15UL + (k << 6) + (k >> 2);
                return Mix64(k);
            }
        }

        int AddVertex(double x, double y, double z)
        {
            // -0.0 и +0.0 равны, но имеют разные битовые образы: нормализуем,
            // иначе ноль порождает вершины-дубли
            if (x == 0.0) x = 0.0;
            if (y == 0.0) y = 0.0;
            if (z == 0.0) z = 0.0;

            ulong k = Key(x, y, z);
            int idx;
            // Ключ — 64-битный хеш, поэтому при попадании обязаны сверить сами
            // координаты: иначе коллизия молча сварит две далёкие вершины в одну
            // и в модели появится шип. При расхождении — линейное пробирование.
            for (int probe = 0; probe < 8; probe++)
            {
                if (!_index.TryGetValue(k, out idx))
                {
                    idx = Verts.Count / 3;
                    _index[k] = idx;
                    Verts.Add(x); Verts.Add(y); Verts.Add(z);
                    return idx;
                }
                int b = idx * 3;
                if (Verts[b] == x && Verts[b + 1] == y && Verts[b + 2] == z) return idx;
                HashCollisions++;
                unchecked { k += 0x9E3779B97F4A7C15UL; }
            }
            // 8 коллизий подряд — практически невозможно; добавляем без склейки
            idx = Verts.Count / 3;
            Verts.Add(x); Verts.Add(y); Verts.Add(z);
            return idx;
        }

        void AddTriangle(double x1, double y1, double z1, double x2, double y2, double z2, double x3, double y3, double z3)
        {
            int a = AddVertex(x1, y1, z1);
            int b = AddVertex(x2, y2, z2);
            int c = AddVertex(x3, y3, z3);
            if (a == b || b == c || a == c) { SkippedDegenerate++; return; }
            Quads.Add(a); Quads.Add(b); Quads.Add(c); Quads.Add(c);
            TriCount++;
        }
    }

    // ------------------------------------------------------------------------
    // Динамическая реализация интерфейса InwSimplePrimitivesCB (Reflection.Emit)
    // ------------------------------------------------------------------------
    public static class CallbackFactory
    {
        private static Type _proxy;
        private static readonly object Lock = new object();

        public static Type Build(Type iface)
        {
            lock (Lock)
            {
                if (_proxy != null) return _proxy;

                AssemblyName an = new AssemblyName("NWD2DWG.CallbackProxy");
                AssemblyBuilder ab = AppDomain.CurrentDomain.DefineDynamicAssembly(an, AssemblyBuilderAccess.Run);
                ab.SetCustomAttribute(new CustomAttributeBuilder(
                    typeof(ComVisibleAttribute).GetConstructor(new[] { typeof(bool) }),
                    new object[] { true }));

                ModuleBuilder mb = ab.DefineDynamicModule("proxy");
                TypeBuilder tb = mb.DefineType("PrimitiveCallbackProxy",
                    TypeAttributes.Public | TypeAttributes.BeforeFieldInit);

                tb.AddInterfaceImplementation(iface);
                FieldBuilder fld = tb.DefineField("_sink", typeof(PrimitiveSink), FieldAttributes.Public);

                ConstructorBuilder ctor = tb.DefineConstructor(MethodAttributes.Public,
                    CallingConventions.Standard, new[] { typeof(PrimitiveSink) });
                ILGenerator cil = ctor.GetILGenerator();
                cil.Emit(OpCodes.Ldarg_0);
                cil.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
                cil.Emit(OpCodes.Ldarg_0);
                cil.Emit(OpCodes.Ldarg_1);
                cil.Emit(OpCodes.Stfld, fld);
                cil.Emit(OpCodes.Ret);

                MethodInfo handle = typeof(PrimitiveSink).GetMethod("Handle");

                foreach (MethodInfo im in iface.GetMethods())
                {
                    ParameterInfo[] ps = im.GetParameters();
                    Type[] pt = new Type[ps.Length];
                    for (int i = 0; i < ps.Length; i++) pt[i] = ps[i].ParameterType;

                    MethodBuilder m = tb.DefineMethod(im.Name,
                        MethodAttributes.Public | MethodAttributes.Virtual |
                        MethodAttributes.NewSlot | MethodAttributes.Final,
                        CallingConventions.HasThis, im.ReturnType, pt);

                    ILGenerator il = m.GetILGenerator();
                    // аргументы: sink, имя метода, object[] args
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, fld);
                    il.Emit(OpCodes.Ldstr, im.Name);
                    il.Emit(OpCodes.Ldc_I4, ps.Length);
                    il.Emit(OpCodes.Newarr, typeof(object));
                    for (int i = 0; i < ps.Length; i++)
                    {
                        il.Emit(OpCodes.Dup);
                        il.Emit(OpCodes.Ldc_I4, i);
                        il.Emit(OpCodes.Ldarg, i + 1);
                        if (pt[i].IsValueType) il.Emit(OpCodes.Box, pt[i]);
                        else il.Emit(OpCodes.Castclass, typeof(object));
                        il.Emit(OpCodes.Stelem_Ref);
                    }
                    il.Emit(OpCodes.Call, handle);
                    il.Emit(OpCodes.Ret);

                    tb.DefineMethodOverride(m, im);
                }

                _proxy = tb.CreateType();
                Log.Write("динамическая реализация InwSimplePrimitivesCB создана: " + _proxy.AssemblyQualifiedName);
                return _proxy;
            }
        }

        public static object CreateProxy(NwLoader loader, PrimitiveSink sink)
        {
            Type proxy = Build(loader.CallbackIface);
            return Activator.CreateInstance(proxy, sink);
        }
    }

    // ------------------------------------------------------------------------
    // Запись DXF
    // ------------------------------------------------------------------------
    public class DxfWriter : IDisposable
    {
        readonly StreamWriter _sw;
        readonly StringBuilder _buf = new StringBuilder(1 << 20);
        readonly int _units;
        long _entities;

        // Лимит DXF PolyfaceMesh — 32767 и на вершины, и на грани
        const int MaxVerts = 30000;
        const int MaxFaces = 30000;

        public long Entities { get { return _entities; } }

        public DxfWriter(string path, int units)
        {
            _units = units;
            _sw = new StreamWriter(path, false, new ASCIIEncoding());
            _buf.AppendLine("999");
            _buf.AppendLine("Created by NWD2DWG v1.0 (Navisworks -> AutoCAD converter)");
            _buf.AppendLine("0");
            _buf.AppendLine("SECTION");
            _buf.AppendLine("2");
            _buf.AppendLine("HEADER");
            _buf.AppendLine("9");
            _buf.AppendLine("$ACADVER");
            _buf.AppendLine("1");
            _buf.AppendLine("AC1027");
            _buf.AppendLine("9");
            _buf.AppendLine("$INSUNITS");
            _buf.AppendLine("70");
            _buf.AppendLine(units.ToString(CultureInfo.InvariantCulture));
            _buf.AppendLine("0");
            _buf.AppendLine("ENDSEC");
        }

        public void BeginEntities(IEnumerable<string> layers)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string l in layers)
            {
                string s = SanitizeLayer(l);
                if (seen.Add(s)) list.Add(s);
            }
            if (list.Count == 0) list.Add("0");

            _buf.AppendLine("0");
            _buf.AppendLine("SECTION");
            _buf.AppendLine("2");
            _buf.AppendLine("TABLES");
            _buf.AppendLine("0");
            _buf.AppendLine("TABLE");
            _buf.AppendLine("2");
            _buf.AppendLine("LAYER");
            _buf.AppendLine("70");
            _buf.AppendLine(list.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string l in list)
            {
                _buf.AppendLine("0");
                _buf.AppendLine("LAYER");
                _buf.AppendLine("2");
                _buf.AppendLine(l);
                _buf.AppendLine("70");
                _buf.AppendLine("0");
                _buf.AppendLine("62");
                _buf.AppendLine("7");
                _buf.AppendLine("6");
                _buf.AppendLine("CONTINUOUS");
            }
            _buf.AppendLine("0");
            _buf.AppendLine("ENDTAB");
            _buf.AppendLine("0");
            _buf.AppendLine("ENDSEC");
            _buf.AppendLine("0");
            _buf.AppendLine("SECTION");
            _buf.AppendLine("2");
            _buf.AppendLine("ENTITIES");
        }

        static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) d = 0;
            return d.ToString("G12", CultureInfo.InvariantCulture);
        }

        void WriteCode(int code, string value) { _buf.Append(code).Append("\r\n").Append(value).Append("\r\n"); }

        void FlushIfNeeded()
        {
            if (_buf.Length >= 1 << 20)
            {
                _sw.Write(_buf.ToString());
                _buf.Length = 0;
            }
        }

        public void AddPolyface(IList<double> verts, IList<int> quads, string layer, int rgbColor)
        {
            if (verts == null || quads == null || quads.Count == 0) return;
            string l = SanitizeLayer(layer);

            // разбивка на куски по лимитам формата
            int f0 = 0;
            while (f0 < quads.Count)
            {
                var used = new Dictionary<int, int>();
                var rev = new List<int>();
                var faces = new List<int>();
                int f = f0;
                var addedNow = new List<int>(4);
                while (f < quads.Count)
                {
                    if (faces.Count / 4 >= MaxFaces) break;
                    int a = quads[f], b = quads[f + 1], c = quads[f + 2], d = quads[f + 3];
                    addedNow.Clear();
                    foreach (int vi in new[] { a, b, c, d })
                    {
                        if (!used.ContainsKey(vi)) { used[vi] = used.Count; addedNow.Add(vi); }
                    }
                    if (used.Count > MaxVerts)
                    {
                        // Откатываем ТОЛЬКО вершины, добавленные этой гранью.
                        // Удаление всех четырёх выбивало из словаря вершины,
                        // уже использованные предыдущими гранями чанка, и рвало
                        // соответствие между rev[] и 1-based индексами в faces[].
                        foreach (int vi in addedNow) used.Remove(vi);
                        break;
                    }
                    // лица: индексы 1-based
                    faces.Add(used[a] + 1); faces.Add(used[b] + 1); faces.Add(used[c] + 1); faces.Add(used[d] + 1);
                    f += 4;
                }
                if (f == f0) { f0 += 4; continue; } // не должно случиться

                foreach (var kv in used.OrderBy(k => k.Value)) rev.Add(kv.Key);

                _buf.AppendLine("0");
                _buf.AppendLine("POLYLINE");
                _buf.AppendLine("8");
                _buf.AppendLine(l);
                _buf.AppendLine("66");
                _buf.AppendLine("1");
                _buf.AppendLine("10"); _buf.AppendLine("0");
                _buf.AppendLine("20"); _buf.AppendLine("0");
                _buf.AppendLine("30"); _buf.AppendLine("0");
                _buf.AppendLine("70");
                _buf.AppendLine("64");
                _buf.AppendLine("71");
                _buf.AppendLine(rev.Count.ToString(CultureInfo.InvariantCulture));
                _buf.AppendLine("72");
                _buf.AppendLine((faces.Count / 4).ToString(CultureInfo.InvariantCulture));
                if (rgbColor >= 0)
                {
                    int aci = SolidReconstructor.RgbToAci(rgbColor);
                    _buf.AppendLine("62");
                    _buf.AppendLine(aci.ToString(CultureInfo.InvariantCulture));
                }
                foreach (int vi in rev)
                {
                    _buf.AppendLine("0");
                    _buf.AppendLine("VERTEX");
                    _buf.AppendLine("8");
                    _buf.AppendLine(l);
                    _buf.AppendLine("10"); _buf.AppendLine(Num(verts[vi * 3]));
                    _buf.AppendLine("20"); _buf.AppendLine(Num(verts[vi * 3 + 1]));
                    _buf.AppendLine("30"); _buf.AppendLine(Num(verts[vi * 3 + 2]));
                    _buf.AppendLine("70");
                    _buf.AppendLine("192");
                }
                for (int i = 0; i < faces.Count; i += 4)
                {
                    _buf.AppendLine("0");
                    _buf.AppendLine("VERTEX");
                    _buf.AppendLine("8");
                    _buf.AppendLine(l);
                    _buf.AppendLine("10"); _buf.AppendLine("0");
                    _buf.AppendLine("20"); _buf.AppendLine("0");
                    _buf.AppendLine("30"); _buf.AppendLine("0");
                    _buf.AppendLine("70");
                    _buf.AppendLine("128");
                    _buf.AppendLine("71"); _buf.AppendLine(faces[i].ToString(CultureInfo.InvariantCulture));
                    _buf.AppendLine("72"); _buf.AppendLine(faces[i + 1].ToString(CultureInfo.InvariantCulture));
                    _buf.AppendLine("73"); _buf.AppendLine(faces[i + 2].ToString(CultureInfo.InvariantCulture));
                    _buf.AppendLine("74"); _buf.AppendLine(faces[i + 3].ToString(CultureInfo.InvariantCulture));
                }
                _buf.AppendLine("0");
                _buf.AppendLine("SEQEND");

                _entities++;
                f0 = f;
                FlushIfNeeded();
            }
        }

        public void Add3dFace(double x1, double y1, double z1, double x2, double y2, double z2,
                              double x3, double y3, double z3, string layer, int rgbColor)
        {
            string l = SanitizeLayer(layer);
            _buf.AppendLine("0");
            _buf.AppendLine("3DFACE");
            _buf.AppendLine("8");
            _buf.AppendLine(l);
            if (rgbColor >= 0)
            {
                int aci = SolidReconstructor.RgbToAci(rgbColor);
                _buf.AppendLine("62");
                _buf.AppendLine(aci.ToString(CultureInfo.InvariantCulture));
            }
            _buf.AppendLine("10"); _buf.AppendLine(Num(x1));
            _buf.AppendLine("20"); _buf.AppendLine(Num(y1));
            _buf.AppendLine("30"); _buf.AppendLine(Num(z1));
            _buf.AppendLine("11"); _buf.AppendLine(Num(x2));
            _buf.AppendLine("21"); _buf.AppendLine(Num(y2));
            _buf.AppendLine("31"); _buf.AppendLine(Num(z2));
            _buf.AppendLine("12"); _buf.AppendLine(Num(x3));
            _buf.AppendLine("22"); _buf.AppendLine(Num(y3));
            _buf.AppendLine("32"); _buf.AppendLine(Num(z3));
            _buf.AppendLine("13"); _buf.AppendLine(Num(x3));
            _buf.AppendLine("23"); _buf.AppendLine(Num(y3));
            _buf.AppendLine("33"); _buf.AppendLine(Num(z3));
            _entities++;
            FlushIfNeeded();
        }

        public void Finish()
        {
            _buf.AppendLine("0");
            _buf.AppendLine("ENDSEC");
            _buf.AppendLine("0");
            _buf.AppendLine("EOF");
            _sw.Write(_buf.ToString());
            _buf.Length = 0;
            _sw.Flush();
        }

        public void Dispose()
        {
            try { _sw.Dispose(); } catch { }
        }

        // --------------------------------------------------------------------
        public static string SanitizeLayer(string name)
        {
            if (string.IsNullOrEmpty(name)) return "0";
            var sb = new StringBuilder(name.Length + 8);
            foreach (char ch in name)
            {
                if (ch == ' ') { sb.Append('_'); continue; }
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                    (ch >= '0' && ch <= '9') || ch == '_' || ch == '-')
                { sb.Append(ch); continue; }
                string tr;
                if (Translit.TryGet(ch, out tr)) { sb.Append(tr); continue; }
                sb.Append('_');
            }
            string s = sb.ToString().Trim('_', ' ', '\t');
            if (s.Length == 0) s = "0";
            if (s.Length > 240) s = s.Substring(0, 240);
            return s;
        }

        static class Translit
        {
            static readonly Dictionary<char, string> Map = new Dictionary<char, string>
            {
                {'а',"a"},{'б',"b"},{'в',"v"},{'г',"g"},{'д',"d"},{'е',"e"},{'ё',"yo"},{'ж',"zh"},
                {'з',"z"},{'и',"i"},{'й',"y"},{'к',"k"},{'л',"l"},{'м',"m"},{'н',"n"},{'о',"o"},
                {'п',"p"},{'р',"r"},{'с',"s"},{'т',"t"},{'у',"u"},{'ф',"f"},{'х',"h"},{'ц',"ts"},
                {'ч',"ch"},{'ш',"sh"},{'щ',"sch"},{'ъ',""},{'ы',"y"},{'ь',""},{'э',"e"},{'ю',"yu"},
                {'я',"ya"},
                {'А',"A"},{'Б',"B"},{'В',"V"},{'Г',"G"},{'Д',"D"},{'Е',"E"},{'Ё',"Yo"},{'Ж',"Zh"},
                {'З',"Z"},{'И',"I"},{'Й',"Y"},{'К',"K"},{'Л',"L"},{'М',"M"},{'Н',"N"},{'О',"O"},
                {'П',"P"},{'Р',"R"},{'С',"S"},{'Т',"T"},{'У',"U"},{'Ф',"F"},{'Х',"H"},{'Ц',"Ts"},
                {'Ч',"Ch"},{'Ш',"Sh"},{'Щ',"Sch"},{'Ъ',""},{'Ы',"Y"},{'Ь',""},{'Э',"E"},{'Ю',"Yu"},
                {'Я',"Ya"}
            };

            public static bool TryGet(char c, out string s) { return Map.TryGetValue(c, out s); }
        }
    }

    // ------------------------------------------------------------------------
    // Запись DWG через COM-автоматизацию AutoCAD
    // ------------------------------------------------------------------------
    public class AcadWriter
    {
        public static string FindProgId()
        {
            string[] ids = { "AutoCAD.Application.25.1", "AutoCAD.Application.25",
                             "AutoCAD.Application.24.3", "AutoCAD.Application.24.2", "AutoCAD.Application.24.1", "AutoCAD.Application.24.0", "AutoCAD.Application.24",
                             "AutoCAD.Application.23.1", "AutoCAD.Application.23", "AutoCAD.Application.22", "AutoCAD.Application.21",
                             "AutoCAD.Application.20", "AutoCAD.Application.19", "AutoCAD.Application.18",
                             "AutoCAD.Application" };
            foreach (string id in ids)
            {
                try { if (Type.GetTypeFromProgID(id) != null) return id; }
                catch { }
            }
            return null;
        }

        public static List<string> ListProgIds()
        {
            var found = new List<string>();
            string[] ids = { "AutoCAD.Application.25.1", "AutoCAD.Application.25",
                             "AutoCAD.Application.24.3", "AutoCAD.Application.24.2", "AutoCAD.Application.24.1", "AutoCAD.Application.24.0", "AutoCAD.Application.24",
                             "AutoCAD.Application.23.1", "AutoCAD.Application.23", "AutoCAD.Application.22", "AutoCAD.Application.21",
                             "AutoCAD.Application.20", "AutoCAD.Application.19", "AutoCAD.Application.18",
                             "AutoCAD.Application" };
            foreach (string id in ids)
            {
                try { if (Type.GetTypeFromProgID(id) != null) found.Add(id); }
                catch { }
            }
            return found;
        }

        public static string FindAccoreConsole()
        {
            string[] searchPaths = {
                @"C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe",
                @"C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe",
                @"C:\Program Files\Autodesk\AutoCAD 2024\accoreconsole.exe",
                @"C:\Program Files\Autodesk\AutoCAD 2023\accoreconsole.exe",
                @"C:\Program Files\Autodesk\AutoCAD 2022\accoreconsole.exe",
                @"C:\Program Files\Autodesk\AutoCAD 2021\accoreconsole.exe",
                @"C:\Program Files\Autodesk\AutoCAD 2020\accoreconsole.exe"
            };
            foreach (string p in searchPaths)
            {
                if (File.Exists(p)) return p;
            }
            try
            {
                string adDir = @"C:\Program Files\Autodesk";
                if (Directory.Exists(adDir))
                {
                    foreach (string d in Directory.GetDirectories(adDir, "AutoCAD*"))
                    {
                        string p = Path.Combine(d, "accoreconsole.exe");
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { }
            return null;
        }

        public static void ConvertDxfToDwg(string dxfPath, string dwgPath, bool visible)
        {
            string accore = FindAccoreConsole();
            if (!string.IsNullOrEmpty(accore))
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "NWD2DWG");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                string scrPath = Path.Combine(tempDir, "conv_" + Guid.NewGuid().ToString("N") + ".scr");
                string normDwg = Path.GetFullPath(dwgPath).Replace('\\', '/');

                string scrContent = string.Format(CultureInfo.InvariantCulture,
                    "_.SAVEAS\r\n2018\r\n\"{0}\"\r\n_.QUIT\r\n_Y\r\n", normDwg);
                // Encoding.Default (ANSI-кодовая страница системы) — accoreconsole
                // читает скрипт именно в ней; ASCII убил бы кириллицу в пути
                File.WriteAllText(scrPath, scrContent, Encoding.Default);

                if (File.Exists(dwgPath)) try { File.Delete(dwgPath); } catch { }

                var psi = new ProcessStartInfo
                {
                    FileName = accore,
                    Arguments = string.Format(CultureInfo.InvariantCulture, "/i \"{0}\" /s \"{1}\"", Path.GetFullPath(dxfPath), scrPath),
                    UseShellExecute = false,
                    CreateNoWindow = !visible,
                    WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
                };

                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(600000))
                    {
                        // раньше зависший accoreconsole просто оставался жить
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(5000); } catch { }
                        Log.Write("accoreconsole не завершился за 10 мин — процесс снят");
                    }
                    else if (p.ExitCode != 0)
                    {
                        Log.Write("accoreconsole завершился с кодом " + p.ExitCode);
                    }
                }

                try { File.Delete(scrPath); } catch { }
                if (File.Exists(dwgPath) && new FileInfo(dwgPath).Length > 0) return;
                Log.Write("accoreconsole не создал DWG, пробуем COM-автоматизацию AutoCAD");
            }

            // Fallback: COM-автоматизация
            string progId = FindProgId();
            if (progId == null)
                throw new Exception("AutoCAD не найден (нет accoreconsole.exe или COM-регистрации AutoCAD). Для вывода установите AutoCAD или используйте формат DXF.");

            Type t = Type.GetTypeFromProgID(progId);
            dynamic acad = Activator.CreateInstance(t);
            try
            {
                try { acad.Visible = visible; } catch { }
                string tempDir = Path.Combine(Path.GetTempPath(), "NWD2DWG");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                string scrPath = Path.Combine(tempDir, "conv_" + Guid.NewGuid().ToString("N") + ".scr");
                string normDwg = Path.GetFullPath(dwgPath).Replace('\\', '/');

                string scrContent = string.Format(CultureInfo.InvariantCulture,
                    "_.SAVEAS\r\n2018\r\n\"{0}\"\r\n", normDwg);
                // ASCII портил кириллицу в пути (например "Новая папка") — нужна ANSI
                File.WriteAllText(scrPath, scrContent, Encoding.Default);

                dynamic doc = acad.Documents.Add();
                doc.SendCommand(string.Format(CultureInfo.InvariantCulture, "_.SCRIPT \"{0}\"\r\n", scrPath.Replace('\\', '/')));

                // SendCommand асинхронна: фиксированные 3 с не хватало на больших
                // файлах — ждём появления DWG и стабилизации его размера
                var swWait = Stopwatch.StartNew();
                long lastLen = -1;
                int stable = 0;
                while (swWait.Elapsed.TotalSeconds < 600)
                {
                    Thread.Sleep(500);
                    long len = -1;
                    try { if (File.Exists(dwgPath)) len = new FileInfo(dwgPath).Length; } catch { }
                    if (len > 0 && len == lastLen) { if (++stable >= 4) break; }
                    else stable = 0;
                    lastLen = len;
                }
                if (lastLen <= 0) Log.Write("AutoCAD (COM) не создал DWG за отведённое время: " + dwgPath);

                try { File.Delete(scrPath); } catch { }
                doc.Close(false);
            }
            finally
            {
                try { acad.Quit(); } catch { }
            }
        }

        public static void Write(string outPath, IEnumerable<string> layerNames,
                                 Action<Action<IList<double>, IList<int>, string, int>> body,
                                 AppOptions opts)
        {
            string progId = FindProgId();
            if (progId == null) throw new Exception("AutoCAD не найден (нет COM-регистрации AutoCAD). Используйте формат DXF.");

            Type t = Type.GetTypeFromProgID(progId);
            dynamic acad = Activator.CreateInstance(t);
            bool quit = false;
            try
            {
                try { acad.Visible = opts.ShowAutoCad; } catch { }
                dynamic doc = acad.Documents.Add();
                try
                {
                    // слои
                    dynamic layers = doc.Layers;
                    foreach (string name in layerNames)
                    {
                        string s = DxfWriter.SanitizeLayer(name);
                        if (string.IsNullOrEmpty(s)) continue;
                        try { layers.Add(s); } catch { }
                    }

                    dynamic ms = doc.ModelSpace;
                    Action<IList<double>, IList<int>, string, int> emit =
                        delegate(IList<double> verts, IList<int> quads, string layer, int rgb)
                        {
                            // разбивка на куски (лимиты AutoCAD на полилинии)
                            const int maxV = 30000, maxF = 30000;
                            int f0 = 0;
                            var addedNow = new List<int>(4);
                            while (f0 < quads.Count)
                            {
                                var used = new Dictionary<int, int>();
                                var rev = new List<int>();
                                var faces = new List<int>();
                                int f = f0;
                                while (f < quads.Count)
                                {
                                    if (faces.Count / 4 >= maxF) break;
                                    int a = quads[f], b = quads[f + 1], c = quads[f + 2], d = quads[f + 3];
                                    addedNow.Clear();
                                    foreach (int vi in new[] { a, b, c, d })
                                    {
                                        if (!used.ContainsKey(vi)) { used[vi] = used.Count; addedNow.Add(vi); }
                                    }
                                    // откат только собственных вершин грани (см. DxfWriter.AddPolyface)
                                    if (used.Count > maxV) { foreach (int vi in addedNow) used.Remove(vi); break; }
                                    faces.Add(used[a] + 1); faces.Add(used[b] + 1); faces.Add(used[c] + 1); faces.Add(used[d] + 1);
                                    f += 4;
                                }
                                if (f == f0) { f0 += 4; continue; }
                                foreach (var kv in used.OrderBy(k => k.Value)) rev.Add(kv.Key);

                                double[] flat = new double[rev.Count * 3];
                                for (int i = 0; i < rev.Count; i++)
                                {
                                    flat[i * 3] = verts[rev[i] * 3];
                                    flat[i * 3 + 1] = verts[rev[i] * 3 + 1];
                                    flat[i * 3 + 2] = verts[rev[i] * 3 + 2];
                                }
                                int[] faceArr = faces.ToArray();

                                dynamic mesh = ms.AddPolyfaceMesh(flat, faceArr);
                                try { mesh.Layer = DxfWriter.SanitizeLayer(layer); } catch { }
                                try { mesh.Update(); } catch { }
                                f0 = f;
                            }
                        };
                    body(emit);

                    doc.SaveAs(outPath);
                    try { doc.Close(false); } catch { }
                    quit = true;
                    try { acad.Quit(); } catch { }
                }
                finally
                {
                    if (!quit) { try { doc.Close(false); } catch { } try { acad.Quit(); } catch { } }
                }
            }
            finally
            {
                try { if (!quit) acad.Quit(); } catch { }
                try { Marshal.ReleaseComObject(acad); } catch { }
            }
        }
    }

    // ------------------------------------------------------------------------
    // Основной конвертер
    // ------------------------------------------------------------------------
    public class ConvertStats
    {
        public int Models;
        public int Items;
        public int Fragments;
        public long Triangles;
        public long Vertices;
        public int HiddenSkipped;
        public int Degenerate;
        public int VertexErrors;
        public long Entities;
        public long OutputBytes;
        public TimeSpan Elapsed;
    }

    public class NavisConverter
    {
        public static string EnsurePluginDll()
        {
            // 1) Рядом с EXE
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string dllPath = Path.Combine(exeDir, "NWD2DWG.Plugin.dll");
            if (File.Exists(dllPath)) return Path.GetFullPath(dllPath);

            // 2) В подпапке dist
            string distPath = Path.Combine(exeDir, "dist", "NWD2DWG.Plugin.dll");
            if (File.Exists(distPath)) return Path.GetFullPath(distPath);

            // 3) Извлечение из встроенных ресурсов в %TEMP%\NWD2DWG\NWD2DWG.Plugin.dll
            string tempDir = Path.Combine(Path.GetTempPath(), "NWD2DWG");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            string tempDll = Path.Combine(tempDir, "NWD2DWG.Plugin.dll");

            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("NWD2DWG.Plugin.dll"))
            {
                if (s != null)
                {
                    using (var fs = new FileStream(tempDll, FileMode.Create, FileAccess.Write))
                    {
                        s.CopyTo(fs);
                    }
                    return tempDll;
                }
            }

            if (File.Exists(tempDll)) return tempDll;
            throw new Exception("Не найден файл плагина NWD2DWG.Plugin.dll. Поместите NWD2DWG.Plugin.dll рядом с программой.");
        }

        // Список DXF, реально созданных плагином (один файл или набор разделов)
        /// <summary>
        /// Показывает, что происходит во время конвертации: строки журнала
        /// плагина по мере появления плюс собственная сводка — сколько прошло
        /// времени, насколько вырос файл, с какой скоростью, сколько осталось
        /// места на диске.
        ///
        /// Здесь же стоит предохранитель. Один прогон вырастил файл до 15.7 ГБ
        /// и был замечен случайно: ни размера, ни свободного места никто не
        /// показывал. Теперь при опасном росте работа останавливается сама.
        /// </summary>
        class ConvWatcher
        {
            const long GB = 1024L * 1024 * 1024;

            // Пороги предупреждений и аварийной остановки.
            static readonly long[] WarnAt = { 2 * GB, 5 * GB, 10 * GB, 20 * GB };
            const long MinFreeBytes = 3 * GB;       // не за счёт последнего места

            // Порог остановки. По умолчанию 20 ГБ — дальше это заведомо не
            // выдача, а разросшаяся тесселяция. Переопределяется переменной
            // окружения: на проверках и на слабых машинах нужен свой предел.
            static readonly long AbortAtBytes = ReadLimitGb() * GB;

            static long ReadLimitGb()
            {
                try
                {
                    string v = Environment.GetEnvironmentVariable("NWD2DWG_MAX_OUTPUT_GB");
                    long gb;
                    if (!string.IsNullOrEmpty(v) && long.TryParse(v, out gb) && gb > 0) return gb;
                }
                catch { }
                return 20;
            }

            readonly string _log, _target, _runDir;
            readonly Action<string> _status;
            Thread _th;
            volatile bool _stop;
            long _pos;
            int _warned;
            bool _aborted;
            readonly DateTime _t0 = DateTime.Now;
            long _lastSize;
            DateTime _lastTick = DateTime.Now;

            public ConvWatcher(string logPath, string target, string runDir, Action<string> status)
            {
                _log = logPath; _target = target; _runDir = runDir; _status = status;
            }

            // Наблюдатель работает в фоновом потоке, пока главный поток STA
            // сидит внутри вызова COM в Navisworks. Обращаться отсюда к общему
            // журналу и к делегату состояния оказалось нельзя: процесс
            // Navisworks падал через десяток секунд. Поэтому пишем только в
            // консоль, и то под замком.
            static readonly object ConsoleLock = new object();

            void Say(string s)
            {
                lock (ConsoleLock)
                {
                    try { Console.Out.WriteLine(s); Console.Out.Flush(); } catch { }
                }
            }

            public void Start()
            {
                _th = new Thread(Loop) { IsBackground = true, Name = "conv-watch" };
                _th.Start();
            }

            public void Stop()
            {
                _stop = true;
                if (_th != null) { try { _th.Join(2500); } catch { } }
                Pump();   // добираем хвост журнала
            }

            void Loop()
            {
                var lastReport = DateTime.Now;
                while (!_stop)
                {
                    try
                    {
                        Pump();
                        if ((DateTime.Now - lastReport).TotalSeconds >= 15)
                        {
                            lastReport = DateTime.Now;
                            Report();
                        }
                    }
                    catch { }
                    Thread.Sleep(1000);
                }
            }

            /// <summary>Новые строки журнала плагина — наружу без задержки.</summary>
            void Pump()
            {
                try
                {
                    if (!File.Exists(_log)) return;
                    using (var fs = new FileStream(_log, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (fs.Length < _pos) _pos = 0;      // журнал пересоздан
                        if (fs.Length == _pos) return;
                        fs.Seek(_pos, SeekOrigin.Begin);
                        using (var sr = new StreamReader(fs, Encoding.UTF8))
                        {
                            string line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                line = line.TrimEnd();
                                if (line.Length > 0) Say("  · " + line);
                            }
                            _pos = fs.Position;
                        }
                    }
                }
                catch { }
            }

            /// <summary>Сводка: время, объём, скорость, свободное место.</summary>
            void Report()
            {
                long size = 0;
                try { if (File.Exists(_target)) size = new FileInfo(_target).Length; }
                catch { }

                var now = DateTime.Now;
                double secs = (now - _lastTick).TotalSeconds;
                double mbPerSec = secs > 0.5 ? (size - _lastSize) / 1048576.0 / secs : 0;
                _lastTick = now; _lastSize = size;

                long free = FreeSpace();
                var sb = new StringBuilder();
                sb.AppendFormat(CultureInfo.InvariantCulture, @"[ход работы] {0:hh\:mm\:ss}",
                                now - _t0);
                if (size > 0)
                    sb.AppendFormat(CultureInfo.InvariantCulture, " | файл {0:F2} ГБ (+{1:F1} МБ/с)",
                                    size / (double)GB, mbPerSec);
                if (free > 0)
                    sb.AppendFormat(CultureInfo.InvariantCulture, " | свободно {0:F1} ГБ",
                                    free / (double)GB);
                Say(sb.ToString());

                for (int i = _warned; i < WarnAt.Length; i++)
                {
                    if (size < WarnAt[i]) break;
                    _warned = i + 1;
                    Say(string.Format(CultureInfo.InvariantCulture,
                        "ВНИМАНИЕ: выходной файл перевалил за {0} ГБ. Если геометрия не нужна, " +
                        "снимите «Писать основную геометрию» либо примените упрощение сетки.",
                        WarnAt[i] / GB));
                }

                if (_aborted) return;   // сообщение об остановке — один раз
                if (size >= AbortAtBytes || (free > 0 && free < MinFreeBytes))
                {
                    _aborted = true;
                    Say(size >= AbortAtBytes
                        ? "ОСТАНОВКА: выходной файл превысил разумный предел — работа прекращается."
                        : "ОСТАНОВКА: на диске почти не осталось места — работа прекращается.");
                    try { File.WriteAllText(Path.Combine(_runDir, "stop.flag"), "1"); }
                    catch { }
                }
            }

            long FreeSpace()
            {
                try
                {
                    string root = Path.GetPathRoot(Path.GetFullPath(_target));
                    if (string.IsNullOrEmpty(root)) return 0;
                    return new DriveInfo(root).AvailableFreeSpace;
                }
                catch { return 0; }
            }
        }

        /// <summary>
        /// Ждёт, пока предыдущий Navisworks закроется.
        ///
        /// Замеры показали: подъём срывается тогда, когда прошлый экземпляр ещё
        /// не отпустил автоматизацию. Процесса в списке может уже не быть, но
        /// выгрузка занимает секунды, и запуск, поданный сразу следом, получает
        /// обрыв связи. Из шести прогонов подряд так падала половина.
        ///
        /// Ждём ограниченно: если пользователь держит Navisworks открытым
        /// сам, ждать его вечно нельзя — пробуем работать как есть.
        /// </summary>
        static void WaitForNavisworksIdle(Action<string> status)
        {
            const int MaxWaitSec = 45;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool told = false;

            while (sw.Elapsed.TotalSeconds < MaxWaitSec)
            {
                int alive;
                try { alive = Process.GetProcessesByName("Roamer").Length; }
                catch { return; }

                if (alive == 0)
                {
                    // Выдержка после исчезновения процесса. Полутора секунд
                    // оказалось мало: обрыв возвращался и после ожидания.
                    // Процесс из списка уходит раньше, чем освобождается COM.
                    if (sw.Elapsed.TotalSeconds > 0.5) Thread.Sleep(6000);
                    return;
                }

                if (!told)
                {
                    told = true;
                    string m = "Ожидание освобождения Navisworks (запущено экземпляров: " + alive + ")";
                    Log.Write(m);
                    if (status != null) { try { status(m); } catch { } }
                }
                Thread.Sleep(1000);
            }

            Log.Write("Navisworks всё ещё запущен — продолжаем, не дожидаясь.");
        }

        /// <summary>Обрыв связи с Navisworks (RPC_S_CALL_FAILED), а не ошибка расчёта.</summary>
        static bool IsRpcBreak(Exception ex)
        {
            // Обрыв приходит не одним кодом, а целым семейством: сервер
            // недоступен, вызов не прошёл, соединение разорвано, не удалось
            // запустить сервер. Ловить только один из них бессмысленно — на
            // измерениях встретились и 0x800706BE, и 0x800706BA.
            int[] rpcCodes =
            {
                unchecked((int)0x800706BA),   // RPC-сервер недоступен
                unchecked((int)0x800706BE),   // вызов не прошёл
                unchecked((int)0x800706BF),   // вызов не прошёл и не выполнен
                unchecked((int)0x800706B5),   // неизвестный интерфейс
                unchecked((int)0x80010108),   // объект отсоединён от клиента
                unchecked((int)0x80080005),   // не удалось запустить сервер
            };

            for (Exception e = ex; e != null; e = e.InnerException)
            {
                int hr = 0;
                try { hr = System.Runtime.InteropServices.Marshal.GetHRForException(e); }
                catch { }
                for (int i = 0; i < rpcCodes.Length; i++)
                    if (hr == rpcCodes[i]) return true;

                if (e.Message == null) continue;
                for (int i = 0; i < rpcCodes.Length; i++)
                    if (e.Message.IndexOf(rpcCodes[i].ToString(CultureInfo.InvariantCulture),
                                          StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        static IEnumerable<string> CollectProducedDxf(AppOptions opts, string targetDxf)
        {
            if (!opts.SplitDisciplines)
            {
                if (File.Exists(targetDxf)) yield return targetDxf;
                yield break;
            }
            string dir = Path.GetDirectoryName(targetDxf) ?? ".";
            string bas = Path.GetFileNameWithoutExtension(targetDxf);
            foreach (string f in Directory.GetFiles(dir, bas + "_*.dxf")) yield return f;
        }

        public static ConvertStats ConvertFile(AppOptions opts, string input, string outPath,
                                               Action<string> status, Action<double> progress,
                                               Func<bool> cancelled)
        {
            var stats = new ConvertStats();
            Stopwatch sw = Stopwatch.StartNew();

            string nwDir = FindNavisworks(opts);
            if (nwDir == null)
                throw new Exception("Navisworks Manage/Simulate не найден. Установите/активируйте Navisworks или укажите папку вручную (--navis).");

            var loader = new NwLoader();
            if (!loader.Load(nwDir))
                throw new Exception("Не удалось загрузить API Navisworks из папки " + nwDir + ": " + loader.LastError);

            string pluginDll = EnsurePluginDll();
            Log.Write("плагин Navisworks: " + pluginDll);

            dynamic nw = null;
            Process manualRoamer = null;

            // Своя папка на каждый прогон: раньше промежуточные файлы лежали в
            // общей %TEMP%\NWD2DWG, а finally звал CleanTempFiles(0), который сносил
            // ВСЁ — включая файлы параллельно идущей конвертации.
            string runDir = Path.Combine(Path.GetTempPath(), "NWD2DWG",
                                         "run_" + Guid.NewGuid().ToString("N").Substring(0, 12));
            try { Directory.CreateDirectory(runDir); } catch { }

            string targetDxf = outPath;
            bool isDwg = opts.Format == OutFormat.Dwg;
            if (isDwg)
                targetDxf = Path.Combine(runDir, Path.GetFileNameWithoutExtension(outPath) + ".dxf");

            string convLog = Path.Combine(runDir, "conv.log");
            bool keepRunDir = false;

            try
            {
                // ---- открыть Navisworks ----
                //
                // Обрыв связи случается и здесь, на подъёме автоматизации, а не
                // только при вызове плагина. Причём приходит он тогда обычным
                // InvalidOperationException, без обёртки TargetInvocationException:
                // обращения к объекту автоматизации идут напрямую, а не через
                // рефлексию. Из-за этого повтор, поставленный только вокруг
                // вызова плагина, такие обрывы пропускал — три прогона подряд
                // упали, не сделав ни одной попытки.
                //
                // Поэтому подъём Navisworks и загрузка плагина повторяются
                // здесь, целиком: неудачный экземпляр закрывается, и следующая
                // попытка начинается с чистого места.
                const int MaxStartAttempts = 4;
                MethodInfo mAdd = null;

                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        WaitForNavisworksIdle(status);
                        Log.Write("запуск Navisworks (папка: " + nwDir + ")");
                        nw = CreateNavisworksInstance(loader, nwDir, opts.ShowNavisworks, out manualRoamer);

                        // Загружаем плагин в процесс Navisworks
                        Log.Write("загрузка плагина в Navisworks...");
                        mAdd = loader.AutomationType.GetMethod("AddPluginAssembly");
                        if (mAdd == null) throw new Exception("AddPluginAssembly не найден в NavisworksApplication");
                        mAdd.Invoke(nw, new object[] { pluginDll });
                        break;
                    }
                    catch (TargetInvocationException tie) when (!IsRpcBreak(tie))
                    {
                        // Без разворачивания наружу уходило бесполезное
                        // «Адресат вызова создал исключение» — по нему нельзя
                        // понять ровно ничего, а разбирать потом приходится
                        // по журналам.
                        Exception inner = tie.InnerException ?? tie;
                        Log.Write("ОШИБКА при подъёме Navisworks: " + inner);
                        keepRunDir = true;
                        throw new Exception("Navisworks: " + inner.Message +
                                            " (лог прогона: " + runDir + ")", inner);
                    }
                    catch (Exception ex) when (IsRpcBreak(ex) && attempt < MaxStartAttempts)
                    {
                        int wait = attempt * 5;
                        string msg = string.Format(CultureInfo.InvariantCulture,
                            "Navisworks не поднялся — попытка {0} из {1}, повтор через {2} с",
                            attempt, MaxStartAttempts, wait);
                        Log.Write(msg);
                        if (status != null) { try { status(msg); } catch { } }

                        // Обрывок предыдущей попытки надо убрать, иначе он
                        // держит автоматизацию и мешает следующей.
                        try { if (nw != null) nw.Dispose(); } catch { }
                        nw = null;
                        try { if (manualRoamer != null && !manualRoamer.HasExited) manualRoamer.Kill(); }
                        catch { }
                        manualRoamer = null;

                        Thread.Sleep(wait * 1000);
                    }
                }

                if (cancelled != null && cancelled()) throw new OperationCanceledException("отменено пользователем");

                // Запуск конвертации через AddInPlugin (плагин открывает файл и извлекает геометрию in-process)
                Log.Write("запуск извлечения геометрии через плагин...");
                MethodInfo mExec = loader.AutomationType.GetMethod("ExecuteAddInPlugin");
                if (mExec == null) throw new Exception("ExecuteAddInPlugin не найден в NavisworksApplication");

                string fmtStr = "dxf";
                switch (opts.Format)
                {
                    case OutFormat.Dxf3dFace: fmtStr = "3dface"; break;
                    case OutFormat.Gltf: fmtStr = "gltf"; break;
                    case OutFormat.Glb: fmtStr = "glb"; break;
                    case OutFormat.Ifc: fmtStr = "ifc"; break;
                }

                // Section Box → строка "minX;minY;minZ;maxX;maxY;maxZ"
                string sectionBoxStr = "";
                if (opts.SectionBox != null && opts.SectionBox.Length == 6)
                {
                    sectionBoxStr = string.Join(";", Array.ConvertAll(opts.SectionBox,
                        d => d.ToString("G12", CultureInfo.InvariantCulture)));
                }

                var adv = opts.AdvConfig ?? new Plugin.AdvancedConfig();
                string advPath = Path.Combine(runDir, "modules.json");
                try { adv.SaveTo(advPath); }
                catch (Exception cex) { Log.Write("не удалось сохранить параметры модулей: " + cex.Message); advPath = ""; }

                var outp = opts.OutProfile ?? new Plugin.OutputProfile();
                string outPath2 = Path.Combine(runDir, "output.json");
                try { outp.SaveTo(outPath2); }
                catch (Exception oex) { Log.Write("не удалось сохранить профиль выдачи: " + oex.Message); outPath2 = ""; }

                string[] pluginArgs = new string[]
                {
                    input,
                    targetDxf,
                    fmtStr,
                    opts.SkipHidden ? "1" : "0",
                    opts.WithColors ? "1" : "0",
                    opts.LayersPerItem ? "1" : "0",
                    convLog,
                    opts.SplitDisciplines ? "1" : "0",
                    // v2.0 параметры
                    opts.DecimatePercent.ToString(CultureInfo.InvariantCulture),  // [8]
                    opts.SolidDetect ? "1" : "0",                                 // [9]
                    opts.TransferXData ? "1" : "0",                               // [10]
                    opts.SelectionSets ?? "",                                      // [11]
                    sectionBoxStr,                                                 // [12]
                    opts.TransferMaterials ? "1" : "0",                            // [13]
                    opts.ParallelThreads.ToString(CultureInfo.InvariantCulture),   // [14]
                    // v3.0 параметры
                    opts.GeoShift ? "1" : "0",                                     // [15]
                    opts.ExportGrids ? "1" : "0",                                  // [16]
                    opts.TracePipes ? "1" : "0",                                   // [17]
                    opts.ExportBoq ? "1" : "0",                                    // [18]
                    opts.ExportBcf ? "1" : "0",                                    // [19]
                    opts.Anonymize ? "1" : "0",                                    // [20]
                    // v3.1–v3.4: раньше эти флаги вообще не доезжали до плагина
                    opts.ClusterClashes ? "1" : "0",                               // [21]
                    opts.SectionPlan ? "1" : "0",                                  // [22]
                    opts.PurgeDxf ? "1" : "0",                                     // [23]
                    opts.BuildPenetrations ? "1" : "0",                            // [24]
                    opts.ValidateClearance ? "1" : "0",                            // [25]
                    opts.MatchSteel ? "1" : "0",                                   // [26]
                    opts.CalcCog ? "1" : "0",                                      // [27]
                    opts.GenerateIso ? "1" : "0",                                  // [28]
                    opts.MapSchedule4D ? "1" : "0",                                // [29]
                    opts.Shrinkwrap ? "1" : "0",                                   // [30]
                    opts.RoomFinish ? "1" : "0",                                   // [31]
                    // Все допуски уходят одним JSON-файлом: позиционный протокол
                    // на 40+ аргументов был бы нечитаем и ломался при любой правке
                    advPath,                                                       // [32]
                    opts.ScheduleFile ?? "",                                       // [33]
                    outPath2                                                       // [34]
                };

                if (status != null) status("Извлечение геометрии и полигонов...");
                if (progress != null) progress(0.2);

                // Плагин работает в этом же потоке и молчит до самого конца:
                // его журнал ложился в файл и печатался задним числом. На
                // тяжёлой модели это выглядело как зависание — двадцать четыре
                // минуты одной строки, и никакой возможности понять, идёт
                // работа или всё встало. Наблюдатель читает журнал плагина по
                // мере появления строк и сам добавляет сводку о ходе.
                var watch = new ConvWatcher(convLog, targetDxf, runDir, status);
                // Выключатель для разбора неполадок: позволяет отделить
                // поведение наблюдателя от поведения самой конвертации.
                if (Environment.GetEnvironmentVariable("NWD2DWG_NO_WATCH") != "1")
                    watch.Start();

                // Вызов плагина в том же потоке (STA).
                //
                // Navisworks иногда обрывает связь на самом старте: процесс
                // поднимается и умирает секунд через двадцать, наружу приходит
                // RPC_S_CALL_FAILED. Замер шести одинаковых прогонов подряд дал
                // два успеха и четыре обрыва, причём обрывы всегда быстрые
                // (22-24 с), а удачные прогоны идут минутами. От параметров
                // запуска частота не зависит — проверено включением и
                // выключением стороннего наблюдателя за ходом работы.
                //
                // Значит это помеха, а не приговор: ждём и пробуем снова.
                // Повторяем только этот обрыв — настоящую ошибку расчёта
                // повторять бессмысленно, она воспроизведётся.
                const int MaxAttempts = 4;

                object ret = null;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        ret = mExec.Invoke(nw, new object[] { "NWD2DWG_Converter.NWD2DWG", pluginArgs });
                        break;
                    }
                    catch (TargetInvocationException tie) when (IsRpcBreak(tie) && attempt < MaxAttempts)
                    {
                        int wait = attempt * 5;
                        string msg = string.Format(CultureInfo.InvariantCulture,
                            "Navisworks оборвал связь на старте — попытка {0} из {1}, повтор через {2} с",
                            attempt, MaxAttempts, wait);
                        Log.Write(msg);
                        if (status != null) { try { status(msg); } catch { } }
                        Thread.Sleep(wait * 1000);
                    }
                    catch (TargetInvocationException tie)
                    {
                        // без разворачивания наружу уходило бесполезное
                        // "Целевой вызов создал исключение"
                        watch.Stop();
                        Exception inner = tie.InnerException ?? tie;
                        Log.Write("ИСКЛЮЧЕНИЕ В ПЛАГИНЕ: " + inner);
                        if (File.Exists(convLog))
                        { try { Log.Write("[Plugin log] " + File.ReadAllText(convLog)); } catch { } }
                        keepRunDir = true;

                        string what = IsRpcBreak(tie)
                            ? "Navisworks обрывает связь на старте и не отвечает после " +
                              MaxAttempts + " попыток. Закройте открытые окна Navisworks " +
                              "и повторите; если не помогает — запустите Navisworks вручную один раз."
                            : "Плагин Navisworks: " + inner.Message;

                        throw new Exception(what + " (подробности и лог плагина: " + runDir + ")", inner);
                    }
                }
                watch.Stop();
                int exitCode = ret != null ? Convert.ToInt32(ret) : 0;

                if (exitCode != 0)
                {
                    string errDetail = "";
                    if (File.Exists(convLog)) { try { errDetail = File.ReadAllText(convLog); } catch { } }
                    keepRunDir = true;
                    throw new Exception("Плагин конвертации завершился с кодом " + exitCode + ": " + errDetail);
                }

                // Когда профиль выдачи запрещает писать геометрию, её отсутствие —
                // это выполненное указание, а не сбой. Раньше шаблон сметчика
                // считал ведомости полностью, а прогон всё равно падал в конце.
                if (!opts.SplitDisciplines && !File.Exists(targetDxf))
                {
                    if (opts.OutProfile != null && !opts.OutProfile.EmitGeometry)
                        Log.Write("Геометрия не писалась по шаблону выдачи — выданы только ведомости и отчёты.");
                    else
                        throw new Exception("Файл геометрии не был создан плагином.");
                }

                // === Глубокая чистка DXF ===
                // делаем здесь, а не в плагине: файл к этому моменту закрыт,
                // а для DWG чистка должна пройти до конвертации через AutoCAD
                if (opts.PurgeDxf)
                {
                    if (status != null) status("Чистка DXF от неиспользуемых слоёв и блоков...");
                    foreach (string dxfToPurge in CollectProducedDxf(opts, targetDxf))
                    {
                        var scope = new Plugin.CadPurger.PurgeScope
                        {
                            Layers = adv.PurgeLayers,
                            Linetypes = adv.PurgeLinetypes,
                            TextStyles = adv.PurgeTextStyles,
                            Blocks = adv.PurgeBlocks
                        };
                        try { Log.Write(Plugin.CadPurger.Purge(dxfToPurge, null, scope)); }
                        catch (Exception pex) { Log.Write("CadPurger: ОШИБКА " + pex.Message); }
                    }
                }

                // Если формат DWG — конвертируем через AutoCAD.
                // При запрете на запись геометрии конвертировать нечего:
                // сочетание бессмысленное, но падать на нём не следует.
                if (isDwg && opts.OutProfile != null && !opts.OutProfile.EmitGeometry)
                    Log.Write("Формат DWG выбран, но запись геометрии запрещена шаблоном — конвертация в DWG пропущена.");
                else if (isDwg)
                {
                    string finalDestDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
                    string finalBaseName = Path.GetFileNameWithoutExtension(outPath);

                    if (opts.SplitDisciplines)
                    {
                        string tempOutDir = Path.GetDirectoryName(targetDxf) ?? ".";
                        string tempBaseName = Path.GetFileNameWithoutExtension(targetDxf);
                        foreach (string splitDxf in Directory.GetFiles(tempOutDir, tempBaseName + "_*.dxf"))
                        {
                            string sectionSuffix = Path.GetFileName(splitDxf).Substring(tempBaseName.Length);
                            string finalSplitDwg = Path.Combine(finalDestDir, finalBaseName + Path.ChangeExtension(sectionSuffix, ".dwg"));
                            if (status != null) status("Конвертация " + Path.GetFileName(splitDxf) + " -> DWG...");
                            Log.Write("конвертация раздела DXF -> DWG: " + finalSplitDwg);
                            AcadWriter.ConvertDxfToDwg(splitDxf, finalSplitDwg, opts.ShowAutoCad);
                            try { File.Delete(splitDxf); } catch { }
                        }
                    }
                    else
                    {
                        if (status != null) status("Конвертация DXF -> DWG через AutoCAD...");
                        Log.Write("конвертация DXF -> DWG: " + outPath);
                        AcadWriter.ConvertDxfToDwg(targetDxf, outPath, opts.ShowAutoCad);
                        try { File.Delete(targetDxf); } catch { }
                    }
                }

                stats.Elapsed = sw.Elapsed;
                try { stats.OutputBytes = new FileInfo(outPath).Length; } catch { }

                // Читаем итоговую статистику из лога плагина
                if (File.Exists(convLog))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(convLog, Encoding.UTF8);
                        foreach (string l in lines)
                        {
                            Log.Write("[Plugin] " + l);
                            if (l.Contains("ГОТОВО:"))
                            {
                                Match m = Regex.Match(l, @"элементов:\s*(\d+),\s*фрагментов:\s*(\d+),\s*треугольников:\s*(\d+),\s*вершин:\s*(\d+)");
                                if (m.Success)
                                {
                                    stats.Items = int.Parse(m.Groups[1].Value);
                                    stats.Fragments = int.Parse(m.Groups[2].Value);
                                    stats.Triangles = long.Parse(m.Groups[3].Value);
                                    stats.Vertices = long.Parse(m.Groups[4].Value);
                                }
                            }
                        }
                    }
                    catch { }
                }

                Log.Write(string.Format(CultureInfo.InvariantCulture,
                    "ГОТОВО: {0} | элементов {1}, фрагментов {2}, треугольников {3}, вершин {4} | {5:F1} МБ | {6}",
                    Path.GetFileName(outPath), stats.Items, stats.Fragments, stats.Triangles,
                    stats.Vertices, stats.OutputBytes / 1048576.0, sw.Elapsed));

                if (progress != null) progress(1.0);
                if (status != null) status("Готово: " + Path.GetFileName(outPath));

                return stats;
            }
            finally
            {
                try { if (nw != null) { object r; TryCallMethod(nw, "CloseFile", out r); } } catch { }
                // Чужое окно, к которому мы подключились, закрывать нельзя:
                // человек мог оставить его открытым для своей работы.
                if (NavisConverter.OwnsInstance)
                {
                    try { if (nw != null) nw.Dispose(); } catch { }
                    try { if (manualRoamer != null && !manualRoamer.HasExited) manualRoamer.Kill(); } catch { }
                }
                else Log.Write("Navisworks оставлен открытым: подключались к чужому окну");
                // при падении оставляем папку прогона: там лежит лог плагина
                if (keepRunDir) Log.Write("Временные файлы прогона сохранены для разбора: " + runDir);
                else { try { TempCleaner.CleanRun(runDir); } catch { } }
                Log.Flush();
            }
        }

        // --------------------------------------------------------------------
        // Автоматическая очистка временных файлов (%TEMP%\NWD2DWG)
        // --------------------------------------------------------------------
        public static class TempCleaner
        {
            // Удаляет папку одного прогона конвертации целиком.
            public static long CleanRun(string runDir)
            {
                if (string.IsNullOrEmpty(runDir) || !Directory.Exists(runDir)) return 0;
                long freed = 0;
                try
                {
                    foreach (var f in new DirectoryInfo(runDir).GetFiles("*", SearchOption.AllDirectories))
                    { try { freed += f.Length; } catch { } }
                    Directory.Delete(runDir, true);
                }
                catch { }
                if (freed > 0)
                    Log.Write(string.Format(CultureInfo.InvariantCulture,
                        "TempCleaner: удалена папка прогона, освобождено {0:F1} МБ", freed / 1048576.0));
                return freed;
            }

            public static long CleanTempFiles(int maxAgeHours = 1)
            {
                long freedBytes = 0;
                string tempDir = Path.Combine(Path.GetTempPath(), "NWD2DWG");
                if (!Directory.Exists(tempDir)) return 0;

                try
                {
                    var dir = new DirectoryInfo(tempDir);

                    // брошенные папки прогонов (аварийное завершение)
                    DateTime dirThreshold = DateTime.Now.AddHours(-Math.Max(0, maxAgeHours));
                    foreach (var sub in dir.GetDirectories("run_*"))
                    {
                        try
                        {
                            if (maxAgeHours > 0 && sub.LastWriteTime >= dirThreshold) continue;
                            foreach (var f in sub.GetFiles("*", SearchOption.AllDirectories))
                            { try { freedBytes += f.Length; } catch { } }
                            sub.Delete(true);
                        }
                        catch { }
                    }
                    DateTime threshold = DateTime.Now.AddHours(-maxAgeHours);

                    foreach (var file in dir.GetFiles())
                    {
                        // Не удаляем текущий используемый DLL плагин
                        if (file.Name.Equals("NWD2DWG.Plugin.dll", StringComparison.OrdinalIgnoreCase))
                            continue;

                        bool shouldDelete = false;
                        if (maxAgeHours <= 0)
                        {
                            // При полной очистке удаляем любые временные артефакты
                            shouldDelete = true;
                        }
                        else
                        {
                            string ext = file.Extension.ToLowerInvariant();
                            // Временные тяжелые DXF/DWG/SCR удаляем если старше 1 часа
                            if (ext == ".dxf" || ext == ".dwg" || ext == ".scr" || ext == ".tmp")
                            {
                                if (file.LastWriteTime < threshold) shouldDelete = true;
                            }
                            else if (file.LastWriteTime < DateTime.Now.AddDays(-3))
                            {
                                // Старые логи старше 3 дней
                                shouldDelete = true;
                            }
                        }

                        if (shouldDelete)
                        {
                            try
                            {
                                long len = file.Length;
                                file.Delete();
                                freedBytes += len;
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                if (freedBytes > 0)
                {
                    Log.Write(string.Format(CultureInfo.InvariantCulture, "TempCleaner: очищено {0:F1} МБ временных файлов", freedBytes / 1048576.0));
                }
                return freedBytes;
            }
        }

        // --------------------------------------------------------------------
        // Создание экземпляра NavisworksApplication с поддержкой 2025+/2026
        // В 2026 конструктор больше не запускает Roamer.exe автоматически.
        // Стратегия: пробуем создать через Activator — если Roamer не стартовал,
        // запускаем Roamer.exe вручную и подключаемся через TryGetRunningInstance.
        // --------------------------------------------------------------------
        /// <summary>Подняли ли мы Navisworks сами. Чужое окно закрывать нельзя.</summary>
        public static bool OwnsInstance;

        public static dynamic CreateNavisworksInstance(NwLoader loader, string nwDir, bool visible, out Process manualRoamer)
        {
            manualRoamer = null;
            OwnsInstance = true;

            // Кто из процессов Navisworks был запущен ДО нас.
            //
            // Раньше здесь стояла глобальная проверка «есть ли в системе хоть
            // какой-нибудь Roamer.exe». Она отвечала на неверный вопрос:
            // недобитый процесс от предыдущего прогона считался нашим, и
            // программа возвращала объект автоматизации, ни к чему не
            // подключённый. Первый же вызов рвался с ошибкой связи — отсюда и
            // «один прогон проходит, следующий падает», и бесполезность
            // повторов: они снова видели тот же чужой процесс.
            //
            // Правильный вопрос — появился ли НОВЫЙ процесс, наш.
            var before = new HashSet<int>();
            try
            {
                foreach (var p in Process.GetProcessesByName("Roamer"))
                { before.Add(p.Id); p.Dispose(); }
            }
            catch { }
            if (before.Count > 0)
                Log.Write("до запуска уже работало экземпляров Navisworks: " + before.Count);

            // 1) Классический путь: Activator.CreateInstance
            Log.Write("попытка 1: Activator.CreateInstance (классический запуск)");
            dynamic nw = Activator.CreateInstance(loader.AutomationType);
            try { nw.Visible = visible; } catch (Exception ex) { Log.Write("предупреждение: Visible=" + ex.Message); }

            // Даём Navisworks время на старт (в старых версиях конструктор сам запускает Roamer.exe)
            Thread.Sleep(3000);

            // Появился ли процесс, которого до нас не было
            bool ourRoamerStarted = false;
            try
            {
                foreach (var p in Process.GetProcessesByName("Roamer"))
                {
                    if (!before.Contains(p.Id)) ourRoamerStarted = true;
                    p.Dispose();
                    if (ourRoamerStarted) break;
                }
            }
            catch { }

            // Мало увидеть процесс — объект должен отвечать. Обращение к нему
            // стоит копейки, а отличает рабочее подключение от пустышки.
            if (ourRoamerStarted && Responds(nw))
            {
                Log.Write("Roamer.exe запущен автоматически (режим 2017-2024)");
                return nw;
            }

            if (ourRoamerStarted)
                Log.Write("процесс появился, но объект автоматизации не отвечает — переходим к ручному запуску");
            else
                Log.Write("новый Roamer.exe не появился — режим 2025+/2026: ручной запуск");

            try { nw.Dispose(); } catch { }

            string roamerPath = Path.Combine(nwDir, "Roamer.exe");
            if (!File.Exists(roamerPath))
                throw new Exception("Roamer.exe не найден: " + roamerPath);

            Log.Write("запуск Roamer.exe: " + roamerPath);
            var psi = new ProcessStartInfo
            {
                FileName = roamerPath,
                WorkingDirectory = nwDir,
                UseShellExecute = true
            };
            manualRoamer = Process.Start(psi);
            Log.Write("Roamer.exe запущен: PID=" + manualRoamer.Id);

            // Ждём, пока Navisworks закончит подниматься и начнёт ждать ввода.
            //
            // Замер показал: подключение через TryGetRunningInstance отвечает
            // уже через 5 секунд после старта, но приложение к этому моменту
            // ещё грузит ленту и свои надстройки. Плагин, вызванный на десятой
            // секунде, ронял процесс. Отсюда и разброс: при тёплом кэше
            // Navisworks успевал подняться, при холодном — нет.
            //
            // WaitForInputIdle отвечает именно на нужный вопрос: закончил ли
            // процесс инициализацию.
            try
            {
                if (manualRoamer.WaitForInputIdle(90000))
                    Log.Write("Navisworks завершил инициализацию");
                else
                    Log.Write("Navisworks не сообщил о готовности за 90 с — продолжаем осторожно");
            }
            catch (Exception ex) { Log.Write("ожидание готовности: " + ex.Message); }

            // Ждём инициализации Roamer.exe (до 30 секунд)
            Log.Write("ожидание инициализации Roamer.exe...");
            dynamic result = null;
            MethodInfo tryGetMethod = loader.AutomationType.GetMethod("TryGetRunningInstance",
                BindingFlags.Public | BindingFlags.Static);

            if (tryGetMethod == null)
            {
                // Fallback: пробуем instance-метод
                tryGetMethod = loader.AutomationType.GetMethod("TryGetRunningInstance",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            if (tryGetMethod == null)
                throw new Exception("TryGetRunningInstance не найден в NavisworksApplication — версия API не поддерживается");

            bool isStatic = tryGetMethod.IsStatic;
            object tempInstance = isStatic ? null : Activator.CreateInstance(loader.AutomationType);

            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (manualRoamer.HasExited)
                    throw new Exception("Roamer.exe завершился сразу после запуска (код: " + manualRoamer.ExitCode + ")");

                Thread.Sleep(1000);

                try
                {
                    object r = tryGetMethod.Invoke(tempInstance, null);
                    if (r != null)
                    {
                        result = r;
                        Log.Write("TryGetRunningInstance: подключено (попытка " + (attempt + 1) + ")");
                        break;
                    }
                }
                catch { }
            }

            if (tempInstance != null && !ReferenceEquals(tempInstance, result))
            { try { ((IDisposable)tempInstance).Dispose(); } catch { } }

            if (result == null)
                throw new Exception("Не удалось подключиться к Roamer.exe через TryGetRunningInstance (тайм-аут 30 сек)");

            try { result.Visible = visible; } catch { }

            // Подключение получено — но объект должен ещё и устойчиво отвечать.
            // Трёх секунд вслепую не хватало: первый же вызов плагина рвал связь.
            // Спрашиваем несколько раз подряд и только потом отдаём наружу.
            int steady = 0;
            for (int i = 0; i < 10 && steady < 2; i++)
            {
                Thread.Sleep(500);
                if (Responds(result)) steady++;
                else steady = 0;
            }
            Log.Write(steady >= 2
                ? "объект автоматизации отвечает устойчиво"
                : "объект автоматизации отвечает неустойчиво — работа может прерваться");
            return result;
        }

        /// <summary>
        /// Отвечает ли объект автоматизации на обращение.
        ///
        /// Наличие процесса ничего не гарантирует: объект может быть создан, а
        /// связи с процессом не быть. Дешёвое обращение к свойству отличает
        /// одно от другого сразу, а не на первом полезном вызове через минуту.
        /// </summary>
        static bool Responds(dynamic nw)
        {
            try
            {
                object probe = nw.Visible;   // безобидное чтение
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("объект автоматизации не отвечает: " + ex.Message);
                return false;
            }
        }

        // --------------------------------------------------------------------
        // Вызов OpenFile через Reflection (обходит проблемы DLR с void-методами
        // и изменение сигнатуры в Navisworks 2025+/2026).
        // Пробует: OpenFile(string, string[]) → OpenFile(string)
        // --------------------------------------------------------------------
        public static void InvokeOpenFile(object nw, NwLoader loader, string filePath)
        {
            Type t = nw.GetType();
            // Ищем все перегрузки OpenFile
            MethodInfo[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "OpenFile").ToArray();

            Log.Write("OpenFile перегрузок: " + methods.Length);
            foreach (var m in methods)
            {
                var parms = m.GetParameters();
                Log.Write("  " + m.ReturnType.Name + " OpenFile(" +
                    string.Join(", ", parms.Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            }

            // 1) Пробуем двухпараметровую сигнатуру: OpenFile(string, string[]) — Navisworks 2025+
            MethodInfo twoArg = methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string[]);
            });
            if (twoArg != null)
            {
                Log.Write("вызов OpenFile(string, string[]) через Reflection");
                twoArg.Invoke(nw, new object[] { filePath, new string[0] });
                return;
            }

            // 2) Однопараметровая: OpenFile(string) — Navisworks 2017-2024
            MethodInfo oneArg = methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 1 && p[0].ParameterType == typeof(string);
            });
            if (oneArg != null)
            {
                Log.Write("вызов OpenFile(string) через Reflection");
                oneArg.Invoke(nw, new object[] { filePath });
                return;
            }

            // 3) Если ничего не нашли — fallback через dynamic
            Log.Write("предупреждение: OpenFile через Reflection не найден, пробуем dynamic");
            dynamic d = nw;
            try { d.OpenFile(filePath, new string[0]); }
            catch { d.OpenFile(filePath); }
        }

        // --------------------------------------------------------------------
        public static object ResolveDocument(object nw, NwLoader loader)
        {
            // 1) свойство Document у автоматизации
            if (nw != null)
            {
                object d = Dyn.Get(nw, "Document");
                if (d != null)
                {
                    try { if (Dyn.Get(d, "Models") != null) { Log.Write("Document получен через NavisworksApplication.Document"); return d; } }
                    catch { }
                }
            }
            // 2) статический Autodesk.Navisworks.Api.Application.ActiveDocument
            if (loader != null && loader.ApiApplicationType != null)
            {
                try
                {
                    PropertyInfo pi = loader.ApiApplicationType.GetProperty("ActiveDocument",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (pi != null)
                    {
                        object d = pi.GetValue(null, null);
                        if (d != null) { Log.Write("Document получен через Application.ActiveDocument"); return d; }
                    }
                }
                catch (Exception ex) { Log.Write("ActiveDocument: " + ex.GetType().Name + " " + ex.Message); }
            }
            return null;
        }

        static string FindNavisworks(AppOptions opts)
        {
            if (!string.IsNullOrEmpty(opts.NavisworksDir))
            {
                if (File.Exists(Path.Combine(opts.NavisworksDir, "Autodesk.Navisworks.Automation.dll")))
                    return Path.GetFullPath(opts.NavisworksDir);
                Log.Write("указанная папка Navisworks не подходит: " + opts.NavisworksDir);
            }
            foreach (NwInstall ni in NwDetect.Find())
            {
                Log.Write("найден Navisworks: " + ni);
                if (ni.HasAutomation && ni.HasApi) return ni.Dir;
            }
            return null;
        }

        static int Count(object col)
        {
            int n = 0;
            try { foreach (object o in (IEnumerable)col) n++; } catch { }
            return n;
        }

        static string SafeStr(object o)
        {
            if (o == null) return "";
            string s = o as string;
            if (s == null) { try { s = o.ToString(); } catch { s = ""; } }
            return s ?? "";
        }

        static int ReadUnits(dynamic doc)
        {
            try
            {
                object u = Dyn.Get(doc, "Units");
                if (u != null)
                {
                    string name = u.ToString();
                    Log.Write("единицы Navisworks: " + name);
                    string n = name.ToLowerInvariant();
                    if (n.Contains("milli")) return 4;
                    if (n.Contains("centi")) return 5;
                    if (n.Contains("meter")) return 6;
                    if (n.Contains("kilo")) return 7;
                    if (n.Contains("inch")) return 1;
                    if (n.Contains("feet") || n.Contains("foot")) return 2;
                    if (n.Contains("yard")) return 3;
                    if (n.Contains("mile")) return 8;
                    if (n.Contains("micro")) return 13;
                }
            }
            catch { }
            return 6;
        }

        static bool TryCallMethod(object o, string name, out object result)
        {
            result = null;
            try
            {
                dynamic d = o;
                switch (name)
                {
                    case "CloseFile": d.CloseFile(); result = null; break;
                    default: return false;
                }
                return true;
            }
            catch { return false; }
        }

        // --------------------------------------------------------------------
        // Обход геометрии всех моделей/элементов, вызов emit(layer, rgb, verts, quads)
        // --------------------------------------------------------------------
        static void WalkGeometry(AppOptions opts, NwLoader loader, object models, ConvertStats stats,
                                 int totalItems, Action<string> status, Action<double> progress,
                                 Func<bool> cancelled, Action<string, int, IList<double>, IList<int>> emit)
        {
            var sink = new PrimitiveSink();
            object cb = CallbackFactory.CreateProxy(loader, sink);

            int itemIdx = 0;
            bool matrixStrategyLogged = false;
            int matrixStrategy = 0; // 0=unknown,1=GetLocalToWorldMatrix().Matrix,2=GetLocalToWorldTransformMatrix()

            foreach (dynamic model in (dynamic)models)
            {
                dynamic root = Dyn.Get(model, "RootItem");
                if (root == null) continue;
                string modelName = SafeStr(Dyn.Get(model, "DisplayName"));
                if (string.IsNullOrEmpty(modelName)) modelName = "Model";

                foreach (dynamic item in Dyn.Get(root, "DescendantsAndSelf"))
                {
                    itemIdx++;
                    if (cancelled != null && cancelled()) throw new OperationCanceledException("отменено пользователем");

                    if (itemIdx % 25 == 0)
                    {
                        if (progress != null) progress((double)itemIdx / Math.Max(1, totalItems));
                        if (status != null)
                            status(string.Format("Элемент {0} / {1}  (треугольников: {2})",
                                itemIdx, totalItems, stats.Triangles));
                        Application.DoEvents();
                    }

                    // скрытые элементы
                    if (opts.SkipHidden)
                    {
                        object hidden = Dyn.Get(item, "IsHidden");
                        if (hidden is bool && (bool)hidden) { stats.HiddenSkipped++; continue; }
                    }

                    // цвет элемента
                    int rgb = -1;
                    if (opts.WithColors) rgb = ItemColor(item);

                    // путь элемента
                    object oaPath = null;
                    try { oaPath = loader.ToInwOaPath.Invoke(null, new[] { (object)item }); }
                    catch { }
                    if (oaPath == null) continue;

                    object paths = Dyn.Call(oaPath, "Paths");
                    if (paths == null) continue;

                    foreach (dynamic path3 in (dynamic)paths)
                    {
                        object fragments = Dyn.Call(path3, "Fragments");
                        if (fragments == null) continue;

                        foreach (dynamic frag in (dynamic)fragments)
                        {
                            if (!SamePath(frag, path3)) continue;
                            if (opts.SkipHidden)
                            {
                                object h = Dyn.Get(frag, "IsHidden");
                                if (h is bool && (bool)h) { stats.HiddenSkipped++; continue; }
                            }

                            double[] m = GetMatrix(frag, ref matrixStrategy, ref matrixStrategyLogged);
                            if (m == null) continue;

                            sink.Reset(m);
                            try
                            {
                                object r = Dyn.Call(frag, "GenerateSimplePrimitives", loader.NormalEnum, cb);
                                if (r == null && sink.TriCount == 0 && stats.Fragments < 5)
                                    Log.Write("внимание: GenerateSimplePrimitives вернул null/пусто для фрагмента");
                            }
                            catch (Exception ex)
                            {
                                if (stats.Fragments < 5)
                                    Log.Write("ошибка GenerateSimplePrimitives: " + ex.GetType().Name + " " + ex.Message);
                            }

                            stats.Fragments++;
                            stats.Triangles += sink.TriCount;
                            stats.Vertices += sink.Verts.Count / 3;
                            stats.Degenerate += sink.SkippedDegenerate;
                            stats.VertexErrors += sink.VertexReadErrors;

                            string layer = opts.LayersPerItem
                                ? SafeStr(Dyn.Get(item, "DisplayName"))
                                : modelName;
                            if (string.IsNullOrEmpty(layer)) layer = modelName;

                            if (sink.TriCount > 0)
                                emit(layer, rgb, sink.Verts, sink.Quads);
                        }
                    }
                }
            }

            if (progress != null) progress(1.0);
            Log.Write(string.Format("обход завершён: фрагментов {0}, скрытых пропущено {1}, вырожденных {2}, ошибок вершин {3}",
                stats.Fragments, stats.HiddenSkipped, stats.Degenerate, stats.VertexErrors));
        }

        static bool SamePath(object frag, object path3)
        {
            Array a = Dyn.Get(Dyn.Get(frag, "path"), "ArrayData") as Array;
            Array b = Dyn.Get(path3, "ArrayData") as Array;
            if (a == null || b == null) return true; // не удалось сравнить — принимаем
            if (a.Length != b.Length) return false;
            try
            {
                int lba = a.GetLowerBound(0), lbb = b.GetLowerBound(0);
                for (int i = 0; i < a.Length; i++)
                    if (Convert.ToInt32(a.GetValue(lba + i)) != Convert.ToInt32(b.GetValue(lbb + i))) return false;
            }
            catch { return true; }
            return true;
        }

        static double[] GetMatrix(object frag, ref int strategy, ref bool logged)
        {
            if (strategy != 2)
            {
                object t = Dyn.Call(frag, "GetLocalToWorldMatrix");
                object m = Dyn.Get(t, "Matrix");
                double[] r = To16(m);
                if (r != null)
                {
                    if (!logged) { Log.Write("матрица: strategy 1 (GetLocalToWorldMatrix().Matrix)"); logged = true; }
                    strategy = 1;
                    return r;
                }
            }
            if (strategy != 1)
            {
                object t = Dyn.Call(frag, "GetLocalToWorldTransformMatrix");
                double[] r = To16(t);
                if (r != null)
                {
                    if (!logged) { Log.Write("матрица: strategy 2 (GetLocalToWorldTransformMatrix())"); logged = true; }
                    strategy = 2;
                    return r;
                }
            }
            return null;
        }

        static double[] To16(object o)
        {
            Array a = o as Array;
            if (a == null) return null;
            try
            {
                if (a.Length < 16) return null;
                var r = new double[16];
                int lb = a.GetLowerBound(0);
                for (int i = 0; i < 16; i++) r[i] = Convert.ToDouble(a.GetValue(lb + i));
                return r;
            }
            catch { return null; }
        }

        // --------------------------------------------------------------------
        // Цвет элемента
        // --------------------------------------------------------------------
        static int ItemColor(object item)
        {
            try
            {
                // 1) ModelItem.Geometry.OriginalColor
                object geom = Dyn.Get(item, "Geometry");
                if (geom != null)
                {
                    object oc = Dyn.Get(geom, "OriginalColor");
                    int c = ParseColor(oc);
                    if (c >= 0) return c;
                }
                // 2) PropertyCategories: Item / ItemColor
                object pc = Dyn.Get(item, "PropertyCategories");
                if (pc != null)
                {
                    object prop = Dyn.Call(pc, "FindPropertyByName", "Item", "ItemColor");
                    if (prop == null) prop = Dyn.Call(pc, "FindPropertyByDisplayName", "Item", "Color");
                    if (prop != null)
                    {
                        object val = Dyn.Get(prop, "Value");
                        object ds = Dyn.Call(val, "ToDisplayString");
                        int c = ParseColor(ds);
                        if (c >= 0) return c;
                    }
                }
            }
            catch { }
            return -1;
        }

        static int ParseColor(object o)
        {
            try
            {
                if (o == null) return -1;
                if (o is Color)
                {
                    Color c = (Color)o;
                    return (c.R << 16) | (c.G << 8) | c.B;
                }
                if (o is int || o is uint)
                {
                    int v = Convert.ToInt32(o);
                    if (v >= 0 && v <= 0xFFFFFF) return v;
                    return -1;
                }
                string s = o.ToString();
                if (string.IsNullOrEmpty(s)) return -1;
                string[] parts = s.Split(',');
                var nums = new List<int>();
                foreach (string p in parts)
                {
                    int n;
                    string t = p.Trim();
                    if (t.StartsWith("Color [") || t.StartsWith("A=")) continue;
                    if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) nums.Add(n);
                }
                if (nums.Count >= 3)
                {
                    int r = nums[nums.Count - 3], g = nums[nums.Count - 2], b = nums[nums.Count - 1];
                    if (r < 0) r = 0; if (r > 255) r = 255;
                    if (g < 0) g = 0; if (g > 255) g = 255;
                    if (b < 0) b = 0; if (b > 255) b = 255;
                    return (r << 16) | (g << 8) | b;
                }
            }
            catch { }
            return -1;
        }
    }

    // ------------------------------------------------------------------------
    // Диагностика и предварительная проверка
    // ------------------------------------------------------------------------
    public static class Diagnostics
    {
        public static string Run(bool includeApiCheck)
        {
            var sb = new StringBuilder();
            void W(string s) { sb.AppendLine(s); Log.Write(s); }

            W("=== NWD2DWG диагностика ===");
            W("время: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            W("OS: " + Environment.OSVersion);
            W(".NET: " + Environment.Version + (Environment.Is64BitProcess ? " (x64)" : " (x86)"));
            W("exe: " + (Assembly.GetEntryAssembly() != null ? Assembly.GetEntryAssembly().Location : "?"));

            W("");
            W("--- Navisworks ---");
            var installs = NwDetect.Find();
            if (installs.Count == 0) W("Navisworks НЕ НАЙДЕН");
            foreach (var ni in installs) W(ni.ToString());

            W("");
            W("--- AutoCAD (COM) ---");
            var ids = AcadWriter.ListProgIds();
            if (ids.Count == 0) W("AutoCAD COM-сервер НЕ НАЙДЕН (режим DWG будет недоступен)");
            else foreach (string id in ids) W("найден: " + id);

            if (includeApiCheck)
            {
                W("");
                W("--- Проверка API Navisworks ---");
                NwInstall best = installs.FirstOrDefault(i => i.HasAutomation && i.HasApi);
                if (best == null)
                {
                    W("НЕВОЗМОЖНО: нет папки Navisworks с Automation API");
                }
                else
                {
                    var loader = new NwLoader();
                    if (loader.Load(best.Dir))
                    {
                        W("сборки загружены: OK");
                        if (loader.Check())
                        {
                            W("AutomationApplication: " + (loader.AutomationType != null ? "OK" : "НЕТ"));
                            W("Api.Application: " + (loader.ApiApplicationType != null ? "OK" : "НЕТ"));
                            W("ComApiBridge.ToInwOaPath: " + (loader.ToInwOaPath != null ? "OK" : "НЕТ"));
                            W("nwEVertexProperty.eNORMAL: " + (loader.NormalEnum != null ? "OK" : "НЕТ"));
                            W("InwSimplePrimitivesCB: " + (loader.CallbackIface != null ? "OK" : "НЕТ"));
                            if (loader.CallbackIface != null)
                            {
                                try { CallbackFactory.Build(loader.CallbackIface); W("динамический callback: OK"); }
                                catch (Exception ex) { W("динамический callback: ОШИБКА " + ex.Message); }
                            }
                            W("ВЫВОД: API готов к работе.");
                        }
                        else W("ВЫВОД: проблемы — " + loader.LastError);
                    }
                    else W("ВЫВОД: загрузка не удалась — " + loader.LastError);
                }
            }

            W("");
            W("=== конец диагностики ===");
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------------
    // Самотест (без Navisworks): проверка записи DXF
    // ------------------------------------------------------------------------
    public static class SelfTest
    {
        public static int Run(string[] args)
        {
            string dir = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "NWD2DWG_selftest");
            try { Directory.CreateDirectory(dir); } catch { }

            var report = new StringBuilder();
            bool failed = false;
            void W(string s)
            {
                // маркер провала ловим здесь, а не в тексте отчёта в конце:
                // исключение писало САМОТЕСТ УПАЛ, подстроки ОШИБК там нет,
                // и упавший самотест возвращал 0 — сборка считала его успешной
                if (s != null && (s.IndexOf("ОШИБК", StringComparison.Ordinal) >= 0
                               || s.IndexOf("УПАЛ", StringComparison.Ordinal) >= 0
                               || s.IndexOf("FAIL", StringComparison.Ordinal) >= 0)) failed = true;
                report.AppendLine(s); Console.WriteLine(s); Log.Write(s);
            }

            try
            {
                // куб 2x2x2 с вершиной в (0,0,0)
                double[,] cubeV = { {0,0,0},{2,0,0},{2,2,0},{0,2,0},{0,0,2},{2,0,2},{2,2,2},{0,2,2} };
                int[,] cubeF = {
                    {0,1,2},{0,2,3}, {4,6,5},{4,7,6},
                    {0,4,5},{0,5,1}, {1,5,6},{1,6,2},
                    {2,6,7},{2,7,3}, {3,7,4},{3,4,0} };

                var sink = new PrimitiveSink();
                var m = new double[16];
                for (int i = 0; i < 16; i++) m[i] = (i % 5 == 0) ? 1.0 : 0.0;
                sink.Reset(m);
                for (int f = 0; f < 12; f++)
                {
                    int a = cubeF[f, 0], b = cubeF[f, 1], c = cubeF[f, 2];
                    sink.Handle("Triangle", new object[] { new DummyVertex(cubeV[a, 0], cubeV[a, 1], cubeV[a, 2]),
                                                          new DummyVertex(cubeV[b, 0], cubeV[b, 1], cubeV[b, 2]),
                                                          new DummyVertex(cubeV[c, 0], cubeV[c, 1], cubeV[c, 2]) });
                }
                W("сгенерировано треугольников: " + sink.TriCount + " (ожидается 12), вершин: " + sink.Verts.Count / 3 + " (ожидается 8)");
                W("вырожденных пропущено: " + sink.SkippedDegenerate + ", ошибок чтения вершин: " + sink.VertexReadErrors);
                if (sink.TriCount != 12 || sink.Verts.Count / 3 != 8) W("ОШИБКА: неверное число треугольников/вершин");

                string p1 = Path.Combine(dir, "selftest_polyface.dxf");
                using (var w = new DxfWriter(p1, 6))
                {
                    w.BeginEntities(new[] { "Модель Стены-1", "Model" });
                    w.AddPolyface(sink.Verts, sink.Quads, "Модель Стены-1", (255 << 16) | (128 << 8) | 0);
                    w.Finish();
                }

                string p2 = Path.Combine(dir, "selftest_3dface.dxf");
                using (var w = new DxfWriter(p2, 4))
                {
                    w.BeginEntities(new[] { "0" });
                    for (int i = 0; i < sink.Quads.Count; i += 4)
                    {
                        int a = sink.Quads[i], b = sink.Quads[i + 1], c = sink.Quads[i + 2];
                        w.Add3dFace(sink.Verts[a * 3], sink.Verts[a * 3 + 1], sink.Verts[a * 3 + 2],
                                    sink.Verts[b * 3], sink.Verts[b * 3 + 1], sink.Verts[b * 3 + 2],
                                    sink.Verts[c * 3], sink.Verts[c * 3 + 1], sink.Verts[c * 3 + 2], "0", -1);
                    }
                    w.Finish();
                }

                // проверка содержимого (нормализуем переводы строк)
                string t1 = File.ReadAllText(p1).Replace("\r\n", "\n");
                int polylines = CountOccurrences(t1, "\nPOLYLINE\n");
                int seqends = CountOccurrences(t1, "\nSEQEND\n");
                int vverts = CountOccurrences(t1, "\n192\n");
                int fverts = CountOccurrences(t1, "\n128\n");
                bool hasCyrLayer = t1.Contains("Model_Steny-1") || t1.Contains("Model");
                W("polyface.dxf: POLYLINE=" + polylines + " SEQEND=" + seqends + " VERTEX(192)=" + vverts + " VERTEX(128)=" + fverts);
                if (polylines != 1 || seqends != 1 || vverts != 8 || fverts != 12) W("ОШИБКА: структура polyface.dxf неверна");

                string t2 = File.ReadAllText(p2).Replace("\r\n", "\n");
                int f3 = CountOccurrences(t2, "\n3DFACE\n");
                W("3dface.dxf: 3DFACE=" + f3 + " (ожидается 12)");
                if (f3 != 12) W("ОШИБКА: структура 3dface.dxf неверна");

                W("размер polyface.dxf: " + new FileInfo(p1).Length + " байт");
                W("размер 3dface.dxf: " + new FileInfo(p2).Length + " байт");

                // === v2.0: Тест MeshDecimator ===
                var decVerts = new List<double>(sink.Verts);
                var decQuads = new List<int>(sink.Quads);
                Plugin.MeshDecimator.Decimate(ref decVerts, ref decQuads, 0.5);
                int decTris = decQuads.Count / 4;
                W(string.Format("MeshDecimator: исходно 12 треугольников -> после 50% декимации: {0} треугольников (вершин: {1})",
                    decTris, decVerts.Count / 3));
                if (decTris >= 12 || decTris == 0) W("ОШИБКА: MeshDecimator не уменьшил количество треугольников");

                // === v2.0: Тест SolidReconstructor ===
                var solidRes = Plugin.SolidReconstructor.TryReconstruct(sink.Verts, sink.Quads);
                W(string.Format("SolidReconstructor: тип={0}, уверенность={1:F2}, размеры=({2:F1}x{3:F1}x{4:F1})",
                    solidRes.Type, solidRes.Confidence, solidRes.Width, solidRes.Depth, solidRes.Height));
                if (solidRes.Type != Plugin.SolidType.Box) W("ОШИБКА: SolidReconstructor не определил коробку");

                // === v2.0: Тест GltfWriter (.gltf и .glb) ===
                string gltfPath = Path.Combine(dir, "selftest.gltf");
                string glbPath = Path.Combine(dir, "selftest.glb");
                var gltfTris = new List<int>();
                for (int qi = 0; qi < sink.Quads.Count; qi += 4)
                {
                    gltfTris.Add(sink.Quads[qi]);
                    gltfTris.Add(sink.Quads[qi + 1]);
                    gltfTris.Add(sink.Quads[qi + 2]);
                }
                var gltfMesh = new Plugin.GltfMeshData
                {
                    Name = "Cube",
                    Verts = sink.Verts,
                    Indices = gltfTris,
                    Rgb = (255 << 16) | (128 << 8),
                    Transparency = 0.2
                };
                var gwJson = new Plugin.GltfWriter(gltfPath);
                gwJson.AddMesh(gltfMesh);
                gwJson.Write();

                var gwBin = new Plugin.GltfWriter(glbPath);
                gwBin.AddMesh(gltfMesh);
                gwBin.Write();

                bool gltfOk = File.Exists(gltfPath) && new FileInfo(gltfPath).Length > 100;
                bool glbOk = File.Exists(glbPath) && new FileInfo(glbPath).Length > 100;
                W(string.Format("GltfWriter: gltf={0} ({1} байт), glb={2} ({3} байт)",
                    gltfOk ? "OK" : "FAIL", gltfOk ? new FileInfo(gltfPath).Length : 0,
                    glbOk ? "OK" : "FAIL", glbOk ? new FileInfo(glbPath).Length : 0));
                if (!gltfOk || !glbOk) W("ОШИБКА: glTF/GLB экспорт не удался");

                // === v2.0: Тест IfcWriter ===
                string ifcPath = Path.Combine(dir, "selftest.ifc");
                var ifcProps = new Dictionary<string, string>
                {
                    { "Item::Name", "TestCube" },
                    { "Material::Type", "Concrete" }
                };
                var ifcMesh = new Plugin.IfcMeshData
                {
                    Name = "TestCube",
                    Layer = "Structures",
                    Verts = sink.Verts,
                    Indices = gltfTris,
                    Rgb = (200 << 16) | (200 << 8) | 200,
                    Properties = ifcProps
                };
                var iw = new Plugin.IfcWriter(ifcPath);
                iw.AddElement(ifcMesh);
                iw.Write();

                bool ifcOk = File.Exists(ifcPath) && new FileInfo(ifcPath).Length > 200;
                string ifcText = ifcOk ? File.ReadAllText(ifcPath) : "";
                bool ifcValid = ifcText.Contains("IFCPROJECT") && ifcText.Contains("IFCFACE") && ifcText.Contains("ISO-10303-21");
                W(string.Format("IfcWriter: ifc={0} ({1} байт, валидный STEP={2})",
                    ifcOk ? "OK" : "FAIL", ifcOk ? new FileInfo(ifcPath).Length : 0, ifcValid ? "ДА" : "НЕТ"));
                if (!ifcOk || !ifcValid) W("ОШИБКА: IFC экспорт не удался или невалиден");

                // === v3.0: Тест GeoTransform ===
                var geoVerts = new List<double> { 5000000.0, 2000000.0, 150.0, 5000002.0, 2000002.0, 152.0 };
                var geoRes = Plugin.GeoTransform.AnalyzeBounds(geoVerts, 1000.0);
                Plugin.GeoTransform.ApplyShift(geoVerts, geoRes.OffsetX, geoRes.OffsetY, geoRes.OffsetZ);
                bool geoOk = geoRes.IsShifted && Math.Abs(geoVerts[0]) < 10.0;
                W(string.Format("GeoTransform: смещение=({0:F0}, {1:F0}, {2:F0}), сдвиг к нулю={3}",
                    geoRes.OffsetX, geoRes.OffsetY, geoRes.OffsetZ, geoOk ? "OK" : "FAIL"));
                if (!geoOk) W("ОШИБКА: GeoTransform не рассчитал сдвиг геометрии к нулю");

                // === v3.0: Тест GridExtractor ===
                string gridDxfPath = Path.Combine(dir, "selftest_grids.dxf");
                using (var gw = new StreamWriter(gridDxfPath, false, Encoding.UTF8))
                {
                    gw.WriteLine("0\nSECTION\n2\nENTITIES");
                    var grids = new List<Plugin.GridLineData>
                    {
                        new Plugin.GridLineData { Name = "1", StartX = 0, StartY = 0, StartZ = 0, EndX = 0, EndY = 10000, EndZ = 0, IsLevel = false },
                        new Plugin.GridLineData { Name = "Ур.+3.000", StartX = -5000, StartY = 0, StartZ = 3000, EndX = 5000, EndY = 0, EndZ = 3000, IsLevel = true }
                    };
                    Plugin.GridExtractor.WriteGridsToDxf(gw, grids, 300.0);
                    gw.WriteLine("0\nENDSEC\n0\nEOF");
                }
                bool gridOk = File.Exists(gridDxfPath) && File.ReadAllText(gridDxfPath).Contains("_GRIDS") && File.ReadAllText(gridDxfPath).Contains("_LEVELS");
                W("GridExtractor: экспорт осей и отметок уровней=" + (gridOk ? "OK" : "FAIL"));
                if (!gridOk) W("ОШИБКА: GridExtractor не записал оси в DXF");

                // === v3.0: Тест PipeTracer ===
                var cylSolid = new Plugin.SolidResult
                {
                    Type = Plugin.SolidType.Cylinder,
                    CenterX = 100, CenterY = 200, CenterZ = 50,
                    AxisX = 1, AxisY = 0, AxisZ = 0,
                    Radius = 54.0, Height = 1000.0, Confidence = 0.95
                };
                var pipeSeg = Plugin.PipeTracer.TraceFromSolid(cylSolid, "Heating");
                bool pipeOk = pipeSeg != null && pipeSeg.Diameter == 108.0 && pipeSeg.Length == 1000.0;
                W(string.Format("PipeTracer: трассировка трубы DN{0:F0} L={1:F0}={2}",
                    pipeSeg != null ? pipeSeg.Diameter : 0, pipeSeg != null ? pipeSeg.Length : 0, pipeOk ? "OK" : "FAIL"));
                if (!pipeOk) W("ОШИБКА: PipeTracer не извлек параметры трубопровода");

                // === v3.0: Тест BoqCalculator ===
                var boq = new Plugin.BoqCalculator();
                boq.AddMesh("Architecture", "Wall_Concrete", "Concrete_B25", sink.Verts, sink.Quads);
                string boqCsvPath = Path.Combine(dir, "selftest_boq.csv");
                boq.ExportCsv(boqCsvPath);
                bool boqOk = File.Exists(boqCsvPath) && File.ReadAllText(boqCsvPath).Contains("Concrete_B25");
                W("BoqCalculator: расчет объемов ВОР в CSV/Excel=" + (boqOk ? "OK" : "FAIL"));
                if (!boqOk) W("ОШИБКА: BoqCalculator не экспортировал сводную ведомость объемов");

                // === v3.0: Тест BcfExporter ===
                string bcfPath = Path.Combine(dir, "selftest_clashes.bcfzip");
                var bcfTopics = new List<Plugin.BcfTopic>
                {
                    new Plugin.BcfTopic
                    {
                        Title = "Коллизия: Труба ОВ vs Балка КМ",
                        Description = "Пересечение на отм. +3.450",
                        CameraPosX = 100, CameraPosY = 200, CameraPosZ = 300,
                        CameraDirX = 0, CameraDirY = 1, CameraDirZ = 0,
                        CameraUpX = 0, CameraUpY = 0, CameraUpZ = 1
                    }
                };
                Plugin.BcfExporter.ExportBcfZip(bcfPath, bcfTopics);
                bool bcfOk = File.Exists(bcfPath) && new FileInfo(bcfPath).Length > 100;
                W("BcfExporter: генерация пакета коллизий BCF 2.1=" + (bcfOk ? "OK" : "FAIL"));
                if (!bcfOk) W("ОШИБКА: BcfExporter не создал валидный .bcfzip");

                // === v3.0: Тест BimDiff ===
                var oldM = new Dictionary<string, Plugin.DiffElement>
                {
                    { "guid_1", new Plugin.DiffElement { Guid = "guid_1", Name = "Column_A", Verts = new List<double> { 0,0,0, 1,0,0, 0,1,0 } } },
                    { "guid_2", new Plugin.DiffElement { Guid = "guid_2", Name = "Beam_Old", Verts = new List<double> { 0,0,0, 2,0,0, 0,2,0 } } }
                };
                var newM = new Dictionary<string, Plugin.DiffElement>
                {
                    { "guid_1", new Plugin.DiffElement { Guid = "guid_1", Name = "Column_A", Verts = new List<double> { 0,0,0, 1,0,0, 0,1,0 } } }, // Unchanged
                    { "guid_3", new Plugin.DiffElement { Guid = "guid_3", Name = "Pipe_New", Verts = new List<double> { 5,5,5, 6,5,5, 5,6,5 } } }   // Added
                };
                var diffRes = Plugin.BimDiffEngine.Compare(oldM, newM);
                bool diffOk = diffRes.Count == 2; // guid_3 Added, guid_2 Deleted
                W(string.Format("BimDiff: 3D-сравнение версий (найдено изменений: {0})={1}", diffRes.Count, diffOk ? "OK" : "FAIL"));
                if (!diffOk) W("ОШИБКА: BimDiff не выявил изменения в модели");

                // === v3.0: Тест SpatialTiler & BimAnonymizer ===
                var tileKey = Plugin.SpatialTiler.GetTileKey(25000, 45000, 3000, 20000);
                bool tileOk = tileKey.TileX == 1 && tileKey.TileY == 2 && tileKey.TileZ == 0;
                var rawP = new Dictionary<string, string> { { "Cost::Price", "100000" }, { "General::Category", "Duct" } };
                var cleanP = Plugin.BimAnonymizer.SanitizeProperties(rawP);
                bool anonOk = !cleanP.ContainsKey("Cost::Price") && cleanP.ContainsKey("General::Category");
                W(string.Format("SpatialTiler & BimAnonymizer: захватка={0}, анонимизация={1}",
                    tileOk ? "OK" : "FAIL", anonOk ? "OK" : "FAIL"));

                // === v3.1: Тест ClashClusterer ===
                var cPts = new List<Plugin.ClashPoint> {
                    new Plugin.ClashPoint(100, 100, 100, "C1"),
                    new Plugin.ClashPoint(150, 120, 110, "C2"),
                    new Plugin.ClashPoint(5000, 5000, 5000, "C3")
                };
                var clusters = Plugin.ClashClusterer.Cluster(cPts, 500.0, 2);
                bool clusterOk = clusters.Count == 1 && clusters[0].Points.Count == 2;
                W("ClashClusterer (DBSCAN 3D): " + (clusterOk ? "OK" : "FAIL"));

                // === v3.1: Тест Section2Plan ===
                var planPolys = Plugin.Section2Plan.Slice(sink.Verts, sink.Quads, 1.0, 0.1);
                bool planOk = planPolys.Count > 0;
                W("Section2Plan (2D срез сечений): " + (planOk ? "OK" : "FAIL"));

                // === v3.1: Тест CadPurger ===
                string purgedDxf = Path.Combine(dir, "selftest_purged.dxf");
                string purgeLog = Plugin.CadPurger.Purge(p1, purgedDxf);
                bool purgeOk = File.Exists(purgedDxf) && new FileInfo(purgedDxf).Length > 50;
                W("CadPurger (DXF Deep Clean): " + (purgeOk ? "OK" : "FAIL"));

                // === v3.2: Тест PenetrationBuilder ===
                var testPipes = new List<Plugin.PipeAxis> {
                    new Plugin.PipeAxis { Ax = -1000, Ay = 500, Az = 500, Bx = 1000, By = 500, Bz = 500, DN = 100, SystemName = "Heating" }
                };
                var testPlanes = new List<Plugin.ConstructionPlane> {
                    new Plugin.ConstructionPlane { Nx = 1, Ny = 0, Nz = 0, D = 0, Thickness = 200, ElementName = "Wall_1", ElementType = "Wall", MinX = -100, MaxX = 100, MinY = 0, MaxY = 1000, MinZ = 0, MaxZ = 1000 }
                };
                var pens = Plugin.PenetrationBuilder.Build(testPipes, testPlanes);
                bool penOk = pens.Count == 1 && pens[0].SleeveD == 150.0;
                W("PenetrationBuilder (Авторасстановка гильз DN+50): " + (penOk ? "OK" : "FAIL"));

                // === v3.2: Тест ClearanceValidator ===
                var testBoxes = new List<Plugin.SceneBox> {
                    new Plugin.SceneBox { MinX = 0, MinY = 0, MinZ = 0, MaxX = 2000, MaxY = 2000, MaxZ = 0, IsFloor = true, Name = "Floor" },
                    new Plugin.SceneBox { MinX = 500, MinY = 500, MinZ = 1500, MaxX = 1500, MaxY = 1500, MaxZ = 1800, IsFloor = false, Name = "LowDuct" }
                };
                var viol = Plugin.ClearanceValidator.Validate(testBoxes, 2000.0, 500.0);
                bool clearOk = viol.Count > 0 && viol[0].Clearance == 1500.0;
                W("ClearanceValidator (Высота проходов СП 118): " + (clearOk ? "OK" : "FAIL"));

                // === v3.2: Тест SteelProfileMatcher ===
                var beamVerts = new List<double>();
                for (int bx = 0; bx <= 3000; bx += 1000)
                {
                    beamVerts.AddRange(new double[] { bx, -50, -100,  bx, 50, -100,  bx, 50, 100,  bx, -50, 100 });
                }
                var steelMatch = Plugin.SteelProfileMatcher.MatchMesh(beamVerts);
                bool steelOk = steelMatch.Length > 2900 && !string.IsNullOrEmpty(steelMatch.Designation);
                W("SteelProfileMatcher (Сортамент ГОСТ КМ/КМД): " + (steelOk ? "OK" : "FAIL"));

                // === v3.3: Тест CogCalculator ===
                var cogEl = Plugin.CogCalculator.CalculateElement("Cube", sink.Verts, sink.Quads, "Steel");
                bool cogOk = cogEl.MassKg > 0 && Math.Abs(cogEl.CogX - 1.0) < 0.2;
                W("CogCalculator (Центр масс Гаусса-Остроградского): " + (cogOk ? "OK" : "FAIL"));

                // === v3.3: Тест IsoGenerator ===
                var isoNet = Plugin.IsoGenerator.GenerateIsoNetwork(testPipes);
                var isoJoints = Plugin.IsoGenerator.DetectJoints(isoNet);
                bool isoOk = isoNet.Count == 1 && isoJoints.Count == 2;
                W("IsoGenerator (Изометрия трубопроводов ГОСТ 2.317): " + (isoOk ? "OK" : "FAIL"));

                // === v3.4: Тест ScheduleMapper ===
                var tasks4D = new List<Plugin.ScheduleTask> {
                    new Plugin.ScheduleTask { Uid = "1", Name = "Монтаж стен", PlannedStart = DateTime.Now.AddDays(-5), PlannedFinish = DateTime.Now.AddDays(5) }
                };
                var matches4D = Plugin.ScheduleMapper.EvaluateModel(new List<string>{"Стена_1"}, new List<string>{"Wall"}, tasks4D, DateTime.Now);
                bool schedOk = matches4D.Count == 1 && matches4D[0].Status == Plugin.Task4DStatus.InProgress;
                W("ScheduleMapper (4D Calendar Planning): " + (schedOk ? "OK" : "FAIL"));

                // === v3.4: Тест ShrinkWrapper ===
                var wrapRes = Plugin.ShrinkWrapper.WrapMesh(sink.Verts, sink.Quads);
                bool wrapOk = wrapRes.OutVerts.Count == 24 && wrapRes.OutQuads.Count == 24;
                W("ShrinkWrapper (Защита IP / Оболочки OBB): " + (wrapOk ? "OK" : "FAIL"));

                // === v3.4: Тест RoomFinishSchedule ===
                var rData = new Plugin.RoomData {
                    Number = "101", Name = "PumpRoom", HeightMm = 3000,
                    Contour2D = new List<double[]> { new double[]{0,0}, new double[]{4000,0}, new double[]{4000,3000}, new double[]{0,3000} }
                };
                rData.Openings.Add(new Plugin.RoomOpening { WidthMm = 900, HeightMm = 2100, IsDoor = true });
                rData.Calculate();
                bool roomOk = Math.Abs(rData.FloorAreaM2 - 12.0) < 0.01 && rData.NetWallAreaM2 < rData.GrossWallAreaM2;
                W("RoomFinishSchedule (Ведомость отделки ГОСТ 21.501): " + (roomOk ? "OK" : "FAIL"));

                bool allOk = polylines == 1 && seqends == 1 && vverts == 8 && fverts == 12 && f3 == 12
                             && decTris < 12 && solidRes.Type == Plugin.SolidType.Box
                             && gltfOk && glbOk && ifcOk && ifcValid
                             && geoOk && gridOk && pipeOk && boqOk && bcfOk && diffOk && tileOk && anonOk
                             && clusterOk && planOk && purgeOk && penOk && clearOk && steelOk && cogOk && isoOk && schedOk && wrapOk && roomOk;
                W("САМОТЕСТ ПРОЙДЕН: " + (allOk ? "OK (все 31 алгоритм экосистемы v3.4 исправны)" : "ОШИБКИ"));
            }
            catch (Exception ex)
            {
                W("САМОТЕСТ УПАЛ: " + ex);
            }

            try { File.WriteAllText(Path.Combine(dir, "selftest_report.txt"), report.ToString(), Encoding.UTF8); } catch { }
            return failed ? 1 : 0;
        }

        static int CountOccurrences(string s, string sub)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
            return n;
        }

        // обёртка вершины, имитирующая InwSimpleVertex с 1-based SAFEARRAY coord
        class DummyVertex
        {
            readonly Array _coord;
            public DummyVertex(double x, double y, double z)
            {
                var a = Array.CreateInstance(typeof(float), new[] { 3 }, new[] { 1 });
                a.SetValue((float)x, 1); a.SetValue((float)y, 2); a.SetValue((float)z, 3);
                _coord = a;
            }
            public Array coord { get { return _coord; } }
        }
    }

    // ------------------------------------------------------------------------
    // Точка входа
    // ------------------------------------------------------------------------
    public static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        [STAThread]
        static int Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                Log.Write("НЕОБРАБОТАННАЯ ОШИБКА: " + e.Exception);
                try
                {
                    MessageBox.Show("Произошла ошибка:\n" + e.Exception.Message +
                        "\n\nПодробности в логе: " + (Log.FilePath ?? ""),
                        "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Write("КРИТИЧЕСКАЯ ОШИБКА: " + e.ExceptionObject);
                Log.Flush();
            };

            if (args != null && args.Length > 0)
            {
                string cmd = args[0].ToLowerInvariant();
                AttachConsole(-1);

                if (cmd == "--screenshot-license")
                {
                    string shotPath = args.Length > 1 ? args[1] : "license.png";
                    try
                    {
                        var dlg = MainForm.CreateLicenseForm();
                        dlg.StartPosition = FormStartPosition.Manual;
                        dlg.Location = new Point(50, 50);
                        dlg.Show();
                        dlg.Refresh();
                        for (int s = 0; s < 10; s++) { Application.DoEvents(); Thread.Sleep(30); }
                        using (var bmp = new Bitmap(dlg.Width, dlg.Height))
                        {
                            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
                            bmp.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        dlg.Close();
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(shotPath + ".err.txt", ex.ToString());
                    }
                    return 0;
                }

                if (cmd == "--screenshot-about")
                {
                    string shotPath = args.Length > 1 ? args[1] : "about.png";
                    try
                    {
                        var dlg = MainForm.CreateAboutForm();
                        dlg.StartPosition = FormStartPosition.Manual;
                        dlg.Location = new Point(50, 50);
                        dlg.Show();
                        dlg.Refresh();
                        for (int s = 0; s < 10; s++) { Application.DoEvents(); Thread.Sleep(30); }
                        using (var bmp = new Bitmap(dlg.Width, dlg.Height))
                        {
                            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
                            bmp.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        dlg.Close();
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(shotPath + ".err.txt", ex.ToString());
                    }
                    return 0;
                }

                if (cmd == "--screenshot-settings")
                {
                    string shotPath = args.Length > 1 ? args[1] : "settings.png";
                    try
                    {
                        var cfg = AdvancedConfig.Load();
                        var dlg = new ModuleSettingsDialog(cfg, OutputProfile.Load());
                        dlg.StartPosition = FormStartPosition.Manual;
                        dlg.Location = new Point(50, 50);
                        dlg.Show();
                        // Второй аргумент — номер вкладки: снимок нужен для
                        // проверки вёрстки каждой из них, а не только первой.
                        int tabIx;
                        if (args.Length > 2 && int.TryParse(args[2], out tabIx))
                            dlg.SelectTab(tabIx);
                        dlg.Refresh();
                        for (int s = 0; s < 10; s++) { Application.DoEvents(); Thread.Sleep(30); }
                        using (var bmp = new Bitmap(dlg.Width, dlg.Height))
                        {
                            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
                            bmp.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        dlg.Close();
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(shotPath + ".err.txt", ex.ToString());
                    }
                    return 0;
                }

                // Сравнение выдач не требует ни Navisworks, ни исходных моделей:
                // сопоставляются индексы, которые можно переслать смежнику.
                if (cmd == "--diff-index")
                {
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Использование: --diff-index <старый_index.csv> <новый_index.csv> [отчёт.csv]");
                        return 2;
                    }
                    try
                    {
                        var oldIdx = Plugin.RevisionIndex.Read(args[1]);
                        var newIdx = Plugin.RevisionIndex.Read(args[2]);
                        var diff = Plugin.RevisionIndex.Compare(oldIdx, newIdx);
                        Console.WriteLine(Plugin.RevisionIndex.Summary(diff));
                        string shiftNote = Plugin.RevisionIndex.BaseShiftNote(diff);
                        if (shiftNote.Length > 0) Console.WriteLine(shiftNote);

                        string outCsv = args.Length > 3 ? args[3]
                                      : Path.ChangeExtension(args[2], null) + "_diff.csv";
                        Plugin.RevisionIndex.WriteCsv(outCsv, diff,
                            Path.GetFileName(args[1]), Path.GetFileName(args[2]));
                        Plugin.RevisionIndex.WriteDxf(Path.ChangeExtension(outCsv, ".dxf"), diff);
                        Console.WriteLine("отчёт: " + outCsv);
                        Console.WriteLine("метки: " + Path.ChangeExtension(outCsv, ".dxf"));
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("ОШИБКА сравнения: " + ex.Message);
                        return 1;
                    }
                }

                if (cmd == "--delivery-log")
                {
                    string logPath = args.Length > 1 ? args[1] : "";
                    var rows = Plugin.DeliveryLog.Read(logPath);
                    if (rows.Count == 0) { Console.WriteLine("Журнал пуст или не найден: " + logPath); return 1; }
                    Console.WriteLine(Plugin.DeliveryLog.Header);
                    foreach (var r in rows) Console.WriteLine(string.Join(";", r));
                    Console.WriteLine("записей: " + rows.Count);
                    return 0;
                }

                if (cmd == "--selftest") return SelfTest.Run(args);
                if (cmd == "--diagnostics")
                {
                    bool api = !args.Contains("--no-api");
                    string report = Diagnostics.Run(api);
                    Console.WriteLine(report);
                    string file = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
                    if (file != null)
                    {
                        try { File.WriteAllText(file, report, Encoding.UTF8); Console.WriteLine("отчёт сохранён: " + file); } catch { }
                    }
                    return 0;
                }
                if (cmd == "--clean-temp")
                {
                    long freed = NavisConverter.TempCleaner.CleanTempFiles(0);
                    Console.WriteLine(string.Format("Очищено {0:F1} МБ во временной папке.", freed / 1048576.0));
                    return 0;
                }
                if (cmd == "--convert" || cmd == "--probe" || cmd == "--watch" || cmd.StartsWith("--screenshot"))
                {
                    try { return Cli(cmd, args); }
                    catch (Exception ex)
                    {
                        Console.WriteLine("ОШИБКА: " + ex.Message);
                        if (ex.InnerException != null)
                            Console.WriteLine("ПРИЧИНА: " + ex.InnerException);
                        Console.WriteLine("Лог: " + (Log.FilePath ?? "(не задан)"));
                        Log.Write("CLI ОШИБКА: " + ex);
                        Log.Flush();
                        return 1;
                    }
                }
            }

            try { NavisConverter.TempCleaner.CleanTempFiles(12); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        // Установка поля AdvancedConfig по имени. Раньше на каждый ключ
        // приходилось по три строки с ручным разбором и своим TryParse.
        static void AdvNum(AppOptions opts, string field, string next, ref int i)
        {
            if (next == null || next.StartsWith("--")) return;
            var fi = typeof(Plugin.AdvancedConfig).GetField(field);
            double v;
            if (fi != null && double.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                if (fi.FieldType == typeof(int)) fi.SetValue(opts.AdvConfig, (int)Math.Round(v));
                else fi.SetValue(opts.AdvConfig, v);
            }
            i++;
        }

        static void AdvStr(AppOptions opts, string field, string next, ref int i)
        {
            if (next == null || next.StartsWith("--")) return;
            var fi = typeof(Plugin.AdvancedConfig).GetField(field);
            if (fi != null) fi.SetValue(opts.AdvConfig, next);
            i++;
        }

        static string ExtFor(OutFormat f)
        {
            switch (f)
            {
                case OutFormat.Dwg: return ".dwg";
                case OutFormat.Gltf: return ".gltf";
                case OutFormat.Glb: return ".glb";
                case OutFormat.Ifc: return ".ifc";
                default: return ".dxf";
            }
        }

        public static Bitmap RenderControlTree(Control root)
        {
            Bitmap bmp = new Bitmap(root.Width, root.Height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(root.BackColor);
                RenderRecursive(root, g, Point.Empty);
            }
            return bmp;
        }

        private static void RenderRecursive(Control c, Graphics g, Point offset)
        {
            if (!c.Visible || c.Width <= 0 || c.Height <= 0) return;
            Point local = new Point(offset.X + c.Left, offset.Y + c.Top);
            using (Bitmap b = new Bitmap(c.Width, c.Height))
            {
                c.DrawToBitmap(b, new Rectangle(0, 0, c.Width, c.Height));
                g.DrawImage(b, local);
            }
            foreach (Control child in c.Controls)
            {
                RenderRecursive(child, g, local);
            }
        }

        static int Cli(string cmd, string[] args)
        {
            var opts = new AppOptions();
            string outPath = null;

            if (cmd == "--screenshot")
            {
                string shotPath = args.Length > 1 ? args[1] : "screenshot.png";
                try
                {
                    var form = new MainForm();
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(50, 50);
                    form.Size = new Size(1180, 1040);
                    form.Show();
                    form.Refresh();
                    for (int s = 0; s < 10; s++) { Application.DoEvents(); Thread.Sleep(40); }
                    using (var bmp = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                        bmp.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    var sb = new StringBuilder();
                    sb.AppendLine("Form Size=" + form.Size + " ClientSize=" + form.ClientSize);
                    foreach (Control c in form.Controls)
                    {
                        sb.AppendLine(string.Format("Control: Type={0}, Dock={1}, Bounds={2}, Visible={3}", c.GetType().Name, c.Dock, c.Bounds, c.Visible));
                        foreach (Control cc in c.Controls)
                        {
                            sb.AppendLine(string.Format("  SubControl: Type={0}, Dock={1}, Bounds={2}, Visible={3}", cc.GetType().Name, cc.Dock, cc.Bounds, cc.Visible));
                            foreach (Control ccc in cc.Controls)
                            {
                                sb.AppendLine(string.Format("    SubSubControl: Type={0}, Text={1}, Dock={2}, Bounds={3}, Visible={4}", ccc.GetType().Name, ccc.Text, ccc.Dock, ccc.Bounds, ccc.Visible));
                                foreach (Control cccc in ccc.Controls)
                                {
                                    sb.AppendLine(string.Format("      Btn: Text={0}, Bounds={1}, Visible={2}", cccc.Text, cccc.Bounds, cccc.Visible));
                                }
                            }
                        }
                    }
                    File.WriteAllText(shotPath + ".txt", sb.ToString());
                }
                catch (Exception ex)
                {
                    File.WriteAllText(shotPath + ".err.txt", ex.ToString());
                }
                return 0;
            }

            if (cmd == "--screenshot-license")
            {
                string shotPath = args.Length > 1 ? args[1] : "license.png";
                try
                {
                    using (var dlg = MainForm.CreateLicenseForm())
                    {
                        dlg.StartPosition = FormStartPosition.Manual;
                        dlg.Location = new Point(50, 50);
                        dlg.Show();
                        dlg.Refresh();
                        for (int s = 0; s < 10; s++) { Application.DoEvents(); Thread.Sleep(30); }
                        using (var bmp = new Bitmap(dlg.Width, dlg.Height))
                        {
                            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
                            bmp.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        dlg.Close();
                    }
                }
                catch (Exception ex)
                {
                    File.WriteAllText(shotPath + ".err.txt", ex.ToString());
                }
                return 0;
            }

            if (cmd == "--screenshot-about")
            {
                string shotPath = args.Length > 1 ? args[1] : "about.png";
                try
                {
                    using (var dlg = MainForm.CreateAboutForm())
                    {
                        dlg.StartPosition = FormStartPosition.Manual;
                        dlg.Location = new Point(50, 50);
                        dlg.Show();
                        dlg.Refresh();
                        for (int s = 0; s < 10; s++) { Application.DoEvents(); Thread.Sleep(30); }
                        using (var bmp = new Bitmap(dlg.Width, dlg.Height))
                        {
                            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
                            bmp.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        dlg.Close();
                    }
                }
                catch (Exception ex)
                {
                    File.WriteAllText(shotPath + ".err.txt", ex.ToString());
                }
                return 0;
            }

            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a.ToLowerInvariant())
                {
                    case "--format":
                        if (next != null)
                        {
                            switch (next.ToLowerInvariant())
                            {
                                case "3dface": case "dxf3dface": opts.Format = OutFormat.Dxf3dFace; break;
                                case "dwg": opts.Format = OutFormat.Dwg; break;
                                case "gltf": opts.Format = OutFormat.Gltf; break;
                                case "glb": opts.Format = OutFormat.Glb; break;
                                case "ifc": opts.Format = OutFormat.Ifc; break;
                                default: opts.Format = OutFormat.DxfPolyface; break;
                            }
                            i++;
                        }
                        break;
                    case "--visible":
                        opts.ShowNavisworks = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--acadvisible":
                        opts.ShowAutoCad = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--skiphidden":
                    case "--skip-hidden":
                        opts.SkipHidden = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--colors":
                    case "--color":
                        opts.WithColors = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--layers":
                        opts.LayersPerItem = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--split":
                        opts.SplitDisciplines = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--navis":
                        if (next != null && !next.StartsWith("--")) { opts.NavisworksDir = next; i++; }
                        break;
                    // === v2.0 CLI flags ===
                    case "--decimate":
                        if (next != null && !next.StartsWith("--")) { int dp; if (int.TryParse(next, out dp)) opts.DecimatePercent = Math.Max(0, Math.Min(90, dp)); i++; }
                        break;
                    case "--solid":
                    case "--soliddetect":
                        opts.SolidDetect = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--xdata":
                        opts.TransferXData = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--materials":
                        opts.TransferMaterials = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--sets":
                        opts.SelectionSets = next != null && !next.StartsWith("--") ? next : "";
                        if (next != null && !next.StartsWith("--")) i++;
                        break;
                    case "--bbox":
                        if (next != null && !next.StartsWith("--"))
                        {
                            string[] bp = next.Split(',');
                            if (bp.Length == 6)
                            {
                                opts.SectionBox = new double[6];
                                bool ok = true;
                                for (int bi = 0; bi < 6; bi++)
                                    if (!double.TryParse(bp[bi].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out opts.SectionBox[bi]))
                                        ok = false;
                                if (!ok) opts.SectionBox = null;
                            }
                            i++;
                        }
                        break;
                    case "--threads":
                        if (next != null && !next.StartsWith("--")) { int tp; if (int.TryParse(next, out tp)) opts.ParallelThreads = tp; i++; }
                        break;
                    case "--watch":
                        opts.WatchFolder = next != null && !next.StartsWith("--") ? next : "";
                        if (next != null && !next.StartsWith("--")) i++;
                        break;
                    case "--interval":
                        if (next != null && !next.StartsWith("--")) { int iv; if (int.TryParse(next, out iv)) opts.WatchInterval = Math.Max(1, iv); i++; }
                        break;
                    // === v3.0 CLI flags ===
                    case "--geoshift": case "--geo":
                        opts.GeoShift = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--grids":
                        opts.ExportGrids = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--tracepipes": case "--pipes":
                        opts.TracePipes = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--boq":
                        opts.ExportBoq = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--bcf":
                        opts.ExportBcf = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--anonymize":
                        opts.Anonymize = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    // === v3.1 – v3.4 CLI flags ===
                    case "--clash-cluster":
                        opts.ClusterClashes = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--section-plan":
                        opts.SectionPlan = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--purge":
                        opts.PurgeDxf = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--penetrations":
                        opts.BuildPenetrations = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--clearance":
                        opts.ValidateClearance = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--steel":
                        opts.MatchSteel = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--cog":
                        opts.CalcCog = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--iso":
                        opts.GenerateIso = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--4d":
                        opts.MapSchedule4D = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--shrinkwrap":
                        opts.Shrinkwrap = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--room-finish":
                        opts.RoomFinish = next == null || next.StartsWith("--") || (next != "0" && !next.Equals("false", StringComparison.OrdinalIgnoreCase));
                        if (next != null && !next.StartsWith("--") && (next == "0" || next == "1" || next.Equals("true", StringComparison.OrdinalIgnoreCase) || next.Equals("false", StringComparison.OrdinalIgnoreCase))) i++;
                        break;
                    case "--preset":
                        if (next != null && !next.StartsWith("--"))
                        {
                            var pr = Plugin.ConfigPreset.ByName(next);
                            if (pr != null)
                            {
                                pr.ApplyTo(opts.AdvConfig, opts.OutProfile);
                                Console.WriteLine("Применён шаблон: " + pr.Name + " (" + pr.Norms + ")");
                            }
                            else
                            {
                                Console.WriteLine("Неизвестный шаблон: " + next + ". Доступные:");
                                foreach (var p2 in Plugin.ConfigPreset.All) Console.WriteLine("  " + p2.Name);
                            }
                            i++;
                        }
                        break;
                    // === Допуски модулей (все линейные величины — в мм) ===
                    case "--clash-eps":
                        AdvNum(opts, "ClashEpsilonMm", next, ref i); break;
                    case "--clash-minpts":
                        AdvNum(opts, "ClashMinPts", next, ref i); break;
                    case "--clash-mindist":
                    case "--clash-tol":
                        AdvNum(opts, "ClashMinDistanceMm", next, ref i); break;
                    case "--bcf-author":
                        AdvStr(opts, "BcfAuthor", next, ref i); break;
                    case "--sched-source":
                        AdvStr(opts, "ScheduleSource", next, ref i); break;
                    case "--sched-date":
                        AdvStr(opts, "ScheduleStatusDate", next, ref i); break;
                    case "--min-headroom":
                        AdvNum(opts, "MinHeadroomCorridorMm", next, ref i); break;
                    case "--clearance-cell":
                        AdvNum(opts, "ClearanceCellMm", next, ref i); break;
                    case "--section-z":
                        AdvNum(opts, "SectionCutHeightMm", next, ref i); break;
                    case "--section-eps":
                        AdvNum(opts, "SectionDpEpsMm", next, ref i); break;
                    case "--section-layer":
                        AdvStr(opts, "SectionLayer", next, ref i); break;
                    case "--room-min-area":
                        AdvNum(opts, "RoomMinAreaM2", next, ref i); break;
                    case "--room-max-area":
                        AdvNum(opts, "RoomMaxAreaM2", next, ref i); break;
                    case "--room-height":
                        AdvNum(opts, "RoomHeightMm", next, ref i); break;
                    case "--pipe-dn-min":
                        AdvNum(opts, "PipeMinDiameterMm", next, ref i); break;
                    case "--pipe-dn-max":
                        AdvNum(opts, "PipeMaxDiameterMm", next, ref i); break;
                    case "--pipe-min-len":
                        AdvNum(opts, "PipeMinLengthMm", next, ref i); break;
                    case "--sleeve-gap":
                        AdvNum(opts, "SleeveGapMediumMm", next, ref i); break;
                    case "--sleeve-ext":
                        AdvNum(opts, "SleeveExtensionMm", next, ref i); break;
                    case "--sleeve-min-thk":
                        AdvNum(opts, "SleeveMinStructureMm", next, ref i); break;
                    case "--steel-tol":
                        AdvNum(opts, "SteelTolerancePct", next, ref i); break;
                    case "--steel-custom":
                        AdvStr(opts, "SteelIncludeCustom", next, ref i); break;
                    case "--steel-min-len":
                        AdvNum(opts, "SteelMinLengthMm", next, ref i); break;
                    case "--density-steel":
                        AdvNum(opts, "DensitySteel", next, ref i); break;
                    case "--density-concrete":
                        AdvNum(opts, "DensityConcrete", next, ref i); break;
                    case "--density-piping":
                    case "--density-water":
                        AdvNum(opts, "DensityPiping", next, ref i); break;
                    case "--cog-min-mass":
                        AdvNum(opts, "CogMinMassKg", next, ref i); break;
                    case "--decimate-min-tris":
                        AdvNum(opts, "DecimateMinTriangles", next, ref i); break;
                    case "--solid-confidence":
                        AdvNum(opts, "SolidMinConfidence", next, ref i); break;
                    case "--shrink-lvl":
                        AdvNum(opts, "ShrinkwrapLevel", next, ref i); break;
                    case "--boq-group":
                        AdvStr(opts, "BoqGroupBy", next, ref i); break;

                    // Формат ведомостей есть в окне, но из командной строки был
                    // недоступен — а именно ей пользуются пакетная обработка и
                    // управление снаружи, которым книга Excel нужна не меньше.
                    case "--report-format":
                        if (next != null && !next.StartsWith("--"))
                        {
                            string rf = next.Trim().ToLowerInvariant();
                            if (rf == "csv") opts.OutProfile.ReportFormat = "Csv";
                            else if (rf == "xlsx") opts.OutProfile.ReportFormat = "Xlsx";
                            else if (rf == "both") opts.OutProfile.ReportFormat = "Both";
                            else Console.WriteLine("Неизвестный формат ведомостей: " + next +
                                                   ". Допустимо: csv, xlsx, both");
                            i++;
                        }
                        break;
                    case "--schedule":
                        if (next != null && !next.StartsWith("--")) { opts.ScheduleFile = next; i++; }
                        break;
                    default:
                        // раньше опечатка во флаге (--out вместо позиционного
                        // пути) молча игнорировалась и конвертация падала
                        // где-то глубже с невнятной ошибкой
                        if (a.StartsWith("--"))
                        {
                            Console.WriteLine("ПРЕДУПРЕЖДЕНИЕ: неизвестный ключ игнорируется: " + a);
                            break;
                        }
                        if (opts.Input == null) opts.Input = a;
                        else if (outPath == null) outPath = a;
                        else Console.WriteLine("ПРЕДУПРЕЖДЕНИЕ: лишний аргумент игнорируется: " + a);
                        break;
                }
            }

            if (string.IsNullOrEmpty(opts.Input) && cmd != "--watch")
            {
                Console.WriteLine("NWD2DWG v3.5 — Конвертер Navisworks → AutoCAD/glTF/IFC");
                Console.WriteLine("использование: NWD2DWG --convert <файл.nwd|nwc|nwf> <выход.dxf|dwg|gltf|glb|ifc> [опции]");
                Console.WriteLine("  --format dxf|3dface|dwg|gltf|glb|ifc");
                Console.WriteLine("  --visible 0|1  --skiphidden 0|1  --colors 0|1  --layers 0|1  --navis <папка>");
                Console.WriteLine("  --decimate <0-90>   Степень упрощения полигонов (%)");
                Console.WriteLine("  --soliddetect 1     Распознавание цилиндров/коробок");
                Console.WriteLine("  --xdata 1           Перенос BIM-свойств в XData");
                Console.WriteLine("  --materials 1       Перенос прозрачности/материалов");
                Console.WriteLine("  --sets \"Трубы,Стены\" Фильтр по Selection Sets");
                Console.WriteLine("  --geoshift 1        Сдвиг к нулю (0,0,0) + .wld");
                Console.WriteLine("  --grids 1           Оси и уровни (_GRIDS / _LEVELS)");
                Console.WriteLine("  --pipes 1           Оси труб и DN (PipeTracer)");
                Console.WriteLine("  --boq 1             Расчет объемов ВОР в Excel");
                Console.WriteLine("  --bcf 1             Коллизии BCF 2.1 zip");
                Console.WriteLine("  --clash-cluster 1   Кластеризация коллизий DBSCAN");
                Console.WriteLine("  --section-plan 1    2D поэтажный план (Z-срез)");
                Console.WriteLine("  --purge 1           Глубокая чистка DXF");
                Console.WriteLine("  --penetrations 1    Расстановка гильз (DN+50)");
                Console.WriteLine("  --clearance 1       Контроль высоты проходов (СП 118)");
                Console.WriteLine("  --steel 1           Сортамент стали ГОСТ (КМ/КМД)");
                Console.WriteLine("  --cog 1             Центр масс блока (CoG)");
                Console.WriteLine("  --iso 1             Изометрия трубопроводов ГОСТ 2.317");
                Console.WriteLine("  --4d 1              4D календарный график");
                Console.WriteLine("  --shrinkwrap 1      Защита IP (OBB-оболочки)");
                Console.WriteLine("  --room-finish 1     Ведомость отделки ГОСТ 21.501");
                return 2;
            }

            // === Watchdog Mode ===
            if (cmd == "--watch")
            {
                string watchDir = !string.IsNullOrEmpty(opts.WatchFolder) ? opts.WatchFolder
                                : !string.IsNullOrEmpty(opts.Input) ? opts.Input : ".";
                if (!Directory.Exists(watchDir))
                {
                    Console.WriteLine("Папка не найдена: " + watchDir);
                    return 1;
                }

                string watchLog = Path.Combine(watchDir, "NWD2DWG_watchdog.log");
                Log.SetFile(watchLog);
                Console.WriteLine("NWD2DWG Watchdog: мониторинг папки " + watchDir);
                Console.WriteLine("Лог: " + watchLog);
                Console.WriteLine("Нажмите Ctrl+C для выхода.");
                Log.Write("=== Watchdog запущен: " + watchDir + " ===");

                var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var watcher = new FileSystemWatcher(watchDir))
                {
                    watcher.Filter = "*.*";
                    watcher.IncludeSubdirectories = true;
                    watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;

                    watcher.Created += (s, e) =>
                    {
                        string ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
                        if (ext != ".nwd" && ext != ".nwc" && ext != ".nwf") return;
                        if (processed.Contains(e.FullPath)) return;

                        // Дебаунсинг: ждём стабилизации файла
                        Thread.Sleep(opts.WatchInterval * 1000);
                        try
                        {
                            if (!File.Exists(e.FullPath)) return;
                            using (var fs = File.Open(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                        }
                        catch { Thread.Sleep(3000); }

                        processed.Add(e.FullPath);
                        string outExt = opts.Format == OutFormat.Dwg ? ".dwg"
                                      : opts.Format == OutFormat.Gltf ? ".gltf"
                                      : opts.Format == OutFormat.Glb ? ".glb"
                                      : opts.Format == OutFormat.Ifc ? ".ifc"
                                      : ".dxf";
                        string outFile = Path.ChangeExtension(e.FullPath, outExt);
                        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] Конвертация: " + Path.GetFileName(e.FullPath));
                        Log.Write("Watchdog: обнаружен файл " + e.FullPath);

                        try
                        {
                            opts.Input = e.FullPath;
                            ConvertStats st = NavisConverter.ConvertFile(opts, e.FullPath, outFile,
                                s2 => { Console.WriteLine("  " + s2); }, d => { }, () => false);
                            Console.WriteLine("  Готово: " + outFile + " | " + st.Triangles + " треугольников");
                            Log.Write("Watchdog: завершено " + outFile);
                        }
                        catch (Exception ex2)
                        {
                            Console.WriteLine("  ОШИБКА: " + ex2.Message);
                            Log.Write("Watchdog ОШИБКА: " + ex2);
                        }
                    };

                    watcher.EnableRaisingEvents = true;

                    // Блокируем поток до Ctrl+C
                    var exitEvent = new ManualResetEvent(false);
                    Console.CancelKeyPress += (s, e) => { e.Cancel = true; exitEvent.Set(); };
                    exitEvent.WaitOne();
                }

                Log.Write("=== Watchdog остановлен ===");
                return 0;
            }

            // Путь без расширения — это папка, даже если её ещё нет. Раньше
            // несуществующий путь принимался за имя файла: выдача уходила в
            // файл без расширения, а ведомости и протокол — в родительский
            // каталог. Внешний вызов, который сам папку не создал, получал
            // именно это.
            // Папка на входе — это пакетная обработка, а не модель.
            //
            // Проверка стоит ДО приведения выходного пути: иначе папка выдачи
            // превращается в имя файла по имени входной папки, и вся выдача
            // ложится в каталог вроде out\in.dxf — именно так и вышло.
            // И до всякого обращения к Navisworks: иначе путь уходит туда как
            // имя модели и всплывает модальное окно, которое в автоматическом
            // прогоне некому закрыть.
            if (cmd == "--convert" && Directory.Exists(opts.Input))
                return ConvertFolder(opts, Path.GetFullPath(opts.Input), outPath);

            if (outPath != null && !Directory.Exists(outPath) && !File.Exists(outPath)
                && string.IsNullOrEmpty(Path.GetExtension(outPath)))
            {
                try
                {
                    Directory.CreateDirectory(outPath);
                    Console.WriteLine("Папка выдачи создана: " + outPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Не удалось создать папку выдачи: " + ex.Message);
                }
            }

            // путь вывода — это файл, а не папка: иначе плагин падал на
            // UnauthorizedAccessException при создании StreamWriter
            if (outPath != null && Directory.Exists(outPath))
            {
                outPath = Path.Combine(outPath,
                    Path.GetFileNameWithoutExtension(opts.Input ?? "output") + ExtFor(opts.Format));
                Console.WriteLine("Указана папка — файл будет создан как: " + outPath);
            }

            if (!File.Exists(opts.Input))
            {
                Console.WriteLine("Файл не найден: " + opts.Input);
                return 2;
            }

            string inExt = Path.GetExtension(opts.Input).ToLowerInvariant();
            if (inExt != ".nwd" && inExt != ".nwc" && inExt != ".nwf")
            {
                Console.WriteLine("Navisworks не открывает файлы «" + inExt +
                                  "». Нужны .nwd, .nwc или .nwf.");
                return 2;
            }

            if (outPath == null)
            {
                string ext = ".dxf";
                switch (opts.Format)
                {
                    case OutFormat.Dwg: ext = ".dwg"; break;
                    case OutFormat.Gltf: ext = ".gltf"; break;
                    case OutFormat.Glb: ext = ".glb"; break;
                    case OutFormat.Ifc: ext = ".ifc"; break;
                }
                outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opts.Input)) ?? ".",
                    Path.GetFileNameWithoutExtension(opts.Input) + ext);
            }

            string logFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".",
                Path.GetFileNameWithoutExtension(outPath) + "_NWD2DWG.log");
            Log.SetFile(logFile);

            if (cmd == "--probe")
            {
                Probe.Run(opts, outPath);
                return 0;
            }

            var cts = new CancellationTokenSource();
            ConvertStats st = NavisConverter.ConvertFile(opts, Path.GetFullPath(opts.Input), Path.GetFullPath(outPath),
                s => { Console.WriteLine(s); }, d => { }, () => false);
            Console.WriteLine("готово: " + outPath + " | треугольников: " + st.Triangles + " | " + st.OutputBytes + " байт");
            return 0;
        }

        /// <summary>
        /// Обработка папки целиком из командной строки.
        ///
        /// В окне режим «Папка целиком» был, а в командной строке путь к папке
        /// уходил в Navisworks как имя модели. Тот не находил модуль для
        /// «расширения» in и показывал модальное окно — в автоматическом
        /// прогоне это вечное зависание, потому что нажать «ОК» некому.
        ///
        /// Отказ на одном файле не прекращает обход: остальные модели папки
        /// должны быть обработаны, а список непрошедших выводится в конце.
        /// </summary>
        static int ConvertFolder(AppOptions opts, string inDir, string outDir)
        {
            var files = new List<string>();
            foreach (string ext in new[] { "*.nwd", "*.nwc", "*.nwf" })
            {
                try { files.AddRange(Directory.GetFiles(inDir, ext, SearchOption.AllDirectories)); }
                catch (Exception ex) { Console.WriteLine("не удалось прочитать папку: " + ex.Message); }
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);

            if (files.Count == 0)
            {
                Console.WriteLine("В папке нет моделей .nwd/.nwc/.nwf: " + inDir);
                return 2;
            }

            if (string.IsNullOrEmpty(outDir)) outDir = inDir;
            try { Directory.CreateDirectory(outDir); } catch { }

            Console.WriteLine("Папка: " + inDir);
            Console.WriteLine("Моделей к обработке: " + files.Count);

            string ext2 = ExtFor(opts.Format);
            int done = 0;
            var failed = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; i < files.Count; i++)
            {
                string src = files[i];
                string dst = Path.Combine(outDir,
                    Path.GetFileNameWithoutExtension(src) + ext2);

                Console.WriteLine();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "--- {0} из {1}: {2}", i + 1, files.Count, Path.GetFileName(src)));

                string logFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dst)) ?? ".",
                    Path.GetFileNameWithoutExtension(dst) + "_NWD2DWG.log");
                Log.SetFile(logFile);

                try
                {
                    opts.Input = src;
                    ConvertStats st = NavisConverter.ConvertFile(opts,
                        Path.GetFullPath(src), Path.GetFullPath(dst),
                        s => Console.WriteLine(s), d => { }, () => false);
                    Console.WriteLine("готово: " + Path.GetFileName(dst) +
                                      " | треугольников: " + st.Triangles);
                    done++;
                }
                catch (Exception ex)
                {
                    // Одна испорченная модель не должна останавливать всю папку.
                    Console.WriteLine("ОШИБКА на " + Path.GetFileName(src) + ": " + ex.Message);
                    failed.Add(Path.GetFileName(src) + " — " + ex.Message);
                }
            }

            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Папка обработана: успешно {0} из {1}, время {2:hh\\:mm\\:ss}",
                done, files.Count, sw.Elapsed));

            if (failed.Count > 0)
            {
                Console.WriteLine("Не обработаны:");
                foreach (string f in failed) Console.WriteLine("  " + f);
            }
            return failed.Count == 0 ? 0 : 1;
        }
    }

    // ------------------------------------------------------------------------
    // Режим --probe: быстрая проверка извлечения геометрии на машине клиента
    // ------------------------------------------------------------------------
    public static class Probe
    {
        public static void Run(AppOptions opts, string outFile)
        {
            // упрощённая версия конвертации: открываем файл, берём первый элемент с геометрией,
            // пишем координаты первых треугольников в текстовый файл
            var sb = new StringBuilder();
            sb.AppendLine("NWD2DWG probe: " + Path.GetFileName(opts.Input));
            sb.AppendLine("время: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            string nwDir = null;
            foreach (NwInstall ni in NwDetect.Find())
            {
                sb.AppendLine("Navisworks: " + ni);
                if (ni.HasAutomation && ni.HasApi && nwDir == null) nwDir = ni.Dir;
            }
            if (nwDir == null)
            {
                sb.AppendLine("РЕЗУЛЬТАТ: Navisworks не найден");
                File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
                return;
            }

            var loader = new NwLoader();
            loader.Load(nwDir);
            sb.AppendLine("Check: " + (loader.Check() ? "OK" : loader.LastError));
            if (!loader.Check())
            {
                File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
                return;
            }

            dynamic nw = null;
            Process manualRoamer = null;
            try
            {
                nw = NavisConverter.CreateNavisworksInstance(loader, nwDir, true, out manualRoamer);
                try { NavisConverter.InvokeOpenFile(nw, loader, opts.Input); } catch (Exception ex) { sb.AppendLine("OpenFile error: " + ex.Message); }
                dynamic doc = NavisConverter.ResolveDocument(nw, loader);
                if (doc == null) { sb.AppendLine("РЕЗУЛЬТАТ: Document == null"); File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8); return; }

                dynamic models = doc.Models;
                int modelCount = 0;
                foreach (dynamic model in models)
                {
                    modelCount++;
                    dynamic root = model.RootItem;
                    int items = 0;
                    if (root != null)
                    {
                        foreach (dynamic item in root.DescendantsAndSelf)
                        {
                            items++;
                            if (items > 20000) break;
                        }
                    }
                    sb.AppendLine("модель " + modelCount + ": элементов (до 20k) = " + items);
                }
                sb.AppendLine("РЕЗУЛЬТАТ: Проверка структуры завершена");
            }
            catch (Exception ex)
            {
                sb.AppendLine("ОШИБКА: " + ex);
            }
            finally
            {
                // Чужое окно, к которому мы подключились, закрывать нельзя:
                // человек мог оставить его открытым для своей работы.
                if (NavisConverter.OwnsInstance)
                {
                    try { if (nw != null) nw.Dispose(); } catch { }
                    try { if (manualRoamer != null && !manualRoamer.HasExited) manualRoamer.Kill(); } catch { }
                }
                else Log.Write("Navisworks оставлен открытым: подключались к чужому окну");
            }
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
            Log.Write(sb.ToString());
        }
    }

    // ------------------------------------------------------------------------
    // GUI (AutoCAD 2026 / MultiCAD Dark Theme Style & DWM Frame)
    // ------------------------------------------------------------------------
    public class DarkPanelGroup : Panel
    {
        public string Title { get; set; }
        public Color BorderColor = MainForm.ColBorder;
        public Color HeaderBg = MainForm.ColPanelHeader;
        public Color TitleColor = MainForm.ColText;

        public DarkPanelGroup()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Padding = new Padding(10, 28, 10, 6);
            BackColor = MainForm.ColPanel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Тело панели
            using (var b = new SolidBrush(BackColor))
            {
                g.FillRectangle(b, 0, 0, Width - 1, Height - 1);
            }

            // Полоса заголовка панели
            using (var b = new SolidBrush(HeaderBg))
            {
                g.FillRectangle(b, 0, 0, Width - 1, 24);
            }

            // Рамка и разделитель
            using (var p = new Pen(BorderColor, 1))
            {
                g.DrawLine(p, 0, 24, Width - 1, 24);
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }

            if (!string.IsNullOrEmpty(Title))
            {
                using (var font = new Font("Segoe UI", 9f, FontStyle.Bold))
                using (var b = new SolidBrush(TitleColor))
                {
                    g.DrawString(Title, font, b, 8, 3);
                }
            }
        }
    }

    public class MainForm : Form
    {
        TextBox _tbInput;
        TextBox _tbOutput;
        CheckBox _cbBatch;
        RadioButton _rbPolyface;
        RadioButton _rb3dFace;
        RadioButton _rbDwg;
        RadioButton _rbGltf;
        RadioButton _rbIfc;
        CheckBox _cbShowNw;
        CheckBox _cbShowAcad;
        CheckBox _cbSkipHidden;
        CheckBox _cbColors;
        CheckBox _cbLayers;
        CheckBox _cbSplit;
        // v2.0 контролы
        TrackBar _tbDecimate;
        System.Windows.Forms.Label _lbDecimateVal;
        CheckBox _cbSolidDetect;
        CheckBox _cbXData;
        CheckBox _cbMaterials;
        TextBox _tbSets;
        // v3.0 контролы
        CheckBox _cbGeoShift;
        CheckBox _cbGrids;
        CheckBox _cbPipeTrace;
        CheckBox _cbBoq;
        CheckBox _cbBcf;
        CheckBox _cbAnonymize;
        // v3.1 – v3.4 контролы
        CheckBox _cbClashCluster;
        CheckBox _cbSectionPlan;
        CheckBox _cbCadPurge;
        CheckBox _cbPenetrations;
        CheckBox _cbClearance;
        CheckBox _cbSteelMatcher;
        CheckBox _cbCog;
        CheckBox _cbIso;
        CheckBox _cbSchedule4D;
        CheckBox _cbShrinkwrap;
        CheckBox _cbRoomFinish;
        AdvancedConfig _advConfig = AdvancedConfig.Load();
        OutputProfile _outProfile = OutputProfile.Load();
        Button _btnCleanTemp;
        Button _btnConvert;
        Button _btnCancel;
        Button _btnDiag;
        Button _btnLogs;
        Button _btnAbout;
        ProgressBar _pb;
        System.Windows.Forms.Label _lbStatus;
        TextBox _tbLog;

        bool _running;
        volatile bool _cancel;
        readonly ToolTip _tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 200 };

        // Цветовая палитра из MultiCAD (CadPalette.xaml / CadStyles.xaml)
        public static readonly Color ColBg = Color.FromArgb(33, 37, 43);             // #21252B (CadCanvasBg)
        public static readonly Color ColHeader = Color.FromArgb(44, 48, 56);         // #2C3038 (CadTitleBarBg)
        public static readonly Color ColPanel = Color.FromArgb(42, 46, 53);          // #2A2E35 (CadInputBg / Group body)
        public static readonly Color ColPanelHeader = Color.FromArgb(55, 60, 68);    // #373C44 (CadRibbonBg / Header strip)
        public static readonly Color ColBorder = Color.FromArgb(76, 82, 92);         // #4C525C (CadPanelBorder)
        public static readonly Color ColBorderDark = Color.FromArgb(44, 50, 61);     // #2C323D (CadBorderDark)
        public static readonly Color ColSeparator = Color.FromArgb(71, 77, 87);      // #474D57 (CadPanelSeparator)
        public static readonly Color ColInput = Color.FromArgb(20, 22, 26);          // #14161A (Deep dark input / terminal)
        public static readonly Color ColAccent = Color.FromArgb(60, 143, 212);       // #3C8FD4 (CadAccentBlue)
        public static readonly Color ColCyan = Color.FromArgb(111, 212, 245);        // #6FD4F5 (IcoCyan)
        public static readonly Color ColBtnPrimary = Color.FromArgb(14, 94, 158);    // #0E5E9E (CadActiveBlue)
        public static readonly Color ColBtnPrimaryHover = Color.FromArgb(31, 132, 200); // #1F84C8
        public static readonly Color ColBtnSec = Color.FromArgb(44, 49, 60);         // #2C313C (Secondary button)
        public static readonly Color ColBtnSecHover = Color.FromArgb(62, 70, 83);    // #3E4653
        public static readonly Color ColChecked = Color.FromArgb(44, 110, 168);      // #2C6EA8 (CadCheckedBg)
        public static readonly Color ColHoverBg = Color.FromArgb(74, 81, 92);        // #4A515C (CadHoverBg)
        public static readonly Color ColText = Color.FromArgb(230, 232, 235);        // #E6E8EB (CadTextPrimary)
        public static readonly Color ColTextSecondary = Color.FromArgb(174, 180, 189); // #AEB4BD
        public static readonly Color ColTextMuted = Color.FromArgb(126, 133, 143);   // #7E858F (CadTextDim)
        public static readonly Color ColLogoRed = Color.FromArgb(224, 26, 34);       // #E01A22 (App Logo Red)

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        public static void ApplyDwmDarkTheme(Form form)
        {
            if (form == null || !form.IsHandleCreated) return;
            try
            {
                IntPtr handle = form.Handle;
                int dark = 1;
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref dark, sizeof(int));

                // 0x00BBGGRR (COLORREF)
                // ColHeader #2C3038 -> 0x0038302C
                int captionColor = 0x0038302C;
                DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

                // ColBorder #4C525C -> 0x005C524C
                int borderColor = 0x005C524C;
                DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

                // ColText #E6E8EB -> 0x00EBE8E6
                int textColor = 0x00EBE8E6;
                DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDwmDarkTheme(this);
        }

        public MainForm()
        {
            Text = "NWD2DWG 3.5 — конвертер моделей Navisworks";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Width = 1180; Height = 1100;
            MinimumSize = new Size(1080, 960);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = ColBg;
            ForeColor = ColText;
            Font = new Font("Segoe UI", 9f);
            AllowDrop = true;

            // 1. ШАПКА ОКНА (Dock = Top, Height = 44)
            var pHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = ColHeader,
                Padding = new Padding(12, 6, 14, 0)
            };

            // Кнопки быстрого доступа QAT
            var pQat = new FlowLayoutPanel
            {
                Location = new Point(12, 7),
                Size = new Size(94, 30),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };

            // В шапке — только работа с файлами. Диагностика, очистка, журнал
            // и «О программе» остались подписанными кнопками внизу окна:
            // дублировать их значками незачем, подпись понятнее значка.
            Button qatNew = CreateQatButton("＋", "Сбросить поля (Очистить)", (s, e) => { _tbInput.Text = ""; _tbOutput.Text = ""; });
            Button qatOpen = CreateQatButton("⎘", "Выбрать файл Navisworks", (s, e) => BrowseInput());
            Button qatFolder = CreateQatButton("⌂", "Выбрать папку сохранения", (s, e) => BrowseOutput());

            pQat.Controls.Add(qatNew);
            pQat.Controls.Add(qatOpen);
            pQat.Controls.Add(qatFolder);

            var lbTitle = new System.Windows.Forms.Label
            {
                Text = "NWD2DWG v3.5  |  BIM-конвертер Navisworks",
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                ForeColor = ColText,
                Location = new Point(118, 11),
                AutoSize = true
            };

            // Сведения о лицензии и ссылка на сайт перенесены в «О программе».
            // В шапке они занимали место каждый сеанс, а нужны один раз.
            pHeader.Controls.Add(pQat);
            pHeader.Controls.Add(lbTitle);

            // 2. НИЖНЯЯ ПАНЕЛЬ ДЕЙСТВИЙ (Dock = Bottom, Height = 56)
            var pBottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 56,
                MinimumSize = new Size(0, 56),
                BackColor = ColBg,
                Padding = new Padding(0, 4, 0, 6)
            };

            _pb = new ProgressBar
            {
                Dock = DockStyle.Top,
                Height = 6,
                Maximum = 1000,
                Margin = new Padding(0, 0, 0, 4)
            };

            _lbStatus = new System.Windows.Forms.Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                ForeColor = ColTextMuted,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9f)
            };

            var pButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 880,
                Height = 40,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };

            _btnConvert = StyleButton(new Button { Text = "▶  Конвертировать", Width = 210, Height = 36, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) }, true);
            _btnConvert.Click += (s, e) => StartConvert();

            _btnCancel = StyleButton(new Button { Text = "■  Отмена", Width = 85, Height = 36, Enabled = false }, false);
            _btnCancel.Click += (s, e) => { _cancel = true; _btnCancel.Enabled = false; _lbStatus.Text = "Отмена…"; };

            _btnDiag = StyleButton(new Button { Text = "⚙  Диагностика", Width = 130, Height = 36 }, false);
            _btnDiag.Click += (s, e) => RunDiag();

            _btnCleanTemp = StyleButton(new Button { Text = "⟲  Очистить Temp", Width = 150, Height = 36 }, false);
            _btnCleanTemp.Click += (s, e) => CleanTemp();

            _btnLogs = StyleButton(new Button { Text = "≡  Логи", Width = 85, Height = 36 }, false);
            _btnLogs.Click += (s, e) => OpenLogs();

            _btnAbout = StyleButton(new Button { Text = "ⓘ  О программе", Width = 135, Height = 36 }, false);
            _btnAbout.Click += (s, e) => ShowAbout();

            pButtons.Controls.Add(_btnConvert);
            pButtons.Controls.Add(_btnCancel);
            pButtons.Controls.Add(_btnDiag);
            pButtons.Controls.Add(_btnCleanTemp);
            pButtons.Controls.Add(_btnLogs);
            pButtons.Controls.Add(_btnAbout);

            var pActionRow = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
            pActionRow.Controls.Add(pButtons);
            pActionRow.Controls.Add(_lbStatus);
            _lbStatus.BringToFront();

            pBottom.Controls.Add(_pb);
            pBottom.Controls.Add(pActionRow);
            pActionRow.BringToFront();

            // 3. ОСНОВНАЯ ОБЛАСТЬ (Dock = Fill)
            var pMain = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ColBg,
                Padding = new Padding(14, 0, 14, 0)
            };

            var pRoot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = ColBg,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            pRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            pRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

            pHeader.Dock = DockStyle.Fill;
            pMain.Dock = DockStyle.Fill;
            pBottom.Dock = DockStyle.Fill;

            pRoot.Controls.Add(pHeader, 0, 0);
            pRoot.Controls.Add(pMain, 0, 1);
            pRoot.Controls.Add(pBottom, 0, 2);

            Controls.Add(pRoot);

            // 3.1: Исходный файл
            var gbIn = new DarkPanelGroup
            {
                Title = "◆  Исходный файл Navisworks (.nwd / .nwc / .nwf)",
                Height = 76,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 6)
            };

            var pInRow = new Panel { Dock = DockStyle.Fill };
            var pInActions = new Panel { Dock = DockStyle.Right, Width = 260, Height = 32 };
            var btnIn = StyleButton(new Button { Text = "Обзор…", Width = 88, Height = 28, Dock = DockStyle.Left }, false);
            btnIn.Click += (s, e) => BrowseInput();
            _cbBatch = StyleCheckBox(new CheckBox { Text = "Папка целиком", AutoSize = true, Dock = DockStyle.Right, Padding = new Padding(8, 4, 0, 0) });
            pInActions.Controls.Add(_cbBatch);
            pInActions.Controls.Add(btnIn);

            _tbInput = new TextBox();
            var pInBorder = CreateInputPanel(_tbInput);
            var pInText = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 0) };
            pInText.Controls.Add(pInBorder);

            pInRow.Controls.Add(pInText);
            pInRow.Controls.Add(pInActions);
            gbIn.Controls.Add(pInRow);

            // 3.2: Папка для сохранения
            var gbOut = new DarkPanelGroup
            {
                Title = "◆  Папка для сохранения (пусто = рядом с исходником)",
                Height = 76,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 6)
            };

            var pOutRow = new Panel { Dock = DockStyle.Fill };
            var btnOut = StyleButton(new Button { Text = "Обзор…", Width = 88, Height = 28, Dock = DockStyle.Right }, false);
            btnOut.Click += (s, e) => BrowseOutput();

            _tbOutput = new TextBox();
            var pOutBorder = CreateInputPanel(_tbOutput);
            var pOutText = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 0) };
            pOutText.Controls.Add(pOutBorder);

            pOutRow.Controls.Add(pOutText);
            pOutRow.Controls.Add(btnOut);
            gbOut.Controls.Add(pOutRow);

            // 3.3: Формат
            var gbFmt = new DarkPanelGroup
            {
                Title = "◆  Формат вывода геометрии и метаданных",
                Height = 98,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 6)
            };
            var pFmtGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2,
                Padding = new Padding(4, 2, 4, 0)
            };
            // Те же доли, что и у остальных панелей окна (36/32/32): иначе
            // третий столбец форматов не совпадал по левому краю с колонками
            // ниже, и это резало глаз.
            pFmtGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            pFmtGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            pFmtGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            pFmtGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            pFmtGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            _rbPolyface = StyleRadio(new RadioButton { Text = "DXF (Polyface Mesh)", Checked = true, Dock = DockStyle.Fill });
            _rb3dFace = StyleRadio(new RadioButton { Text = "DXF (3DFACE)", Dock = DockStyle.Fill });
            _rbDwg = StyleRadio(new RadioButton { Text = "DWG (через AutoCAD)", Dock = DockStyle.Fill });
            _rbGltf = StyleRadio(new RadioButton { Text = "glTF / GLB (Web / VR / 3D)", Dock = DockStyle.Fill });
            _rbIfc = StyleRadio(new RadioButton { Text = "IFC 2x3 (BIM-координация)", Dock = DockStyle.Fill });
            _rbDwg.CheckedChanged += (s, e) =>
            {
                if (_rbDwg.Checked)
                {
                    _cbShowAcad.ForeColor = ColText;
                    _cbShowAcad.AutoCheck = true;
                }
                else
                {
                    _cbShowAcad.ForeColor = ColTextMuted;
                    _cbShowAcad.AutoCheck = false;
                }
            };
            pFmtGrid.Controls.Add(_rbPolyface, 0, 0);
            pFmtGrid.Controls.Add(_rb3dFace, 1, 0);
            pFmtGrid.Controls.Add(_rbDwg, 2, 0);
            pFmtGrid.Controls.Add(_rbGltf, 0, 1);
            pFmtGrid.Controls.Add(_rbIfc, 1, 1);
            gbFmt.Controls.Add(pFmtGrid);

            // 3.4: Параметры
            var gbOpt = new DarkPanelGroup
            {
                Title = "◆  Базовые параметры конвертации",
                Height = 98,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 6)
            };

            var pOptGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2,
                Padding = new Padding(4, 2, 4, 0)
            };
            pOptGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            pOptGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            pOptGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            pOptGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            pOptGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            _cbSplit = StyleCheckBox(new CheckBox { Text = "Разбивать по разделам (XREF)", Checked = true, Dock = DockStyle.Fill });
            _cbSkipHidden = StyleCheckBox(new CheckBox { Text = "Пропускать скрытые", Checked = true, Dock = DockStyle.Fill });
            _cbShowNw = StyleCheckBox(new CheckBox { Text = "Показывать окно Navisworks", Checked = true, Dock = DockStyle.Fill });
            _cbColors = StyleCheckBox(new CheckBox { Text = "Переносить цвета элементов", Checked = false, Dock = DockStyle.Fill });
            _cbLayers = StyleCheckBox(new CheckBox { Text = "Отдельный слой на элемент", Checked = false, Dock = DockStyle.Fill });
            _cbShowAcad = StyleCheckBox(new CheckBox { Text = "Показывать окно AutoCAD", Checked = true, Dock = DockStyle.Fill });
            _cbShowAcad.ForeColor = ColTextMuted;
            _cbShowAcad.AutoCheck = false;

            pOptGrid.Controls.Add(_cbSplit, 0, 0);
            pOptGrid.Controls.Add(_cbSkipHidden, 1, 0);
            pOptGrid.Controls.Add(_cbShowNw, 2, 0);
            pOptGrid.Controls.Add(_cbColors, 0, 1);
            pOptGrid.Controls.Add(_cbLayers, 1, 1);
            pOptGrid.Controls.Add(_cbShowAcad, 2, 1);
            gbOpt.Controls.Add(pOptGrid);

            // 3.5: Расширенные параметры v2.0
            var gbAdv = new DarkPanelGroup
            {
                Title = "◆  Геометрическое ядро, Solid & LOD v2.0",
                Height = 152,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 6)
            };

            var pAdvGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 3,
                Padding = new Padding(4, 2, 4, 0)
            };
            pAdvGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            pAdvGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            pAdvGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            pAdvGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            pAdvGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            pAdvGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            // Ряд 1: Слайдер декимации
            var pDecRow = new Panel { Dock = DockStyle.Fill };
            var lbDec = new System.Windows.Forms.Label
            {
                Text = "Сжатие сетки (LOD):",
                ForeColor = ColText,
                Dock = DockStyle.Left,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 4, 10, 0)
            };
            _tbDecimate = new TrackBar
            {
                Minimum = 0,
                Maximum = 90,
                Value = 0,
                TickFrequency = 10,
                SmallChange = 5,
                LargeChange = 10,
                Dock = DockStyle.Fill,
                BackColor = ColPanel,
                Height = 30
            };
            _lbDecimateVal = new System.Windows.Forms.Label
            {
                Text = "0%",
                ForeColor = ColAccent,
                Dock = DockStyle.Right,
                Width = 55,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            _tbDecimate.ValueChanged += (s, e) => _lbDecimateVal.Text = _tbDecimate.Value + "%";
            pDecRow.Controls.Add(_tbDecimate);
            pDecRow.Controls.Add(lbDec);
            pDecRow.Controls.Add(_lbDecimateVal);
            pAdvGrid.Controls.Add(pDecRow, 0, 0);
            pAdvGrid.SetColumnSpan(pDecRow, 3);

            // Ряд 2: Чекбоксы
            _cbSolidDetect = StyleCheckBox(new CheckBox { Text = "Распознавание тел (Solid)", Checked = false, Dock = DockStyle.Fill });
            _cbXData = StyleCheckBox(new CheckBox { Text = "BIM-свойства (XData)", Checked = false, Dock = DockStyle.Fill });
            _cbMaterials = StyleCheckBox(new CheckBox { Text = "Прозрачность и PBR", Checked = false, Dock = DockStyle.Fill });
            pAdvGrid.Controls.Add(_cbSolidDetect, 0, 1);
            pAdvGrid.Controls.Add(_cbXData, 1, 1);
            pAdvGrid.Controls.Add(_cbMaterials, 2, 1);

            // Ряд 3: Selection Sets
            var pSetsRow = new Panel { Dock = DockStyle.Fill };
            var lbSets = new System.Windows.Forms.Label
            {
                Text = "Наборы (Sets):",
                ForeColor = ColText,
                Dock = DockStyle.Left,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 6, 8, 0)
            };
            _tbSets = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = ColInput,
                ForeColor = ColText,
                Font = new Font("Segoe UI", 9f),
                BorderStyle = BorderStyle.FixedSingle
            };
            _tbSets.GotFocus += (s, e) => { if (_tbSets.Text == "все (через запятую)") { _tbSets.Text = ""; _tbSets.ForeColor = ColText; } };
            _tbSets.LostFocus += (s, e) => { if (string.IsNullOrWhiteSpace(_tbSets.Text)) { _tbSets.Text = "все (через запятую)"; _tbSets.ForeColor = ColTextMuted; } };
            _tbSets.Text = "все (через запятую)";
            _tbSets.ForeColor = ColTextMuted;
            pSetsRow.Controls.Add(_tbSets);
            pSetsRow.Controls.Add(lbSets);
            pAdvGrid.Controls.Add(pSetsRow, 0, 2);
            pAdvGrid.SetColumnSpan(pSetsRow, 3);

            gbAdv.Controls.Add(pAdvGrid);

            // 3.6: Инженерия & BIM v3.0
            var gbV3 = new DarkPanelGroup
            {
                Title = "◆  Инженерия, Оси & Координация v3.0",
                Height = 98,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 6)
            };

            var pV3Grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2,
                Padding = new Padding(4, 2, 4, 0)
            };
            pV3Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            pV3Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            pV3Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            pV3Grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            pV3Grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            _cbGeoShift = StyleCheckBox(new CheckBox { Text = "Сдвиг к нулю (0,0,0) + .wld", Checked = true, Dock = DockStyle.Fill });
            _cbGrids = StyleCheckBox(new CheckBox { Text = "Уровни этажей (_LEVELS)", Checked = true, Dock = DockStyle.Fill });
            _tips.SetToolTip(_cbGrids, "Выгружаются высотные отметки этажей. Геометрию координационных осей публичный API Navisworks не отдаёт.");
            _cbPipeTrace = StyleCheckBox(new CheckBox { Text = "Оси труб (DN/L)", Checked = false, Dock = DockStyle.Fill });
            _cbBoq = StyleCheckBox(new CheckBox { Text = "Смета ВОР в Excel/CSV", Checked = false, Dock = DockStyle.Fill });
            _cbBcf = StyleCheckBox(new CheckBox { Text = "Коллизии BCF 2.1", Checked = false, Dock = DockStyle.Fill });
            _tips.SetToolTip(_cbBcf, "Выгружает сохранённые проверки Clash Detective в пакет BCF 2.1. Если проверок в модели нет, программа сообщает об этом в журнале.");
            _cbAnonymize = StyleCheckBox(new CheckBox { Text = "Анонимизация свойств", Checked = false, Dock = DockStyle.Fill });
            pV3Grid.Controls.Add(_cbGeoShift, 0, 0);
            pV3Grid.Controls.Add(_cbGrids, 1, 0);
            pV3Grid.Controls.Add(_cbPipeTrace, 2, 0);
            pV3Grid.Controls.Add(_cbBoq, 0, 1);
            pV3Grid.Controls.Add(_cbBcf, 1, 1);
            pV3Grid.Controls.Add(_cbAnonymize, 2, 1);
            gbV3.Controls.Add(pV3Grid);

            // 3.7: Экспертиза, EPC & 4D (v3.1 – v3.5)
            var gbV4 = new DarkPanelGroup
            {
                Title = "◆  Экспертиза, EPC & 4D (v3.1 – v3.5)",
                Height = 148,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 6)
            };

            var pV4Grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 4,
                Padding = new Padding(4, 2, 4, 0)
            };
            pV4Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            pV4Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            pV4Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            pV4Grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            pV4Grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            pV4Grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            pV4Grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

            _cbClashCluster = StyleCheckBox(new CheckBox { Text = "Кластеризация коллизий (DBSCAN)", Checked = false, Dock = DockStyle.Fill });
            _tips.SetToolTip(_cbClashCluster, "Группирует коллизии Clash Detective по трассам, отсекая дубли. Радиус ε задаётся в параметрах модулей.");
            _cbSectionPlan = StyleCheckBox(new CheckBox { Text = "2D поэтажный план (Z-срез)", Checked = false, Dock = DockStyle.Fill });
            _cbCadPurge = StyleCheckBox(new CheckBox { Text = "Глубокая чистка DXF (Purge)", Checked = false, Dock = DockStyle.Fill });
            _cbPenetrations = StyleCheckBox(new CheckBox { Text = "Авторасстановка гильз (DN+50)", Checked = false, Dock = DockStyle.Fill });
            _cbClearance = StyleCheckBox(new CheckBox { Text = "Контроль высоты проходов (СП 118)", Checked = false, Dock = DockStyle.Fill });
            _cbSteelMatcher = StyleCheckBox(new CheckBox { Text = "Сортамент стали ГОСТ (КМ/КМД)", Checked = false, Dock = DockStyle.Fill });
            _cbCog = StyleCheckBox(new CheckBox { Text = "Центр масс блока (CoG Гаусс)", Checked = false, Dock = DockStyle.Fill });
            _cbIso = StyleCheckBox(new CheckBox { Text = "Изометрия трубопроводов ГОСТ", Checked = false, Dock = DockStyle.Fill });
            _cbSchedule4D = StyleCheckBox(new CheckBox { Text = "4D статус по графику", Checked = false, Dock = DockStyle.Fill });
            _tips.SetToolTip(_cbSchedule4D, "Берёт задачи из TimeLiner модели (или из файла MS Project / CSV по ключу --schedule) и считает отставание на дату среза.");
            _cbShrinkwrap = StyleCheckBox(new CheckBox { Text = "Защита IP (OBB-оболочки)", Checked = false, Dock = DockStyle.Fill });
            _cbRoomFinish = StyleCheckBox(new CheckBox { Text = "Ведомость отделки ГОСТ 21.501", Checked = false, Dock = DockStyle.Fill });
            _tips.SetToolTip(_cbRoomFinish, "Помещения определяются по замкнутым контурам горизонтального среза. Пороги площади задаются в параметрах модулей.");
            pV4Grid.Controls.Add(_cbClashCluster, 0, 0);
            pV4Grid.Controls.Add(_cbSectionPlan, 1, 0);
            pV4Grid.Controls.Add(_cbCadPurge, 2, 0);
            pV4Grid.Controls.Add(_cbPenetrations, 0, 1);
            pV4Grid.Controls.Add(_cbClearance, 1, 1);
            pV4Grid.Controls.Add(_cbSteelMatcher, 2, 1);
            pV4Grid.Controls.Add(_cbCog, 0, 2);
            pV4Grid.Controls.Add(_cbIso, 1, 2);
            pV4Grid.Controls.Add(_cbSchedule4D, 2, 2);
            var btnModuleSettings = StyleButton(new Button { Text = "⚙  Параметры модулей…", Dock = DockStyle.Fill, Height = 26 }, false);
            btnModuleSettings.Click += (s, e) => OpenModuleSettings();

            pV4Grid.Controls.Add(_cbShrinkwrap, 0, 3);
            pV4Grid.Controls.Add(_cbRoomFinish, 1, 3);
            pV4Grid.Controls.Add(btnModuleSettings, 2, 3);
            gbV4.Controls.Add(pV4Grid);

            // 3.8: Лог
            _tbLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9f),
                BackColor = ColInput,
                ForeColor = Color.FromArgb(220, 227, 235),
                BorderStyle = BorderStyle.FixedSingle
            };

            pMain.Controls.Add(_tbLog);
            pMain.Controls.Add(gbV4);
            pMain.Controls.Add(gbV3);
            pMain.Controls.Add(gbAdv);
            pMain.Controls.Add(gbOpt);
            pMain.Controls.Add(gbFmt);
            pMain.Controls.Add(gbOut);
            pMain.Controls.Add(gbIn);

            _tbLog.BringToFront();

            Log.AddSink(AppendLog);
            Log.Write("NWD2DWG v3.5 — запущен. Разработчик: BaidurovLabs (https://baidurovlabs.ru)");
            Log.Write("Лицензия: GNU General Public License v3.0 (Свободное программное обеспечение)");
            Log.Write("Перетащите .nwd файл или папку в окно программы.");

            DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += (s, e) =>
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files == null || files.Length == 0) return;
                if (Directory.Exists(files[0]))
                {
                    _tbInput.Text = files[0];
                    _cbBatch.Checked = true;
                }
                else
                {
                    _tbInput.Text = files[0];
                }
            };
        }

        static Button CreateQatButton(string text, string toolTip, EventHandler onClick)
        {
            var btn = new Button
            {
                Text = text,
                Width = 28,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = ColTextSecondary,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 9.5f),
                Margin = new Padding(1, 0, 1, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = ColHoverBg;
            btn.FlatAppearance.MouseDownBackColor = ColChecked;
            if (onClick != null) btn.Click += onClick;
            var tt = new ToolTip();
            tt.SetToolTip(btn, toolTip);
            return btn;
        }

        void CleanTemp()
        {
            long freed = NavisConverter.TempCleaner.CleanTempFiles(0);
            MessageBox.Show(this, string.Format("Очищено {0:F1} МБ во временной папке (%TEMP%\\NWD2DWG).", freed / 1048576.0),
                "NWD2DWG TempCleaner", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static Form CreateLicenseForm()
        {
            var dlg = new Form
            {
                Text = "Лицензионное соглашение — GNU General Public License v3.0",
                Width = 840,
                Height = 620,
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = ColBg,
                ForeColor = ColText,
                Font = new Font("Segoe UI", 9f),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };
            dlg.HandleCreated += (s, e) => ApplyDwmDarkTheme(dlg);

            var pRoot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = ColBg,
                Padding = new Padding(16, 12, 16, 12)
            };
            pRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            pRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

            var pTop = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            var lbHead = new System.Windows.Forms.Label
            {
                Text = "NWD2DWG распространяется под лицензией GNU GPL v3",
                Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
                ForeColor = ColAccent,
                Location = new Point(0, 2),
                AutoSize = true
            };
            var lbSub = new System.Windows.Forms.Label
            {
                Text = "Свободное ПО с открытым исходным кодом. Автор: Baidurov Pavel",
                Location = new Point(0, 28),
                ForeColor = ColTextMuted,
                AutoSize = true
            };
            pTop.Controls.Add(lbHead);
            pTop.Controls.Add(lbSub);

            var pBorder = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ColBorder,
                Padding = new Padding(1),
                Margin = new Padding(0, 4, 0, 4)
            };
            var tbLic = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                BackColor = ColInput,
                ForeColor = Color.FromArgb(220, 227, 235),
                BorderStyle = BorderStyle.None,
                TabStop = false
            };

            string licPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LICENSE");
            if (File.Exists(licPath))
            {
                try { tbLic.Text = File.ReadAllText(licPath, Encoding.UTF8); } catch { }
            }
            if (string.IsNullOrEmpty(tbLic.Text))
            {
                tbLic.Text = "GNU GENERAL PUBLIC LICENSE\r\nVersion 3, 29 June 2007\r\n\r\n" +
                             "Copyright (C) 2026 Baidurov Pavel (https://github.com/AnT1pal/NWD2DWG)\r\n\r\n" +
                             "Everyone is permitted to copy and distribute verbatim copies\r\nof this license document, but changing it is not allowed.\r\n\r\n" +
                             "Preamble\r\n\r\n" +
                             "The GNU General Public License is a free, copyleft license for\r\nsoftware and other kinds of works.\r\n\r\n" +
                             "The licenses for most software and other practical works are designed\r\nto take away your freedom to share and change the works. By contrast,\r\nthe GNU General Public License is intended to guarantee your freedom to\r\nshare and change all versions of a program--to make sure it remains free\r\nsoftware for all its users.\r\n\r\n" +
                             "This program is distributed in the hope that it will be useful,\r\nbut WITHOUT ANY WARRANTY; without even the implied warranty of\r\nMERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the\r\nGNU General Public License for more details.\r\n\r\n" +
                             "You should have received a copy of the GNU General Public License\r\nalong with this program. If not, see <https://www.gnu.org/licenses/>.";
            }
            pBorder.Controls.Add(tbLic);

            var pBot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 6, 0, 0)
            };
            pBot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pBot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));

            var lnkGnu = new LinkLabel
            {
                Text = "Открыть текст GPLv3 на gnu.org",
                LinkColor = Color.FromArgb(88, 166, 255),
                ActiveLinkColor = Color.FromArgb(165, 214, 255),
                VisitedLinkColor = Color.FromArgb(88, 166, 255),
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 0, 0)
            };
            lnkGnu.LinkClicked += (s, e) => { try { Process.Start(new ProcessStartInfo("https://www.gnu.org/licenses/gpl-3.0.html") { UseShellExecute = true }); } catch { } };

            var btnOk = StyleButton(new Button { Text = "Понятно", Width = 120, Height = 34, DialogResult = DialogResult.OK, TabStop = true }, true);
            btnOk.Anchor = AnchorStyles.Right;

            pBot.Controls.Add(lnkGnu, 0, 0);
            pBot.Controls.Add(btnOk, 1, 0);

            pRoot.Controls.Add(pTop, 0, 0);
            pRoot.Controls.Add(pBorder, 0, 1);
            pRoot.Controls.Add(pBot, 0, 2);

            dlg.Controls.Add(pRoot);
            dlg.AcceptButton = btnOk;
            dlg.Shown += (s, e) => { btnOk.Focus(); tbLic.SelectionLength = 0; };
            return dlg;
        }

        void ShowLicenseDialog()
        {
            using (var dlg = CreateLicenseForm())
            {
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ShowDialog(this);
            }
        }

        /// <summary>Ссылка в стиле окна. url = null — обработчик вешается снаружи.</summary>
        static LinkLabel AboutLink(string text, string url)
        {
            var lnk = new LinkLabel
            {
                Text = text,
                LinkColor = Color.FromArgb(88, 166, 255),
                ActiveLinkColor = Color.FromArgb(165, 214, 255),
                VisitedLinkColor = Color.FromArgb(88, 166, 255),
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                Margin = new Padding(0, 6, 18, 0)
            };
            if (!string.IsNullOrEmpty(url))
                lnk.LinkClicked += (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch { }
                };
            return lnk;
        }

        public static Form CreateAboutForm()
        {
            var dlg = new Form
            {
                Text = "О программе NWD2DWG",
                Width = 720,
                Height = 400,
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = ColBg,
                ForeColor = ColText,
                Font = new Font("Segoe UI", 9f),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };
            dlg.HandleCreated += (s, e) => ApplyDwmDarkTheme(dlg);

            var pRoot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = ColBg,
                Padding = new Padding(16, 12, 16, 12)
            };
            pRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            pRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

            var pTop = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            var lbHead = new System.Windows.Forms.Label
            {
                Text = "NWD2DWG v3.5",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = ColAccent,
                Location = new Point(0, 2),
                AutoSize = true
            };
            var lbSub = new System.Windows.Forms.Label
            {
                Text = "BIM-конвертер и экосистема прямого извлечения геометрии Autodesk Navisworks",
                Location = new Point(0, 26),
                ForeColor = ColTextMuted,
                AutoSize = true
            };
            pTop.Controls.Add(lbHead);
            pTop.Controls.Add(lbSub);

            var pBorder = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ColBorder,
                Padding = new Padding(1),
                Margin = new Padding(0, 4, 0, 4)
            };
            var tbAbout = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.None,
                Font = new Font("Segoe UI", 9.5f),
                BackColor = ColInput,
                ForeColor = Color.FromArgb(220, 227, 235),
                BorderStyle = BorderStyle.None,
                TabStop = false
            };

            tbAbout.Text = "Программа предназначена для прямого извлечения и конвертации 3D-геометрии,\r\n" +
                           "координационных сеток, уровней и метаданных из файлов Navisworks (.NWD, .NWC, .NWF)\r\n" +
                           "в форматы AutoCAD (.DWG, .DXF), glTF 2.0 / GLB и IFC 2x3.\r\n\r\n" +
                           "Версия: 3.5 (Engineering, EPC, Coordination & 4D Edition)\r\n" +
                           "Лицензия: GNU General Public License v3.0 (GPLv3)\r\n" +
                           "Автор: Baidurov Pavel (baidurovlabs.ru)\r\n" +
                           "Совместимость: Autodesk Navisworks 2020–2026, AutoCAD 2018–2026";
            pBorder.Controls.Add(tbAbout);

            var pBot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 6, 0, 0)
            };
            pBot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pBot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

            // Три ссылки в одну строку. Текст лицензии обязан быть доступен из
            // интерфейса — GPL требует передавать его вместе с программой,
            // а других входов в него после разгрузки шапки не осталось.
            var pLinks = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 0)
            };

            var lnkSite = AboutLink("baidurovlabs.ru", "https://baidurovlabs.ru");
            // Исходники лежат в двух местах: зарубежном и российском. Второй
            // указан не для порядка — в закрытом контуре до GitHub может не
            // быть доступа, и тогда это единственный работающий адрес.
            var lnkGit = AboutLink("GitHub", "https://github.com/AnT1pal/NWD2DWG");
            var lnkSc = AboutLink("SourceCraft", "https://sourcecraft.dev/antipal/nwd2dwg");

            var lnkLic = AboutLink("Лицензия GNU GPL v3", null);
            lnkLic.LinkColor = ColTextMuted;
            lnkLic.VisitedLinkColor = ColTextMuted;
            lnkLic.LinkClicked += (s, e) =>
            {
                using (var lic = CreateLicenseForm())
                {
                    lic.StartPosition = FormStartPosition.CenterParent;
                    lic.ShowDialog(dlg);
                }
            };

            pLinks.Controls.Add(lnkSite);
            pLinks.Controls.Add(lnkGit);
            pLinks.Controls.Add(lnkSc);
            pLinks.Controls.Add(lnkLic);

            var btnClose = StyleButton(new Button { Text = "Закрыть", Width = 110, Height = 34, DialogResult = DialogResult.OK, TabStop = true }, true);
            btnClose.Anchor = AnchorStyles.Right;

            pBot.Controls.Add(pLinks, 0, 0);
            pBot.Controls.Add(btnClose, 1, 0);

            pRoot.Controls.Add(pTop, 0, 0);
            pRoot.Controls.Add(pBorder, 0, 1);
            pRoot.Controls.Add(pBot, 0, 2);

            dlg.Controls.Add(pRoot);
            dlg.AcceptButton = btnClose;
            dlg.Shown += (s, e) => { btnClose.Focus(); tbAbout.SelectionLength = 0; };
            return dlg;
        }

        void ShowAbout()
        {
            using (var dlg = CreateAboutForm())
            {
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ShowDialog(this);
            }
        }

        void OpenModuleSettings()
        {
            using (var dlg = new ModuleSettingsDialog(_advConfig, _outProfile))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _advConfig.Save();
                    _outProfile.Save();
                    Log.Write("Параметры модулей и профиль выдачи сохранены.");
                }
            }
        }

        static Panel CreateInputPanel(TextBox tb)
        {
            var pBorder = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ColBorder,
                Padding = new Padding(1),
                Height = 30
            };
            var pInner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ColInput,
                Padding = new Padding(8, 4, 8, 4)
            };
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = ColInput;
            tb.ForeColor = ColText;
            tb.Font = new Font("Segoe UI", 9.5f);
            tb.Dock = DockStyle.Fill;
            pInner.Controls.Add(tb);
            pBorder.Controls.Add(pInner);
            return pBorder;
        }

        static Button StyleButton(Button btn, bool primary)
        {
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = primary ? ColAccent : ColBorder;
            btn.FlatAppearance.MouseOverBackColor = primary ? ColBtnPrimaryHover : ColBtnSecHover;
            btn.FlatAppearance.MouseDownBackColor = primary ? ColChecked : ColHoverBg;
            btn.BackColor = primary ? ColBtnPrimary : ColBtnSec;
            btn.ForeColor = Color.White;
            btn.Cursor = Cursors.Hand;
            btn.Font = new Font("Segoe UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular);
            btn.Margin = new Padding(3, 2, 3, 2);
            return btn;
        }

        static CheckBox StyleCheckBox(CheckBox cb)
        {
            cb.ForeColor = ColText;
            cb.FlatStyle = FlatStyle.Standard;
            cb.Cursor = Cursors.Hand;
            return cb;
        }

        static RadioButton StyleRadio(RadioButton rb)
        {
            rb.ForeColor = ColText;
            rb.FlatStyle = FlatStyle.Standard;
            rb.Cursor = Cursors.Hand;
            return rb;
        }

        void AppendLog(string line)
        {
            try
            {
                if (IsDisposed || _tbLog == null || _tbLog.IsDisposed) return;
                _tbLog.AppendText(line + Environment.NewLine);
                if (_tbLog.Lines.Length > 2000)
                {
                    var lines = _tbLog.Lines;
                    _tbLog.Lines = lines.Skip(lines.Length - 1500).ToArray();
                }
            }
            catch { }
        }

        void BrowseInput()
        {
            if (_cbBatch.Checked)
            {
                using (var dlg = new FolderBrowserDialog { Description = "Выберите папку с файлами Navisworks" })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) _tbInput.Text = dlg.SelectedPath;
                }
            }
            else
            {
                using (var dlg = new System.Windows.Forms.OpenFileDialog
                {
                    Filter = "Файлы Navisworks (*.nwd;*.nwc;*.nwf)|*.nwd;*.nwc;*.nwf|Все файлы (*.*)|*.*",
                    Title = "Выберите файл Navisworks"
                })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) _tbInput.Text = dlg.FileName;
                }
            }
        }

        void BrowseOutput()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Папка для сохранения результата" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) _tbOutput.Text = dlg.SelectedPath;
            }
        }

        void RunDiag()
        {
            string logFile = Path.Combine(Path.GetTempPath(), "NWD2DWG", "diagnostics_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            Log.SetFile(logFile);
            try
            {
                _tbLog.Clear();
                Log.Write("=== Диагностика (кнопка) ===");
                string report = Diagnostics.Run(true);
                Log.Write("отчёт сохранён: " + logFile);
                Log.Flush();
                MessageBox.Show(this, report, "Диагностика NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log.Write("ошибка диагностики: " + ex);
                MessageBox.Show(this, "Ошибка: " + ex.Message, "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void OpenLogs()
        {
            string dir = Path.Combine(Path.GetTempPath(), "NWD2DWG");
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch { }
        }

        // Маршалинг в UI-поток. Конвертация больше не выполняется в потоке
        // сообщений, поэтому любое обращение к контролам идёт только через них.
        void UiPost(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        void UiSend(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) Invoke(a); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running)
            {
                var r = MessageBox.Show(this,
                    "Конвертация ещё выполняется. Прервать и закрыть программу?",
                    "NWD2DWG", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                _cancel = true;
            }
            base.OnFormClosing(e);
        }

        void StartConvert()
        {
            if (_running) return;
            var opts = CollectOptions();
            List<string> files = GatherFiles(opts, out string error);
            if (files == null)
            {
                MessageBox.Show(this, error, "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _running = true;
            _cancel = false;
            _btnConvert.Enabled = false;
            _btnCancel.Enabled = true;
            _btnDiag.Enabled = false;
            _pb.Value = 0;

            string logFile = Path.Combine(Path.GetTempPath(), "NWD2DWG",
                "convert_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".log");
            Log.SetFile(logFile);
            Log.Write("=== Конвертация начата ===");
            Log.Write("файлов к обработке: " + files.Count);

            // Отдельный STA-поток: раньше конвертация шла прямо в потоке UI, окно
            // висело в "Не отвечает", а Отмена работала только благодаря DoEvents
            // внутри колбэка прогресса. STA обязателен для COM-автоматизации
            // Navisworks и AutoCAD, поэтому не Task.Run (там пул с MTA).
            var worker = new Thread(() => ConvertWorker(opts, files, logFile));
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Name = "NWD2DWG.Convert";
            worker.Start();
        }

        void ConvertWorker(AppOptions opts, List<string> files, string logFile)
        {
            try
            {
                int done = 0;
                foreach (string file in files)
                {
                    if (_cancel) { Log.Write("отменено пользователем"); break; }
                    string outDir = string.IsNullOrEmpty(opts.OutputDir)
                        ? Path.GetDirectoryName(Path.GetFullPath(file))
                        : Path.GetFullPath(opts.OutputDir);
                    try { Directory.CreateDirectory(outDir); } catch { }
                    string ext = ".dxf";
                    switch (opts.Format)
                    {
                        case OutFormat.Dwg: ext = ".dwg"; break;
                        case OutFormat.Gltf: ext = ".gltf"; break;
                        case OutFormat.Glb: ext = ".glb"; break;
                        case OutFormat.Ifc: ext = ".ifc"; break;
                    }
                    string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ext);

                    Log.Write("--- файл " + (done + 1) + "/" + files.Count + ": " + file + " -> " + outPath);
                    UiPost(() => _lbStatus.Text = "Открытие Navisworks…");

                    opts.Input = file;
                    try
                    {
                        ConvertStats st = NavisConverter.ConvertFile(opts, file, outPath,
                            s => UiPost(() => _lbStatus.Text = s),
                            d => UiPost(() => _pb.Value = Math.Max(0, Math.Min(1000, (int)(d * 1000)))),
                            () => _cancel);
                        Log.Write(string.Format("итог: треугольников {0}, элементов {1}, {2:F1} МБ, {3}",
                            st.Triangles, st.Items, st.OutputBytes / 1048576.0, st.Elapsed));
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Write("отменено пользователем (файл: " + Path.GetFileName(file) + ")");
                        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("ОШИБКА файла " + file + ": " + ex);
                        try { if (File.Exists(outPath) && new FileInfo(outPath).Length < 100) File.Delete(outPath); } catch { }
                        string msg = "Не удалось конвертировать:\n" + Path.GetFileName(file) + "\n\n" + ex.Message +
                                     "\n\nПодробности: " + logFile;
                        UiSend(() => MessageBox.Show(this, msg, "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Error));
                    }
                    done++;
                }

                bool cancelled = _cancel;
                UiPost(() => _lbStatus.Text = cancelled ? "Отменено." : "Готово.");
                Log.Write("=== Конвертация завершена ===");
                Log.Flush();
            }
            catch (Exception ex)
            {
                Log.Write("КРИТИЧЕСКАЯ ОШИБКА конвейера: " + ex);
                Log.Flush();
            }
            finally
            {
                UiPost(() =>
                {
                    _running = false;
                    _btnConvert.Enabled = true;
                    _btnCancel.Enabled = false;
                    _btnDiag.Enabled = true;
                });
            }
        }

        AppOptions CollectOptions()
        {
            string sets = _tbSets.Text.Trim();
            if (sets == "все (через запятую)") sets = "";

            return new AppOptions
            {
                Batch = _cbBatch.Checked,
                Format = _rb3dFace.Checked ? OutFormat.Dxf3dFace
                       : _rbDwg.Checked ? OutFormat.Dwg
                       : _rbGltf.Checked ? OutFormat.Gltf
                       : _rbIfc.Checked ? OutFormat.Ifc
                       : OutFormat.DxfPolyface,
                ShowNavisworks = _cbShowNw.Checked,
                ShowAutoCad = _cbShowAcad.Checked,
                SkipHidden = _cbSkipHidden.Checked,
                WithColors = _cbColors.Checked,
                LayersPerItem = _cbLayers.Checked,
                SplitDisciplines = _cbSplit.Checked,
                OutputDir = _tbOutput.Text.Trim(),
                DecimatePercent = _tbDecimate.Value,
                SolidDetect = _cbSolidDetect.Checked,
                TransferXData = _cbXData.Checked,
                TransferMaterials = _cbMaterials.Checked,
                SelectionSets = sets,
                GeoShift = _cbGeoShift.Checked,
                ExportGrids = _cbGrids.Checked,
                TracePipes = _cbPipeTrace.Checked,
                ExportBoq = _cbBoq.Checked,
                ExportBcf = _cbBcf.Checked,
                Anonymize = _cbAnonymize.Checked,
                ClusterClashes = _cbClashCluster.Checked,
                SectionPlan = _cbSectionPlan.Checked,
                PurgeDxf = _cbCadPurge.Checked,
                BuildPenetrations = _cbPenetrations.Checked,
                ValidateClearance = _cbClearance.Checked,
                MatchSteel = _cbSteelMatcher.Checked,
                CalcCog = _cbCog.Checked,
                GenerateIso = _cbIso.Checked,
                MapSchedule4D = _cbSchedule4D.Checked,
                Shrinkwrap = _cbShrinkwrap.Checked,
                RoomFinish = _cbRoomFinish.Checked,
                AdvConfig = _advConfig,
                OutProfile = _outProfile
            };
        }

        List<string> GatherFiles(AppOptions opts, out string error)
        {
            error = null;
            string input = _tbInput.Text.Trim().Trim('"');
            if (string.IsNullOrEmpty(input))
            {
                error = "Укажите файл или папку Navisworks.";
                return null;
            }
            if (opts.Batch || Directory.Exists(input))
            {
                opts.Batch = true;
                if (!Directory.Exists(input)) { error = "Папка не найдена: " + input; return null; }
                var files = new List<string>();
                foreach (string ext in new[] { "*.nwd", "*.nwc", "*.nwf" })
                {
                    try { files.AddRange(Directory.GetFiles(input, ext, SearchOption.AllDirectories)); }
                    catch (Exception ex) { Log.Write("ошибка сканирования " + ext + ": " + ex.Message); }
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                if (files.Count == 0) { error = "В папке не найдено файлов .nwd/.nwc/.nwf."; return null; }
                return files;
            }
            if (!File.Exists(input)) { error = "Файл не найден: " + input; return null; }
            string e2 = Path.GetExtension(input).ToLowerInvariant();
            if (e2 != ".nwd" && e2 != ".nwc" && e2 != ".nwf")
            {
                error = "Неподдерживаемое расширение: " + e2 + " (нужны .nwd/.nwc/.nwf)";
                return null;
            }
            return new List<string> { input };
        }
    }
}
