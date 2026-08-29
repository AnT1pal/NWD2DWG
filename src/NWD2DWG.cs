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

        public void Reset(double[] m)
        {
            Array.Copy(m, Matrix, 16);
            Verts.Clear();
            Quads.Clear();
            _index.Clear();
            TriCount = 0;
            SkippedDegenerate = 0;
            VertexReadErrors = 0;
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
            ulong k = Key(x, y, z);
            int idx;
            if (_index.TryGetValue(k, out idx)) return idx;
            idx = Verts.Count / 3;
            _index[k] = idx;
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

        const int MaxVerts = 30000;
        const int MaxFaces = 60000;

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
                int vcount = 0;
                while (f < quads.Count)
                {
                    if (faces.Count >= MaxFaces) break;
                    int before = used.Count;
                    int a = quads[f], b = quads[f + 1], c = quads[f + 2], d = quads[f + 3];
                    foreach (int vi in new[] { a, b, c, d })
                    {
                        if (!used.ContainsKey(vi)) { used[vi] = vcount + used.Count; }
                    }
                    int newCount = used.Count;
                    if (newCount > MaxVerts) { foreach (int vi in new[] { a, b, c, d }) used.Remove(vi); break; }
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
                    p.WaitForExit(300000); // 5 min timeout
                }

                try { File.Delete(scrPath); } catch { }
                if (File.Exists(dwgPath) && new FileInfo(dwgPath).Length > 0) return;
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
                File.WriteAllText(scrPath, scrContent, Encoding.ASCII);

                dynamic doc = acad.Documents.Add();
                doc.SendCommand(string.Format(CultureInfo.InvariantCulture, "_.SCRIPT \"{0}\"\r\n", scrPath.Replace('\\', '/')));
                Thread.Sleep(3000);
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
                            const int maxV = 30000, maxF = 60000;
                            int f0 = 0;
                            while (f0 < quads.Count)
                            {
                                var used = new Dictionary<int, int>();
                                var rev = new List<int>();
                                var faces = new List<int>();
                                int f = f0;
                                while (f < quads.Count)
                                {
                                    if (faces.Count >= maxF) break;
                                    int a = quads[f], b = quads[f + 1], c = quads[f + 2], d = quads[f + 3];
                                    foreach (int vi in new[] { a, b, c, d })
                                    {
                                        if (!used.ContainsKey(vi)) used[vi] = used.Count;
                                    }
                                    if (used.Count > maxV) { foreach (int vi in new[] { a, b, c, d }) used.Remove(vi); break; }
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

            string targetDxf = outPath;
            bool isDwg = opts.Format == OutFormat.Dwg;
            if (isDwg)
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "NWD2DWG");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                targetDxf = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(outPath) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dxf");
            }

            string convLog = Path.Combine(Path.GetTempPath(), "NWD2DWG", "conv_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

            try
            {
                // ---- открыть Navisworks ----
                Log.Write("запуск Navisworks (папка: " + nwDir + ")");
                nw = CreateNavisworksInstance(loader, nwDir, opts.ShowNavisworks, out manualRoamer);

                // Загружаем плагин в процесс Navisworks
                Log.Write("загрузка плагина в Navisworks...");
                MethodInfo mAdd = loader.AutomationType.GetMethod("AddPluginAssembly");
                if (mAdd == null) throw new Exception("AddPluginAssembly не найден в NavisworksApplication");
                mAdd.Invoke(nw, new object[] { pluginDll });

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
                    opts.Anonymize ? "1" : "0"                                     // [20]
                };

                if (status != null) status("Извлечение геометрии и полигонов...");
                if (progress != null) progress(0.2);

                // Вызов плагина в том же потоке (STA)
                object ret = mExec.Invoke(nw, new object[] { "NWD2DWG_Converter.NWD2DWG", pluginArgs });
                int exitCode = ret != null ? Convert.ToInt32(ret) : 0;

                if (exitCode != 0)
                {
                    string errDetail = "";
                    if (File.Exists(convLog)) { try { errDetail = File.ReadAllText(convLog); } catch { } }
                    throw new Exception("Плагин конвертации завершился с кодом " + exitCode + ": " + errDetail);
                }

                if (!opts.SplitDisciplines && !File.Exists(targetDxf))
                    throw new Exception("Файл геометрии не был создан плагином.");

                // Если формат DWG — конвертируем через AutoCAD
                if (isDwg)
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
                try { if (nw != null) nw.Dispose(); } catch { }
                try { if (manualRoamer != null && !manualRoamer.HasExited) manualRoamer.Kill(); } catch { }
                try { TempCleaner.CleanTempFiles(0); } catch { }
                Log.Flush();
            }
        }

        // --------------------------------------------------------------------
        // Автоматическая очистка временных файлов (%TEMP%\NWD2DWG)
        // --------------------------------------------------------------------
        public static class TempCleaner
        {
            public static long CleanTempFiles(int maxAgeHours = 1)
            {
                long freedBytes = 0;
                string tempDir = Path.Combine(Path.GetTempPath(), "NWD2DWG");
                if (!Directory.Exists(tempDir)) return 0;

                try
                {
                    var dir = new DirectoryInfo(tempDir);
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
        public static dynamic CreateNavisworksInstance(NwLoader loader, string nwDir, bool visible, out Process manualRoamer)
        {
            manualRoamer = null;

            // 1) Классический путь: Activator.CreateInstance
            Log.Write("попытка 1: Activator.CreateInstance (классический запуск)");
            dynamic nw = Activator.CreateInstance(loader.AutomationType);
            try { nw.Visible = visible; } catch (Exception ex) { Log.Write("предупреждение: Visible=" + ex.Message); }

            // Даём Navisworks время на старт (в старых версиях конструктор сам запускает Roamer.exe)
            Thread.Sleep(3000);

            // Проверяем, запустился ли Roamer.exe
            bool roamerRunning = false;
            try
            {
                foreach (var p in Process.GetProcessesByName("Roamer"))
                { roamerRunning = true; p.Dispose(); break; }
            }
            catch { }

            if (roamerRunning)
            {
                Log.Write("Roamer.exe запущен автоматически (режим 2017-2024)");
                return nw;
            }

            // 2) Navisworks 2025+/2026: Roamer.exe не запустился — запускаем вручную
            Log.Write("Roamer.exe НЕ запущен — режим 2025+/2026: ручной запуск");
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
            Thread.Sleep(3000); // Даем Roamer.exe полностью завершить инициализацию
            return result;
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
            void W(string s) { report.AppendLine(s); Console.WriteLine(s); Log.Write(s); }

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

                bool allOk = polylines == 1 && seqends == 1 && vverts == 8 && fverts == 12 && f3 == 12
                             && decTris < 12 && solidRes.Type == Plugin.SolidType.Box
                             && gltfOk && glbOk && ifcOk && ifcValid
                             && geoOk && gridOk && pipeOk && boqOk && bcfOk && diffOk && tileOk && anonOk;
                W("САМОТЕСТ ПРОЙДЕН: " + (allOk ? "OK (все 20 модулей v3.0 исправны)" : "ОШИБКИ"));
            }
            catch (Exception ex)
            {
                W("САМОТЕСТ УПАЛ: " + ex);
            }

            try { File.WriteAllText(Path.Combine(dir, "selftest_report.txt"), report.ToString(), Encoding.UTF8); } catch { }
            return report.ToString().Contains("ОШИБК") ? 1 : 0;
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
                        opts.NavisworksDir = next; if (next != null && !next.StartsWith("--")) i++;
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
                    default:
                        if (a.StartsWith("--")) break;
                        if (opts.Input == null) opts.Input = a;
                        else if (outPath == null) outPath = a;
                        break;
                }
            }

            if (string.IsNullOrEmpty(opts.Input) && cmd != "--watch")
            {
                Console.WriteLine("NWD2DWG v2.0 — Конвертер Navisworks → AutoCAD/glTF/IFC");
                Console.WriteLine("использование: NWD2DWG --convert <файл.nwd|nwc|nwf> <выход.dxf|dwg|gltf|glb|ifc> [опции]");
                Console.WriteLine("  --format dxf|3dface|dwg|gltf|glb|ifc");
                Console.WriteLine("  --visible 0|1  --skiphidden 0|1  --colors 0|1  --layers 0|1  --navis <папка>");
                Console.WriteLine("  --decimate <0-90>   Степень упрощения полигонов (%)");
                Console.WriteLine("  --soliddetect 1     Распознавание цилиндров/коробок");
                Console.WriteLine("  --xdata 1           Перенос BIM-свойств в XData");
                Console.WriteLine("  --materials 1       Перенос прозрачности/материалов");
                Console.WriteLine("  --sets \"Трубы,Стены\" Фильтр по Selection Sets");
                Console.WriteLine("  --bbox minX,minY,minZ,maxX,maxY,maxZ  Section Box обрезка");
                Console.WriteLine("  --threads <N>       Кол-во потоков (0=авто)");
                Console.WriteLine("  --watch <папка>     Фоновый мониторинг папки");
                Console.WriteLine("  --interval <сек>    Интервал мониторинга (по умолчанию 5)");
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
                try { if (nw != null) nw.Dispose(); } catch { }
                try { if (manualRoamer != null && !manualRoamer.HasExited) manualRoamer.Kill(); } catch { }
            }
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
            Log.Write(sb.ToString());
        }
    }

    // ------------------------------------------------------------------------
    // GUI (AutoCAD 2026 Dark Theme Style)
    // ------------------------------------------------------------------------
    public class DarkPanelGroup : Panel
    {
        public string Title { get; set; }
        public Color BorderColor = Color.FromArgb(56, 66, 82);
        public Color HeaderColor = Color.FromArgb(0, 162, 255);

        public DarkPanelGroup()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Padding = new Padding(12, 22, 12, 8);
            BackColor = Color.FromArgb(28, 33, 40);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var rect = new Rectangle(0, 8, Width - 1, Height - 9);
            using (var p = new Pen(BorderColor, 1))
            {
                g.DrawRectangle(p, rect);
            }

            if (!string.IsNullOrEmpty(Title))
            {
                using (var font = new Font("Segoe UI", 9f, FontStyle.Bold))
                {
                    SizeF sz = g.MeasureString(Title, font);
                    var textRect = new Rectangle(12, 0, (int)sz.Width + 8, (int)sz.Height);
                    using (var b = new SolidBrush(BackColor))
                    {
                        g.FillRectangle(b, textRect);
                    }
                    using (var b = new SolidBrush(HeaderColor))
                    {
                        g.DrawString(Title, font, b, 16, 0);
                    }
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

        static readonly Color ColBg = Color.FromArgb(28, 33, 40);
        static readonly Color ColPanel = Color.FromArgb(34, 40, 49);
        static readonly Color ColBorder = Color.FromArgb(56, 66, 82);
        static readonly Color ColText = Color.FromArgb(230, 237, 243);
        static readonly Color ColTextMuted = Color.FromArgb(139, 148, 158);
        static readonly Color ColAccent = Color.FromArgb(0, 162, 255);
        static readonly Color ColInput = Color.FromArgb(18, 22, 28);
        static readonly Color ColBtnPrimary = Color.FromArgb(0, 122, 204);
        static readonly Color ColBtnSec = Color.FromArgb(45, 52, 64);

        public MainForm()
        {
            Text = "NWD2DWG v3.0 — BaidurovLabs (GNU GPL v3)";
            Width = 1180; Height = 1040;
            MinimumSize = new Size(1080, 940);
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
                BackColor = ColBg,
                Padding = new Padding(14, 8, 14, 0)
            };

            var pBrand = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = 34,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0)
            };
            var lnkSite = new LinkLabel
            {
                Text = "baidurovlabs.ru",
                LinkColor = Color.FromArgb(88, 166, 255),
                ActiveLinkColor = Color.FromArgb(165, 214, 255),
                VisitedLinkColor = Color.FromArgb(88, 166, 255),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(6, 3, 0, 0)
            };
            lnkSite.LinkClicked += (s, e) => { try { Process.Start(new ProcessStartInfo("https://baidurovlabs.ru") { UseShellExecute = true }); } catch { } };

            var lnkLicTop = new LinkLabel
            {
                Text = "GNU GPL v3 (Свободное ПО)  |",
                LinkColor = ColTextMuted,
                ActiveLinkColor = ColAccent,
                VisitedLinkColor = ColTextMuted,
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 0)
            };
            lnkLicTop.LinkClicked += (s, e) => ShowLicenseDialog();

            pBrand.Controls.Add(lnkSite);
            pBrand.Controls.Add(lnkLicTop);

            var lbTitle = new System.Windows.Forms.Label
            {
                Text = "NWD2DWG v3.0  |  BIM-конвертер Navisworks",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = ColAccent,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };

            pHeader.Controls.Add(lbTitle);
            pHeader.Controls.Add(pBrand);

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

            _btnCancel = StyleButton(new Button { Text = "Отмена", Width = 85, Height = 36, Enabled = false }, false);
            _btnCancel.Click += (s, e) => { _cancel = true; _btnCancel.Enabled = false; _lbStatus.Text = "Отмена…"; };

            _btnDiag = StyleButton(new Button { Text = "Диагностика", Width = 125, Height = 36 }, false);
            _btnDiag.Click += (s, e) => RunDiag();

            _btnCleanTemp = StyleButton(new Button { Text = "Очистить Temp", Width = 150, Height = 36 }, false);
            _btnCleanTemp.Click += (s, e) =>
            {
                long freed = NavisConverter.TempCleaner.CleanTempFiles(0);
                MessageBox.Show(this, string.Format("Очищено {0:F1} МБ во временной папке (%TEMP%\\NWD2DWG).", freed / 1048576.0),
                    "NWD2DWG TempCleaner", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            _btnLogs = StyleButton(new Button { Text = "Логи", Width = 75, Height = 36 }, false);
            _btnLogs.Click += (s, e) => OpenLogs();

            _btnAbout = StyleButton(new Button { Text = "О программе", Width = 145, Height = 36 }, false);
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
                Title = " Исходный файл Navisworks (.nwd / .nwc / .nwf) ",
                Height = 74,
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
                Title = " Папка для сохранения (пусто = рядом с исходником) ",
                Height = 74,
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
                Title = " Формат вывода ",
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
            pFmtGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            pFmtGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            pFmtGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
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
                    _cbShowAcad.ForeColor = Color.FromArgb(110, 118, 129);
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
                Title = " Параметры конвертации ",
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
            _cbShowAcad.ForeColor = Color.FromArgb(110, 118, 129);
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
                Title = " Расширенные параметры v2.0 ",
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
                BackColor = ColBg,
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
                ForeColor = Color.FromArgb(240, 246, 252),
                Font = new Font("Segoe UI", 9f),
                BorderStyle = BorderStyle.FixedSingle
            };
            _tbSets.GotFocus += (s, e) => { if (_tbSets.Text == "все (через запятую)") { _tbSets.Text = ""; _tbSets.ForeColor = Color.FromArgb(240, 246, 252); } };
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
                Title = " Инженерия & BIM v3.0 ",
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
            _cbGrids = StyleCheckBox(new CheckBox { Text = "Оси и уровни (_GRIDS)", Checked = true, Dock = DockStyle.Fill });
            _cbPipeTrace = StyleCheckBox(new CheckBox { Text = "Оси труб (DN/L)", Checked = false, Dock = DockStyle.Fill });
            _cbBoq = StyleCheckBox(new CheckBox { Text = "Смета ВОР в Excel/CSV", Checked = false, Dock = DockStyle.Fill });
            _cbBcf = StyleCheckBox(new CheckBox { Text = "Коллизии BCF 2.1", Checked = false, Dock = DockStyle.Fill });
            _cbAnonymize = StyleCheckBox(new CheckBox { Text = "Анонимизация свойств", Checked = false, Dock = DockStyle.Fill });

            pV3Grid.Controls.Add(_cbGeoShift, 0, 0);
            pV3Grid.Controls.Add(_cbGrids, 1, 0);
            pV3Grid.Controls.Add(_cbPipeTrace, 2, 0);
            pV3Grid.Controls.Add(_cbBoq, 0, 1);
            pV3Grid.Controls.Add(_cbBcf, 1, 1);
            pV3Grid.Controls.Add(_cbAnonymize, 2, 1);
            gbV3.Controls.Add(pV3Grid);

            // 3.7: Лог
            _tbLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9f),
                BackColor = ColInput,
                ForeColor = Color.FromArgb(201, 209, 217),
                BorderStyle = BorderStyle.FixedSingle
            };

            pMain.Controls.Add(_tbLog);
            pMain.Controls.Add(gbV3);
            pMain.Controls.Add(gbAdv);
            pMain.Controls.Add(gbOpt);
            pMain.Controls.Add(gbFmt);
            pMain.Controls.Add(gbOut);
            pMain.Controls.Add(gbIn);

            _tbLog.BringToFront();

            Log.AddSink(AppendLog);
            Log.Write("NWD2DWG v3.0 — запущен. Разработчик: BaidurovLabs (https://baidurovlabs.ru)");
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
                Text = "Свободное ПО с открытым исходным кодом. Автор: Baidurov Pavel (BaidurovLabs)",
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
                             "Copyright (C) 2026 Baidurov Pavel / BaidurovLabs (https://baidurovlabs.ru)\r\n\r\n" +
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

        public static Form CreateAboutForm()
        {
            var dlg = new Form
            {
                Text = "О программе NWD2DWG v3.0",
                Width = 840,
                Height = 640,
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = ColBg,
                ForeColor = ColText,
                Font = new Font("Segoe UI", 9f),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

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
                Text = "NWD2DWG v3.0 | BIM-конвертер Navisworks",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = ColAccent,
                Location = new Point(0, 2),
                AutoSize = true
            };
            var lbSub = new System.Windows.Forms.Label
            {
                Text = "Разработчик: Baidurov Pavel (BaidurovLabs) | Лицензия: GNU General Public License v3.0",
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
            var tbAbout = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Segoe UI", 9.5f),
                BackColor = ColInput,
                ForeColor = Color.FromArgb(220, 227, 235),
                BorderStyle = BorderStyle.None,
                TabStop = false
            };

            tbAbout.Text = "NWD2DWG — универсальный высокопроизводительный BIM-конвертер геометрии Navisworks (.NWD, .NWC, .NWF) в форматы AutoCAD (.DWG, .DXF), glTF/GLB (Web/VR) и IFC 2x3 (BIM-координация).\r\n\r\n" +
                           "► Инженерные возможности v3.0:\r\n" +
                           " • Сдвиг к нулю (0,0,0) + .wld — устранение графического дребезга на гигантских геодезических координатах\r\n" +
                           " • Оси и уровни (_GRIDS) — автоматическое извлечение координационных сеток и высотных отметок здания\r\n" +
                           " • Оси труб (DN/L) — скелетизация трубопроводных сетей с сохранением диаметров и длин участков\r\n" +
                           " • Смета ВОР в Excel/CSV — расчёт объёмов работ, площадей сеток и длин материалов по категориям\r\n" +
                           " • Коллизии BCF 2.1 — экспорт проверок Clash Detective в открытый стандарт BCF Zip с привязкой точек\r\n" +
                           " • 3D BIM Diff — геометрическое сравнение двух версий моделей с подсветкой новых, удалённых и изменённых тел\r\n" +
                           " • Пространственный тайлинг — нарезка площадок на сектора для лёгкой работы в AutoCAD через XREF\r\n" +
                           " • Анонимизация свойств — удаление коммерческих атрибутов и персональных данных перед передачей модели\r\n" +
                           " • TempCleaner — автоматическая и ручная очистка промежуточного кэша\r\n\r\n" +
                           "► Базовое ядро v2.0:\r\n" +
                           " • QEM Mesh Decimation — адаптивное сжатие полигональных сеток на 0-90% без искажения геометрии\r\n" +
                           " • Solid Reconstructor — PCA-распознавание примитивов и тел (цилиндры, трубы, балки, коробки)\r\n" +
                           " • BIM Attribute Transfer — перенос всех вкладок свойств элементов Navisworks в AutoCAD XData\r\n" +
                           " • glTF 2.0 / GLB и IFC 2x3 — прямой экспорт в открытые 3D и BIM форматы с материалами и PBR\r\n" +
                           " • BIM Watchdog — автоматическая фоновая служба мониторинга и пакетной конвертации директорий\r\n\r\n" +
                           "Совместимость: Autodesk Navisworks 2020-2026, AutoCAD 2018-2026, NanoCAD, Blender, Unity.";
            pBorder.Controls.Add(tbAbout);

            var pBot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0, 6, 0, 0)
            };
            pBot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pBot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pBot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));

            var lnkSite = new LinkLabel
            {
                Text = "baidurovlabs.ru",
                LinkColor = Color.FromArgb(88, 166, 255),
                ActiveLinkColor = Color.FromArgb(165, 214, 255),
                VisitedLinkColor = Color.FromArgb(88, 166, 255),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 20, 0)
            };
            lnkSite.LinkClicked += (s, e) => { try { Process.Start(new ProcessStartInfo("https://baidurovlabs.ru") { UseShellExecute = true }); } catch { } };

            var lnkGit = new LinkLabel
            {
                Text = "GitHub: github.com/AnT1pal/NWD2DWG",
                LinkColor = Color.FromArgb(88, 166, 255),
                ActiveLinkColor = Color.FromArgb(165, 214, 255),
                VisitedLinkColor = Color.FromArgb(88, 166, 255),
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 0, 0)
            };
            lnkGit.LinkClicked += (s, e) => { try { Process.Start(new ProcessStartInfo("https://github.com/AnT1pal/NWD2DWG") { UseShellExecute = true }); } catch { } };

            var btnClose = StyleButton(new Button { Text = "Закрыть", Width = 110, Height = 34, DialogResult = DialogResult.OK, TabStop = true }, true);
            btnClose.Anchor = AnchorStyles.Right;

            pBot.Controls.Add(lnkSite, 0, 0);
            pBot.Controls.Add(lnkGit, 1, 0);
            pBot.Controls.Add(btnClose, 2, 0);

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

        static Panel CreateInputPanel(TextBox tb)
        {
            var pBorder = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(70, 82, 100),
                Padding = new Padding(1),
                Height = 30
            };
            var pInner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(14, 18, 24),
                Padding = new Padding(8, 4, 8, 4)
            };
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = Color.FromArgb(14, 18, 24);
            tb.ForeColor = Color.FromArgb(240, 246, 252);
            tb.Font = new Font("Segoe UI", 10f);
            tb.Dock = DockStyle.Fill;
            pInner.Controls.Add(tb);
            pBorder.Controls.Add(pInner);
            return pBorder;
        }

        static Button StyleButton(Button btn, bool primary)
        {
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = primary ? Color.FromArgb(0, 150, 255) : ColBorder;
            btn.BackColor = primary ? ColBtnPrimary : ColBtnSec;
            btn.ForeColor = Color.White;
            btn.Cursor = Cursors.Hand;
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

            string logFile = Path.Combine(Path.GetTempPath(), "NWD2DWG", "convert_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            Log.SetFile(logFile);
            Log.Write("=== Конвертация начата ===");
            Log.Write("файлов к обработке: " + files.Count);

            var cts = new CancellationTokenSource();

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
                    _lbStatus.Text = "Открытие Navisworks…";

                    opts.Input = file;
                    try
                    {
                        ConvertStats st = NavisConverter.ConvertFile(opts, file, outPath,
                            s => _lbStatus.Text = s,
                            d => { _pb.Value = (int)(d * 1000); Application.DoEvents(); },
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
                        MessageBox.Show(this,
                            "Не удалось конвертировать:\n" + Path.GetFileName(file) + "\n\n" + ex.Message +
                            "\n\nПодробности: " + logFile,
                            "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    done++;
                }

                _lbStatus.Text = _cancel ? "Отменено." : "Готово.";
                Log.Write("=== Конвертация завершена ===");
                Log.Flush();
            }
            finally
            {
                _running = false;
                _btnConvert.Enabled = true;
                _btnCancel.Enabled = false;
                _btnDiag.Enabled = true;
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
                Anonymize = _cbAnonymize.Checked
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
