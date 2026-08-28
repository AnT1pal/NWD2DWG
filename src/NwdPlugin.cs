using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;
using Autodesk.Navisworks.Api.Plugins;

namespace NWD2DWG.Plugin
{
    [Plugin("NWD2DWG_Converter", "NWD2DWG", DisplayName = "NWD2DWG Exporter", ToolTip = "Converts NWD/NWC/NWF geometry to DXF/DWG")]
    public class NwdConverterPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            string inputNwd = parameters.Length > 0 ? parameters[0] : "";
            string outPath = parameters.Length > 1 ? parameters[1] : "";
            string format = parameters.Length > 2 ? parameters[2].ToLowerInvariant() : "dxf";
            bool skipHidden = parameters.Length > 3 && parameters[3] == "1";
            bool withColors = parameters.Length > 4 && parameters[4] == "1";
            bool layersPerItem = parameters.Length > 5 && parameters[5] == "1";
            string logPath = parameters.Length > 6 ? parameters[6] : "";
            bool splitDisciplines = parameters.Length > 7 && parameters[7] == "1";

            Action<string> log = msg =>
            {
                string line = string.Format(CultureInfo.InvariantCulture, "[{0:HH:mm:ss}] {1}", DateTime.Now, msg);
                if (!string.IsNullOrEmpty(logPath))
                {
                    try { File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8); } catch { }
                }
            };

            log("=== NWD2DWG In-Process Plugin запущен ===");
            log("Входной файл: " + inputNwd);
            log("Выходной файл: " + outPath);
            log(string.Format("Параметры: format={0}, skipHidden={1}, withColors={2}, layersPerItem={3}, split={4}",
                format, skipHidden, withColors, layersPerItem, splitDisciplines));

            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                Document doc = Application.ActiveDocument ?? Application.MainDocument;
                if (!string.IsNullOrEmpty(inputNwd))
                {
                    log("Открытие файла модели в Navisworks...");
                    if (doc == null || string.IsNullOrEmpty(doc.FileName) || !string.Equals(Path.GetFullPath(doc.FileName), Path.GetFullPath(inputNwd), StringComparison.OrdinalIgnoreCase))
                    {
                        if (doc != null)
                        {
                            if (!doc.TryOpenFile(inputNwd))
                                doc.OpenFile(inputNwd);
                        }
                        else if (Application.MainDocument != null)
                        {
                            if (!Application.MainDocument.TryOpenFile(inputNwd))
                                Application.MainDocument.OpenFile(inputNwd);
                        }
                        doc = Application.ActiveDocument ?? Application.MainDocument;
                    }
                }

                if (doc == null)
                {
                    log("ОШИБКА: Документ не открыт (ActiveDocument == null)");
                    return 1;
                }

                log("Документ открыт: " + doc.FileName + " | Моделей: " + doc.Models.Count);

                InwOpState10 state = ComApiBridge.State;
                if (state == null)
                {
                    log("ОШИБКА: ComApiBridge.State == null");
                    return 2;
                }

                // Единицы документа
                int insUnits = 4; // по умолчанию мм
                try
                {
                    string uStr = doc.Units.ToString().ToLowerInvariant();
                    if (uStr.Contains("milli")) insUnits = 4;
                    else if (uStr.Contains("centi")) insUnits = 5;
                    else if (uStr.Contains("meter")) insUnits = 6;
                    else if (uStr.Contains("inch")) insUnits = 1;
                    else if (uStr.Contains("foot") || uStr.Contains("feet")) insUnits = 2;
                }
                catch { }

                var sink = new PluginPrimitiveSink();
                var cbProxy = new CallbackProxy { Sink = sink };

                int totalItems = 0;
                int totalFragments = 0;
                long totalTriangles = 0;
                long totalVertices = 0;
                int hiddenSkipped = 0;
                bool use3dFace = format == "3dface";

                if (splitDisciplines)
                {
                    log("--- Запуск экспорта по разделам (XREF) ---");
                    int sectionCount = 0;
                    string baseOutDir = Path.GetDirectoryName(outPath) ?? ".";
                    string baseOutName = Path.GetFileNameWithoutExtension(outPath);

                    foreach (Model model in doc.Models)
                    {
                        ModelItem root = model.RootItem;
                        if (root == null) continue;

                        var childList = new List<ModelItem>();
                        if (root.Children != null)
                        {
                            foreach (ModelItem c in root.Children) childList.Add(c);
                        }
                        IEnumerable itemsToSplit = childList.Count > 0 ? (IEnumerable)childList : new ModelItem[] { root };

                        foreach (ModelItem sectionItem in itemsToSplit)
                        {
                            sectionCount++;
                            string sectionName = !string.IsNullOrEmpty(sectionItem.DisplayName) ? sectionItem.DisplayName : ("Раздел_" + sectionCount);
                            string cleanName = PluginDxfWriter.SanitizeLayer(sectionName);
                            string sectionOutPath = Path.Combine(baseOutDir, string.Format("{0}_{1:D2}_{2}.dxf", baseOutName, sectionCount, cleanName));

                            log(string.Format("Экспорт раздела {0}: {1} -> {2}", sectionCount, sectionName, Path.GetFileName(sectionOutPath)));

                            var sectionLayers = new List<string> { cleanName };
                            foreach (ModelItem itm in sectionItem.DescendantsAndSelf)
                            {
                                try { if (!string.IsNullOrEmpty(itm.DisplayName)) sectionLayers.Add(PluginDxfWriter.SanitizeLayer(itm.DisplayName)); } catch { }
                            }

                            int secItems = 0, secFrags = 0;
                            long secTris = 0;

                            using (var secWriter = new PluginDxfWriter(sectionOutPath, use3dFace, insUnits, withColors))
                            {
                                secWriter.WritePreamble(sectionLayers);
                                var secBatcher = new MeshBatcher(secWriter, 15000);

                                foreach (ModelItem item in sectionItem.DescendantsAndSelf)
                                {
                                    secItems++;
                                    totalItems++;

                                    if (skipHidden && item.IsHidden)
                                    {
                                        hiddenSkipped++;
                                        continue;
                                    }

                                    if (!item.HasGeometry) continue;

                                    InwOaPath3 oaPath = null;
                                    try { oaPath = (InwOaPath3)ComApiBridge.ToInwOaPath(item); } catch { continue; }
                                    if (oaPath == null) continue;

                                    IEnumerable frags = null;
                                    try { frags = (IEnumerable)oaPath.Fragments(); } catch { continue; }
                                    if (frags == null) continue;

                                    int rgb = -1;
                                    if (withColors)
                                    {
                                        try
                                        {
                                            if (item.Geometry != null && item.Geometry.OriginalColor != null)
                                            {
                                                var c = item.Geometry.OriginalColor;
                                                rgb = ((int)(c.R * 255) << 16) | ((int)(c.G * 255) << 8) | (int)(c.B * 255);
                                            }
                                        }
                                        catch { }
                                    }

                                    string layer = layersPerItem
                                        ? (!string.IsNullOrEmpty(item.DisplayName) ? item.DisplayName : cleanName)
                                        : cleanName;

                                    foreach (InwOaFragment3 frag in frags)
                                    {
                                        double[] m = GetMatrix(frag);
                                        if (m == null) continue;

                                        sink.Reset(m);

                                        try { frag.GenerateSimplePrimitives(nwEVertexProperty.eNORMAL, cbProxy); } catch { continue; }

                                        if (sink.TriCount > 0)
                                        {
                                            secFrags++;
                                            totalFragments++;
                                            secTris += sink.TriCount;
                                            totalTriangles += sink.TriCount;
                                            totalVertices += sink.Verts.Count / 3;
                                            secBatcher.AddGeometry(layer, rgb, sink.Verts, sink.Quads);
                                        }
                                    }
                                }

                                secBatcher.FlushAll();
                                secWriter.WritePostamble();
                            }

                            FileInfo secFi = new FileInfo(sectionOutPath);
                            log(string.Format("Раздел {0} готов: {1:F2} МБ | полигонов: {2}", Path.GetFileName(sectionOutPath), secFi.Length / 1048576.0, secTris));
                        }
                    }

                    log(string.Format("ГОТОВО (по разделам): разделов: {0}, полигонов всего: {1} | время: {2}",
                        sectionCount, totalTriangles, sw.Elapsed));
                    return 0;
                }

                // Единый файл
                var layerList = new List<string>();
                foreach (Model model in doc.Models)
                {
                    ModelItem root = model.RootItem;
                    if (root == null) continue;
                    string modelName = "Model";
                    try { modelName = !string.IsNullOrEmpty(model.RootItem.DisplayName) ? model.RootItem.DisplayName : Path.GetFileNameWithoutExtension(model.FileName); }
                    catch { }
                    layerList.Add(modelName);
                    if (layersPerItem)
                    {
                        foreach (ModelItem itm in root.DescendantsAndSelf)
                        {
                            try
                            {
                                string n = itm.DisplayName;
                                if (!string.IsNullOrEmpty(n)) layerList.Add(n);
                            }
                            catch { }
                        }
                    }
                }

                using (var writer = new PluginDxfWriter(outPath, use3dFace, insUnits, withColors))
                {
                    writer.WritePreamble(layerList);
                    var batcher = new MeshBatcher(writer, 15000);

                    foreach (Model model in doc.Models)
                    {
                        ModelItem root = model.RootItem;
                        if (root == null) continue;

                        string modelName = "Model";
                        try { modelName = !string.IsNullOrEmpty(model.RootItem.DisplayName) ? model.RootItem.DisplayName : Path.GetFileNameWithoutExtension(model.FileName); }
                        catch { }

                        foreach (ModelItem item in root.DescendantsAndSelf)
                        {
                            totalItems++;

                            if (skipHidden && item.IsHidden)
                            {
                                hiddenSkipped++;
                                continue;
                            }

                            if (!item.HasGeometry) continue;

                            InwOaPath3 oaPath = null;
                            try { oaPath = (InwOaPath3)ComApiBridge.ToInwOaPath(item); } catch { continue; }
                            if (oaPath == null) continue;

                            IEnumerable frags = null;
                            try { frags = (IEnumerable)oaPath.Fragments(); } catch { continue; }
                            if (frags == null) continue;

                            int rgb = -1;
                            if (withColors)
                            {
                                try
                                {
                                    if (item.Geometry != null && item.Geometry.OriginalColor != null)
                                    {
                                        var c = item.Geometry.OriginalColor;
                                        rgb = ((int)(c.R * 255) << 16) | ((int)(c.G * 255) << 8) | (int)(c.B * 255);
                                    }
                                }
                                catch { }
                            }

                            string layer = layersPerItem
                                ? (!string.IsNullOrEmpty(item.DisplayName) ? item.DisplayName : modelName)
                                : modelName;

                            foreach (InwOaFragment3 frag in frags)
                            {
                                double[] m = GetMatrix(frag);
                                if (m == null) continue;

                                sink.Reset(m);

                                try
                                {
                                    frag.GenerateSimplePrimitives(nwEVertexProperty.eNORMAL, cbProxy);
                                }
                                catch { continue; }

                                if (sink.TriCount > 0)
                                {
                                    totalFragments++;
                                    totalTriangles += sink.TriCount;
                                    totalVertices += sink.Verts.Count / 3;
                                    batcher.AddGeometry(layer, rgb, sink.Verts, sink.Quads);
                                }
                            }

                            if (totalItems % 2000 == 0)
                            {
                                log(string.Format(CultureInfo.InvariantCulture,
                                    "Обработано элементов: {0}, фрагментов: {1}, треугольников: {2}",
                                    totalItems, totalFragments, totalTriangles));
                            }
                        }
                    }

                    batcher.FlushAll();
                    writer.WritePostamble();
                }

                FileInfo fi = new FileInfo(outPath);
                log(string.Format(CultureInfo.InvariantCulture,
                    "ГОТОВО: {0} | элементов: {1}, фрагментов: {2}, треугольников: {3}, вершин: {4} | размер: {5:F2} МБ | время: {6}",
                    Path.GetFileName(outPath), totalItems, totalFragments, totalTriangles, totalVertices,
                    fi.Length / 1048576.0, sw.Elapsed));

                return 0;
            }
            catch (Exception ex)
            {
                string err = "КРИТИЧЕСКАЯ ОШИБКА: " + ex.ToString();
                log(err);
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "NWD2DWG_plugin_error.log"), err, Encoding.UTF8); } catch { }
                return 99;
            }
        }

        static double[] GetMatrix(InwOaFragment3 frag)
        {
            try
            {
                InwLTransform3f3 t = (InwLTransform3f3)frag.GetLocalToWorldMatrix();
                Array m = (Array)t.Matrix;
                if (m == null || m.Length < 16) return null;
                var r = new double[16];
                int lb = m.GetLowerBound(0);
                for (int i = 0; i < 16; i++) r[i] = Convert.ToDouble(m.GetValue(lb + i));
                return r;
            }
            catch
            {
                return new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            }
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public class CallbackProxy : InwSimplePrimitivesCB
    {
        public PluginPrimitiveSink Sink;

        public void Triangle(InwSimpleVertex v1, InwSimpleVertex v2, InwSimpleVertex v3)
        {
            if (Sink == null) return;
            double x1, y1, z1, x2, y2, z2, x3, y3, z3;
            if (!Sink.VertexToWorld(v1, out x1, out y1, out z1)) return;
            if (!Sink.VertexToWorld(v2, out x2, out y2, out z2)) return;
            if (!Sink.VertexToWorld(v3, out x3, out y3, out z3)) return;
            Sink.AddTriangle(x1, y1, z1, x2, y2, z2, x3, y3, z3);
        }

        public void Line(InwSimpleVertex v1, InwSimpleVertex v2) { }
        public void Point(InwSimpleVertex v1) { }
        public void SnapPoint(InwSimpleVertex v1) { }
    }

    public class MeshBatcher
    {
        private readonly PluginDxfWriter _writer;
        private readonly int _maxVertsPerMesh;

        private class BatchData
        {
            public List<double> Verts = new List<double>(45000);
            public List<int> Quads = new List<int>(60000);
        }

        private readonly Dictionary<string, BatchData> _batches = new Dictionary<string, BatchData>(StringComparer.OrdinalIgnoreCase);

        public MeshBatcher(PluginDxfWriter writer, int maxVertsPerMesh = 15000)
        {
            _writer = writer;
            _maxVertsPerMesh = maxVertsPerMesh;
        }

        public void AddGeometry(string layer, int rgb, List<double> verts, List<int> quads)
        {
            if (verts == null || verts.Count == 0 || quads == null || quads.Count == 0) return;

            string key = (layer ?? "0") + "|" + rgb;
            BatchData b;
            if (!_batches.TryGetValue(key, out b))
            {
                b = new BatchData();
                _batches[key] = b;
            }

            int baseOffset = b.Verts.Count / 3;
            int newVertCount = verts.Count / 3;

            if (baseOffset + newVertCount > _maxVertsPerMesh && b.Quads.Count > 0)
            {
                FlushKey(key, b);
                b = new BatchData();
                _batches[key] = b;
                baseOffset = 0;
            }

            b.Verts.AddRange(verts);
            for (int i = 0; i < quads.Count; i++)
            {
                b.Quads.Add(quads[i] + baseOffset);
            }

            if (b.Verts.Count / 3 >= _maxVertsPerMesh)
            {
                FlushKey(key, b);
                b.Verts.Clear();
                b.Quads.Clear();
            }
        }

        private void FlushKey(string key, BatchData b)
        {
            if (b.Quads.Count == 0) return;
            int pipeIdx = key.LastIndexOf('|');
            string layer = pipeIdx > 0 ? key.Substring(0, pipeIdx) : key;
            int rgb = -1;
            if (pipeIdx > 0) int.TryParse(key.Substring(pipeIdx + 1), out rgb);
            _writer.WriteMesh(layer, rgb, b.Verts, b.Quads);
        }

        public void FlushAll()
        {
            foreach (var kv in _batches)
            {
                FlushKey(kv.Key, kv.Value);
            }
            _batches.Clear();
        }
    }

    public class PluginPrimitiveSink
    {
        public double[] Matrix;
        public List<double> Verts = new List<double>();
        public List<int> Quads = new List<int>();
        public int TriCount;
        public int SkippedDegenerate;
        public int VertexReadErrors;
        private Dictionary<ulong, int> _index = new Dictionary<ulong, int>();

        public void Reset(double[] matrix)
        {
            Matrix = matrix;
            Verts.Clear();
            Quads.Clear();
            _index.Clear();
            TriCount = 0;
            SkippedDegenerate = 0;
            VertexReadErrors = 0;
        }

        public bool VertexToWorld(InwSimpleVertex vertex, out double x, out double y, out double z)
        {
            x = y = z = 0;
            try
            {
                Array a = (Array)vertex.coord;
                if (a == null) { VertexReadErrors++; return false; }
                int lb = a.GetLowerBound(0);
                double vx = Convert.ToDouble(a.GetValue(lb));
                double vy = Convert.ToDouble(a.GetValue(lb + 1));
                double vz = Convert.ToDouble(a.GetValue(lb + 2));
                double[] m = Matrix;
                double t1 = vx * m[3] + vy * m[7] + vz * m[11] + m[15];
                if (Math.Abs(t1) < 1e-12) t1 = 1.0;
                x = (vx * m[0] + vy * m[4] + vz * m[8] + m[12]) / t1;
                y = (vx * m[1] + vy * m[5] + vz * m[9] + m[13]) / t1;
                z = (vx * m[2] + vy * m[6] + vz * m[10] + m[14]) / t1;
                return true;
            }
            catch { VertexReadErrors++; return false; }
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
                k = (k ^ (k >> 30)) * 0xBF58476D1CE4E5B9UL;
                k = (k ^ (k >> 27)) * 0x94D049BB133111EBUL;
                return k ^ (k >> 31);
            }
        }

        public int AddVertex(double x, double y, double z)
        {
            ulong k = Key(x, y, z);
            int idx;
            if (_index.TryGetValue(k, out idx)) return idx;
            idx = Verts.Count / 3;
            _index[k] = idx;
            Verts.Add(x); Verts.Add(y); Verts.Add(z);
            return idx;
        }

        public void AddTriangle(double x1, double y1, double z1, double x2, double y2, double z2, double x3, double y3, double z3)
        {
            int a = AddVertex(x1, y1, z1);
            int b = AddVertex(x2, y2, z2);
            int c = AddVertex(x3, y3, z3);
            if (a == b || b == c || a == c) { SkippedDegenerate++; return; }
            Quads.Add(a); Quads.Add(b); Quads.Add(c); Quads.Add(c);
            TriCount++;
        }
    }

    public class PluginDxfWriter : IDisposable
    {
        private StreamWriter _w;
        private bool _use3dFace;
        private int _insUnits;
        private bool _withColors;

        public PluginDxfWriter(string path, bool use3dFace, int insUnits, bool withColors)
        {
            _use3dFace = use3dFace;
            _insUnits = insUnits;
            _withColors = withColors;
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20);
            _w.NewLine = "\r\n";
        }

        public void WritePreamble(IEnumerable<string> layerNames)
        {
            _w.WriteLine("0");
            _w.WriteLine("SECTION");
            _w.WriteLine("2");
            _w.WriteLine("HEADER");
            _w.WriteLine("9");
            _w.WriteLine("$ACADVER");
            _w.WriteLine("1");
            _w.WriteLine("AC1009");
            _w.WriteLine("9");
            _w.WriteLine("$INSUNITS");
            _w.WriteLine("70");
            _w.WriteLine(_insUnits.ToString(CultureInfo.InvariantCulture));
            _w.WriteLine("0");
            _w.WriteLine("ENDSEC");

            _w.WriteLine("0");
            _w.WriteLine("SECTION");
            _w.WriteLine("2");
            _w.WriteLine("ENTITIES");
        }

        public void WriteMesh(string layer, int rgb, List<double> verts, List<int> quads)
        {
            if (verts == null || verts.Count == 0 || quads == null || quads.Count == 0) return;
            string cleanLayer = SanitizeLayer(layer);

            if (_use3dFace)
            {
                int triCount = quads.Count / 4;
                for (int t = 0; t < triCount; t++)
                {
                    int i1 = quads[t * 4] * 3;
                    int i2 = quads[t * 4 + 1] * 3;
                    int i3 = quads[t * 4 + 2] * 3;

                    _w.WriteLine("0");
                    _w.WriteLine("3DFACE");
                    _w.WriteLine("8");
                    _w.WriteLine(cleanLayer);
                    if (_withColors && rgb > 0)
                    {
                        _w.WriteLine("420");
                        _w.WriteLine(rgb.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _w.WriteLine("62");
                        _w.WriteLine("7");
                    }
                    _w.WriteLine("10");
                    _w.WriteLine(verts[i1].ToString("G12", CultureInfo.InvariantCulture));
                    _w.WriteLine("20");
                    _w.WriteLine(verts[i1 + 1].ToString("G12", CultureInfo.InvariantCulture));
                    _w.WriteLine("30");
                    _w.WriteLine(verts[i1 + 2].ToString("G12", CultureInfo.InvariantCulture));

                    _w.WriteLine("11");
                    _w.WriteLine(verts[i2].ToString("G12", CultureInfo.InvariantCulture));
                    _w.WriteLine("21");
                    _w.WriteLine(verts[i2 + 1].ToString("G12", CultureInfo.InvariantCulture));
                    _w.WriteLine("31");
                    _w.WriteLine(verts[i2 + 2].ToString("G12", CultureInfo.InvariantCulture));

                    _w.WriteLine("12");
                    _w.WriteLine(verts[i3].ToString("G12", CultureInfo.InvariantCulture));
                    _w.WriteLine("22");
                    _w.WriteLine(verts[i3 + 1].ToString("G12", CultureInfo.InvariantCulture));
                    _w.WriteLine("32");
                    _w.WriteLine(verts[i3 + 2].ToString("G12", CultureInfo.InvariantCulture));

                    _w.WriteLine("13");
                    _w.WriteLine(verts[i3].ToString("G12", CultureInfo.InvariantCulture));
                    _w.WriteLine("23");
                    _w.WriteLine(verts[i3 + 1].ToString("G12", CultureInfo.InvariantCulture));
                    _w.WriteLine("33");
                    _w.WriteLine(verts[i3 + 2].ToString("G12", CultureInfo.InvariantCulture));
                }
                return;
            }

            // PolyfaceMesh
            int vertCount = verts.Count / 3;
            int faceCount = quads.Count / 4;

            _w.WriteLine("0");
            _w.WriteLine("POLYLINE");
            _w.WriteLine("8");
            _w.WriteLine(cleanLayer);
            if (_withColors && rgb > 0)
            {
                _w.WriteLine("420");
                _w.WriteLine(rgb.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                _w.WriteLine("62");
                _w.WriteLine("7");
            }
            _w.WriteLine("66");
            _w.WriteLine("1");
            _w.WriteLine("70");
            _w.WriteLine("64");
            _w.WriteLine("10");
            _w.WriteLine("0.0");
            _w.WriteLine("20");
            _w.WriteLine("0.0");
            _w.WriteLine("30");
            _w.WriteLine("0.0");
            _w.WriteLine("71");
            _w.WriteLine(vertCount.ToString(CultureInfo.InvariantCulture));
            _w.WriteLine("72");
            _w.WriteLine(faceCount.ToString(CultureInfo.InvariantCulture));

            // Vertices
            for (int v = 0; v < vertCount; v++)
            {
                int baseIdx = v * 3;
                _w.WriteLine("0");
                _w.WriteLine("VERTEX");
                _w.WriteLine("8");
                _w.WriteLine(cleanLayer);
                _w.WriteLine("10");
                _w.WriteLine(verts[baseIdx].ToString("G12", CultureInfo.InvariantCulture));
                _w.WriteLine("20");
                _w.WriteLine(verts[baseIdx + 1].ToString("G12", CultureInfo.InvariantCulture));
                _w.WriteLine("30");
                _w.WriteLine(verts[baseIdx + 2].ToString("G12", CultureInfo.InvariantCulture));
                _w.WriteLine("70");
                _w.WriteLine("192");
            }

            // Faces (4th vertex is duplicate of 3rd for triangles)
            for (int f = 0; f < faceCount; f++)
            {
                int i1 = quads[f * 4] + 1;
                int i2 = quads[f * 4 + 1] + 1;
                int i3 = quads[f * 4 + 2] + 1;
                int i4 = quads[f * 4 + 3] + 1;

                _w.WriteLine("0");
                _w.WriteLine("VERTEX");
                _w.WriteLine("8");
                _w.WriteLine(cleanLayer);
                _w.WriteLine("10");
                _w.WriteLine("0.0");
                _w.WriteLine("20");
                _w.WriteLine("0.0");
                _w.WriteLine("30");
                _w.WriteLine("0.0");
                _w.WriteLine("70");
                _w.WriteLine("128");
                _w.WriteLine("71");
                _w.WriteLine(i1.ToString(CultureInfo.InvariantCulture));
                _w.WriteLine("72");
                _w.WriteLine(i2.ToString(CultureInfo.InvariantCulture));
                _w.WriteLine("73");
                _w.WriteLine(i3.ToString(CultureInfo.InvariantCulture));
                _w.WriteLine("74");
                _w.WriteLine(i4.ToString(CultureInfo.InvariantCulture));
            }

            _w.WriteLine("0");
            _w.WriteLine("SEQEND");
            _w.WriteLine("8");
            _w.WriteLine(cleanLayer);
        }

        public void WritePostamble()
        {
            _w.WriteLine("0");
            _w.WriteLine("ENDSEC");
            _w.WriteLine("0");
            _w.WriteLine("EOF");
            _w.Flush();
        }

        static readonly Dictionary<char, string> TranslitMap = new Dictionary<char, string>
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
                if (TranslitMap.TryGetValue(ch, out tr)) { sb.Append(tr); continue; }
                sb.Append('_');
            }
            string s = sb.ToString().Trim('_', ' ', '\t');
            if (s.Length == 0) s = "0";
            if (s.Length > 240) s = s.Substring(0, 240);
            return s;
        }

        public void Dispose()
        {
            if (_w != null)
            {
                _w.Dispose();
                _w = null;
            }
        }
    }
}
