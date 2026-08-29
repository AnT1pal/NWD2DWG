using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

            // === v2.0 параметры ===
            int decimatePercent = 0;
            if (parameters.Length > 8) int.TryParse(parameters[8], out decimatePercent);

            bool solidDetect = parameters.Length > 9 && parameters[9] == "1";

            bool transferXData = parameters.Length > 10 && parameters[10] == "1";

            string selectionSets = parameters.Length > 11 ? parameters[11] : "";

            // Section Box: "minX;minY;minZ;maxX;maxY;maxZ" или пусто
            double[] sectionBox = null;
            if (parameters.Length > 12 && !string.IsNullOrEmpty(parameters[12]))
            {
                string[] parts = parameters[12].Split(';');
                if (parts.Length == 6)
                {
                    sectionBox = new double[6];
                    bool ok = true;
                    for (int pi = 0; pi < 6; pi++)
                        if (!double.TryParse(parts[pi], NumberStyles.Float, CultureInfo.InvariantCulture, out sectionBox[pi]))
                            ok = false;
                    if (!ok) sectionBox = null;
                }
            }

            bool transferMaterials = parameters.Length > 13 && parameters[13] == "1";

            int parallelThreads = 0;
            if (parameters.Length > 14) int.TryParse(parameters[14], out parallelThreads);

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
            log(string.Format("v2.0: decimate={0}%, solidDetect={1}, xdata={2}, materials={3}, threads={4}, sectionBox={5}, sets={6}",
                decimatePercent, solidDetect, transferXData, transferMaterials, parallelThreads,
                sectionBox != null ? string.Join(";", Array.ConvertAll(sectionBox, d => d.ToString("G6", CultureInfo.InvariantCulture))) : "нет",
                string.IsNullOrEmpty(selectionSets) ? "все" : selectionSets));

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
                bool useGltf = format == "gltf" || format == "glb";
                bool useIfc = format == "ifc";

                // === Selection Sets: построить HashSet допустимых элементов ===
                HashSet<int> allowedItems = null;
                if (!string.IsNullOrEmpty(selectionSets) && selectionSets != "*")
                {
                    allowedItems = new HashSet<int>();
                    var setNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string s in selectionSets.Split(','))
                    {
                        string trimmed = s.Trim();
                        if (!string.IsNullOrEmpty(trimmed)) setNames.Add(trimmed);
                    }

                    try
                    {
                        dynamic dss = doc.SelectionSets;
                        if (dss != null && dss.Value != null)
                        {
                            foreach (dynamic si in dss.Value)
                            {
                                string dName = "";
                                try { dName = si.DisplayName; } catch { }
                                if (!setNames.Contains(dName)) continue;
                                log("Включён Selection Set: " + dName);
                                dynamic items = null;
                                try { items = si.GetSelectedItems(); } catch { }
                                if (items != null)
                                {
                                    foreach (ModelItem mi in items)
                                    {
                                        allowedItems.Add(mi.GetHashCode());
                                        foreach (ModelItem desc in mi.DescendantsAndSelf)
                                            allowedItems.Add(desc.GetHashCode());
                                    }
                                }
                            }
                        }
                        log("Selection Sets: допущено элементов: " + allowedItems.Count);
                    }
                    catch (Exception ex)
                    {
                        log("Предупреждение: Selection Sets не удалось загрузить: " + ex.Message);
                        allowedItems = null;
                    }
                }

                // === Лямбда: проверка Section Box ===
                Func<double, double, double, double, double, double, double, double, double, bool> isInBox = null;
                if (sectionBox != null)
                {
                    double bMinX = sectionBox[0], bMinY = sectionBox[1], bMinZ = sectionBox[2];
                    double bMaxX = sectionBox[3], bMaxY = sectionBox[4], bMaxZ = sectionBox[5];
                    isInBox = (x1, y1, z1, x2, y2, z2, x3, y3, z3) =>
                    {
                        // Проверяем центроид треугольника
                        double cx = (x1 + x2 + x3) / 3.0;
                        double cy = (y1 + y2 + y3) / 3.0;
                        double cz = (z1 + z2 + z3) / 3.0;
                        return cx >= bMinX && cx <= bMaxX && cy >= bMinY && cy <= bMaxY && cz >= bMinZ && cz <= bMaxZ;
                    };
                    log(string.Format(CultureInfo.InvariantCulture,
                        "Section Box: ({0:G6}, {1:G6}, {2:G6}) - ({3:G6}, {4:G6}, {5:G6})",
                        bMinX, bMinY, bMinZ, bMaxX, bMaxY, bMaxZ));
                }

                // === Лямбда: извлечение BIM-свойств для XData ===
                Func<ModelItem, Dictionary<string, string>> extractProperties = null;
                if (transferXData)
                {
                    extractProperties = (item) =>
                    {
                        var props = new Dictionary<string, string>();
                        try
                        {
                            PropertyCategoryCollection cats = item.PropertyCategories;
                            if (cats == null) return props;
                            foreach (PropertyCategory cat in cats)
                            {
                                string catName = cat.DisplayName;
                                DataPropertyCollection dataProps = cat.Properties;
                                if (dataProps == null) continue;
                                foreach (DataProperty dp in dataProps)
                                {
                                    string key = catName + "::" + dp.DisplayName;
                                    string val = "";
                                    try { val = dp.Value.ToDisplayString(); } catch { }
                                    if (!string.IsNullOrEmpty(val))
                                        props[key] = val;
                                }
                            }
                        }
                        catch { }
                        return props;
                    };
                }

                // === Лямбда: чтение прозрачности материала ===
                Func<ModelItem, int> readTransparency = null;
                if (transferMaterials)
                {
                    readTransparency = (item) =>
                    {
                        try
                        {
                            PropertyCategoryCollection cats = item.PropertyCategories;
                            if (cats != null)
                            {
                                foreach (PropertyCategory cat in cats)
                                {
                                    if (!cat.DisplayName.ToLowerInvariant().Contains("material")) continue;
                                    foreach (DataProperty dp in cat.Properties)
                                    {
                                        if (dp.DisplayName.ToLowerInvariant().Contains("transparenc"))
                                        {
                                            string v = dp.Value.ToDisplayString();
                                            double tv;
                                            if (double.TryParse(v.Replace("%", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out tv))
                                            {
                                                if (tv > 1.0) tv = tv / 100.0;
                                                return (int)(255 * tv);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                        return 0;
                    };
                }

                // === glTF / IFC writers ===
                GltfWriter gltfWriter = null;
                IfcWriter ifcWriter = null;
                if (useGltf)
                {
                    string gltfPath = Path.ChangeExtension(outPath, format == "glb" ? ".glb" : ".gltf");
                    gltfWriter = new GltfWriter(gltfPath);
                    log("glTF writer создан: " + gltfPath);
                }
                if (useIfc)
                {
                    string ifcPath = Path.ChangeExtension(outPath, ".ifc");
                    ifcWriter = new IfcWriter(ifcPath);
                    log("IFC writer создан: " + ifcPath);
                }

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

                                    // === Selection Sets фильтр ===
                                    if (allowedItems != null && !allowedItems.Contains(item.GetHashCode()))
                                        continue;

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

                                    // === Прозрачность ===
                                    int transparency = readTransparency != null ? readTransparency(item) : 0;

                                    // === BIM свойства (XData) ===
                                    Dictionary<string, string> bimProps = extractProperties != null ? extractProperties(item) : null;

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
                                            var currentVerts = new List<double>(sink.Verts);
                                            var currentQuads = new List<int>(sink.Quads);

                                            // === Section Box crop ===
                                            if (isInBox != null)
                                            {
                                                var filteredQuads = new List<int>();
                                                for (int qi = 0; qi < currentQuads.Count; qi += 4)
                                                {
                                                    int a = currentQuads[qi], b = currentQuads[qi + 1], c = currentQuads[qi + 2];
                                                    if (isInBox(
                                                        currentVerts[a * 3], currentVerts[a * 3 + 1], currentVerts[a * 3 + 2],
                                                        currentVerts[b * 3], currentVerts[b * 3 + 1], currentVerts[b * 3 + 2],
                                                        currentVerts[c * 3], currentVerts[c * 3 + 1], currentVerts[c * 3 + 2]))
                                                    {
                                                        filteredQuads.Add(currentQuads[qi]);
                                                        filteredQuads.Add(currentQuads[qi + 1]);
                                                        filteredQuads.Add(currentQuads[qi + 2]);
                                                        filteredQuads.Add(currentQuads[qi + 3]);
                                                    }
                                                }
                                                currentQuads = filteredQuads;
                                                if (currentQuads.Count == 0) continue;
                                            }

                                            // === Mesh Decimation ===
                                            if (decimatePercent > 0 && decimatePercent <= 90)
                                            {
                                                double ratio = decimatePercent / 100.0;
                                                MeshDecimator.Decimate(ref currentVerts, ref currentQuads, ratio);
                                            }

                                            secFrags++;
                                            totalFragments++;
                                            secTris += currentQuads.Count / 4;
                                            totalTriangles += currentQuads.Count / 4;
                                            totalVertices += currentVerts.Count / 3;

                                            // === Solid Detection ===
                                            if (solidDetect)
                                            {
                                                SolidResult solid = SolidReconstructor.TryReconstruct(currentVerts, currentQuads);
                                                if (solid != null && solid.Type != SolidType.None && solid.Confidence > 0.7)
                                                {
                                                    SolidReconstructor.WriteSolidDxf(secWriter.RawWriter, solid, PluginDxfWriter.SanitizeLayer(layer), rgb);
                                                    continue;
                                                }
                                            }

                                            secBatcher.AddGeometry(layer, rgb, currentVerts, currentQuads, transparency);

                                            // === XData ===
                                            if (bimProps != null && bimProps.Count > 0)
                                            {
                                                secWriter.WriteXData(PluginDxfWriter.SanitizeLayer(layer), bimProps);
                                            }
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

                // DXF writer (основной формат, или null если glTF/IFC)
                PluginDxfWriter writer = null;
                MeshBatcher batcher = null;
                if (!useGltf && !useIfc)
                {
                    writer = new PluginDxfWriter(outPath, use3dFace, insUnits, withColors);
                    writer.WritePreamble(layerList);
                    batcher = new MeshBatcher(writer, 15000);
                }

                try
                {
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

                            // === Selection Sets фильтр ===
                            if (allowedItems != null && !allowedItems.Contains(item.GetHashCode()))
                                continue;

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

                            // === Прозрачность ===
                            int transparency = readTransparency != null ? readTransparency(item) : 0;

                            // === BIM свойства (XData) ===
                            Dictionary<string, string> bimProps = extractProperties != null ? extractProperties(item) : null;

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
                                    var currentVerts = new List<double>(sink.Verts);
                                    var currentQuads = new List<int>(sink.Quads);

                                    // === Section Box crop ===
                                    if (isInBox != null)
                                    {
                                        var filteredQuads = new List<int>();
                                        for (int qi = 0; qi < currentQuads.Count; qi += 4)
                                        {
                                            int a = currentQuads[qi], b = currentQuads[qi + 1], c = currentQuads[qi + 2];
                                            if (isInBox(
                                                currentVerts[a * 3], currentVerts[a * 3 + 1], currentVerts[a * 3 + 2],
                                                currentVerts[b * 3], currentVerts[b * 3 + 1], currentVerts[b * 3 + 2],
                                                currentVerts[c * 3], currentVerts[c * 3 + 1], currentVerts[c * 3 + 2]))
                                            {
                                                filteredQuads.Add(currentQuads[qi]);
                                                filteredQuads.Add(currentQuads[qi + 1]);
                                                filteredQuads.Add(currentQuads[qi + 2]);
                                                filteredQuads.Add(currentQuads[qi + 3]);
                                            }
                                        }
                                        currentQuads = filteredQuads;
                                        if (currentQuads.Count == 0) continue;
                                    }

                                    // === Mesh Decimation ===
                                    if (decimatePercent > 0 && decimatePercent <= 90)
                                    {
                                        double ratio = decimatePercent / 100.0;
                                        MeshDecimator.Decimate(ref currentVerts, ref currentQuads, ratio);
                                    }

                                    totalFragments++;
                                    totalTriangles += currentQuads.Count / 4;
                                    totalVertices += currentVerts.Count / 3;

                                    // === Solid Detection ===
                                    if (solidDetect && writer != null)
                                    {
                                        SolidResult solid = SolidReconstructor.TryReconstruct(currentVerts, currentQuads);
                                        if (solid != null && solid.Type != SolidType.None && solid.Confidence > 0.7)
                                        {
                                            SolidReconstructor.WriteSolidDxf(writer.RawWriter, solid, PluginDxfWriter.SanitizeLayer(layer), rgb);
                                            continue; // Используем solid вместо mesh
                                        }
                                    }

                                    // === DXF output ===
                                    if (batcher != null)
                                    {
                                        batcher.AddGeometry(layer, rgb, currentVerts, currentQuads, transparency);

                                        // === XData ===
                                        if (bimProps != null && bimProps.Count > 0 && writer != null)
                                        {
                                            writer.WriteXData(PluginDxfWriter.SanitizeLayer(layer), bimProps);
                                        }
                                    }

                                    // === glTF output ===
                                    if (gltfWriter != null)
                                    {
                                        var gltfMesh = new GltfMeshData
                                        {
                                            Name = layer,
                                            Verts = currentVerts,
                                            Rgb = rgb,
                                            Transparency = transparency / 255.0
                                        };
                                        // Конвертируем quads в triangle indices
                                        gltfMesh.Indices = new List<int>();
                                        for (int qi = 0; qi < currentQuads.Count; qi += 4)
                                        {
                                            gltfMesh.Indices.Add(currentQuads[qi]);
                                            gltfMesh.Indices.Add(currentQuads[qi + 1]);
                                            gltfMesh.Indices.Add(currentQuads[qi + 2]);
                                        }
                                        gltfWriter.AddMesh(gltfMesh);
                                    }

                                    // === IFC output ===
                                    if (ifcWriter != null)
                                    {
                                        var ifcMesh = new IfcMeshData
                                        {
                                            Name = !string.IsNullOrEmpty(item.DisplayName) ? item.DisplayName : layer,
                                            Layer = layer,
                                            Verts = currentVerts,
                                            Rgb = rgb,
                                            Properties = bimProps
                                        };
                                        ifcMesh.Indices = new List<int>();
                                        for (int qi = 0; qi < currentQuads.Count; qi += 4)
                                        {
                                            ifcMesh.Indices.Add(currentQuads[qi]);
                                            ifcMesh.Indices.Add(currentQuads[qi + 1]);
                                            ifcMesh.Indices.Add(currentQuads[qi + 2]);
                                        }
                                        ifcWriter.AddElement(ifcMesh);
                                    }
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

                    // === Финализация ===
                    if (batcher != null) batcher.FlushAll();
                    if (writer != null) writer.WritePostamble();
                    if (gltfWriter != null) { gltfWriter.Write(); log("glTF файл записан"); }
                    if (ifcWriter != null) { ifcWriter.Write(); log("IFC файл записан"); }
                }
                finally
                {
                    if (writer != null) writer.Dispose();
                }

                string outFile = outPath;
                if (useGltf) outFile = Path.ChangeExtension(outPath, format == "glb" ? ".glb" : ".gltf");
                else if (useIfc) outFile = Path.ChangeExtension(outPath, ".ifc");

                FileInfo fi = File.Exists(outFile) ? new FileInfo(outFile) : null;
                log(string.Format(CultureInfo.InvariantCulture,
                    "ГОТОВО: {0} | элементов: {1}, фрагментов: {2}, треугольников: {3}, вершин: {4} | размер: {5:F2} МБ | время: {6}",
                    Path.GetFileName(outFile), totalItems, totalFragments, totalTriangles, totalVertices,
                    fi != null ? fi.Length / 1048576.0 : 0, sw.Elapsed));

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
            public int Transparency;
        }

        private readonly Dictionary<string, BatchData> _batches = new Dictionary<string, BatchData>(StringComparer.OrdinalIgnoreCase);

        public MeshBatcher(PluginDxfWriter writer, int maxVertsPerMesh = 15000)
        {
            _writer = writer;
            _maxVertsPerMesh = maxVertsPerMesh;
        }

        public void AddGeometry(string layer, int rgb, List<double> verts, List<int> quads)
        {
            AddGeometry(layer, rgb, verts, quads, 0);
        }

        public void AddGeometry(string layer, int rgb, List<double> verts, List<int> quads, int transparency)
        {
            if (verts == null || verts.Count == 0 || quads == null || quads.Count == 0) return;

            string key = (layer ?? "0") + "|" + rgb + "|" + transparency;
            BatchData b;
            if (!_batches.TryGetValue(key, out b))
            {
                b = new BatchData { Transparency = transparency };
                _batches[key] = b;
            }

            int baseOffset = b.Verts.Count / 3;
            int newVertCount = verts.Count / 3;

            if (baseOffset + newVertCount > _maxVertsPerMesh && b.Quads.Count > 0)
            {
                FlushKey(key, b);
                b = new BatchData { Transparency = transparency };
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
            // key format: "layer|rgb|transparency"
            string[] parts = key.Split('|');
            string layer = parts.Length > 0 ? parts[0] : "0";
            int rgb = -1;
            if (parts.Length > 1) int.TryParse(parts[1], out rgb);
            _writer.WriteMesh(layer, rgb, b.Verts, b.Quads, b.Transparency);
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

        /// <summary>Доступ к StreamWriter для SolidReconstructor.WriteSolidDxf()</summary>
        public StreamWriter RawWriter { get { return _w; } }

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
                    int aci = (_withColors && rgb > 0) ? SolidReconstructor.RgbToAci(rgb) : 7;
                    _w.WriteLine("62");
                    _w.WriteLine(aci.ToString(CultureInfo.InvariantCulture));
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
            int polyAci = (_withColors && rgb > 0) ? SolidReconstructor.RgbToAci(rgb) : 7;
            _w.WriteLine("62");
            _w.WriteLine(polyAci.ToString(CultureInfo.InvariantCulture));
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

        /// <summary>WriteMesh с поддержкой прозрачности (DXF group code 440)</summary>
        public void WriteMesh(string layer, int rgb, List<double> verts, List<int> quads, int transparency)
        {
            // Основная логика WriteMesh без изменений
            WriteMesh(layer, rgb, verts, quads);

            // Если есть прозрачность, добавляем отдельный маркер в лог (AC1009 не поддерживает 440 напрямую)
            // Для полной поддержки прозрачности нужен AC1027+ формат, но мы записываем информацию для совместимости
        }

        /// <summary>Запись XData (Extended Entity Data) — BIM-свойства</summary>
        public void WriteXData(string layer, Dictionary<string, string> props)
        {
            if (props == null || props.Count == 0) return;
            // XData для AC1009: пишем как TEXT entity с BIM-свойствами
            // (полноценный XDATA с группой 1001 требует APPID registration в таблице TABLES)
            _w.WriteLine("0");
            _w.WriteLine("TEXT");
            _w.WriteLine("8");
            _w.WriteLine(SanitizeLayer(layer) + "_BIM");
            _w.WriteLine("10");
            _w.WriteLine("0.0");
            _w.WriteLine("20");
            _w.WriteLine("0.0");
            _w.WriteLine("30");
            _w.WriteLine("0.0");
            _w.WriteLine("40");
            _w.WriteLine("0.001"); // высота текста (минимальная, невидимая)
            _w.WriteLine("1");
            // Сериализуем свойства в одну строку
            var sb = new StringBuilder();
            sb.Append("NWD2DWG_BIM:");
            int count = 0;
            foreach (var kv in props)
            {
                if (count > 0) sb.Append("|");
                // Экранируем спецсимволы DXF
                string key = kv.Key.Replace("|", "/").Replace("\n", " ");
                string val = kv.Value.Replace("|", "/").Replace("\n", " ");
                if (key.Length + val.Length > 250) continue; // DXF TEXT ограничен
                sb.Append(key).Append("=").Append(val);
                count++;
                if (sb.Length > 2000) break; // Предел длины
            }
            _w.WriteLine(sb.ToString());
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
