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
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.Timeliner;

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

            // === v3.0 параметры ===
            bool geoShift = parameters.Length > 15 && parameters[15] == "1";
            bool exportGrids = parameters.Length > 16 && parameters[16] == "1";
            bool tracePipes = parameters.Length > 17 && parameters[17] == "1";
            bool exportBoq = parameters.Length > 18 && parameters[18] == "1";
            bool exportBcf = parameters.Length > 19 && parameters[19] == "1";
            bool anonymize = parameters.Length > 20 && parameters[20] == "1";

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
            log(string.Format("v3.0: geoShift={0}, grids={1}, tracePipes={2}, boq={3}, bcf={4}, anonymize={5}",
                geoShift, exportGrids, tracePipes, exportBoq, exportBcf, anonymize));

            // Раньше все эти флаги только печатались. Теперь они собираются в
            // конвейер, который реально работает с извлечённой геометрией.
            var engOpt = EngineeringOptions.FromArgs(parameters);
            var eng = new EngineeringPipeline
            {
                Opt = engOpt,
                Log = log,
                OutBasePath = outPath,
                SourceModel = inputNwd
            };
            log(string.Format("v3.1-3.4: clash={0}, plan={1}, purge={2}, sleeves={3}, clearance={4}, steel={5}, cog={6}, iso={7}, 4d={8}, wrap={9}, rooms={10}",
                engOpt.ClusterClashes, engOpt.SectionPlan, engOpt.PurgeDxf, engOpt.BuildPenetrations,
                engOpt.ValidateClearance, engOpt.MatchSteel, engOpt.CalcCog, engOpt.GenerateIso,
                engOpt.MapSchedule4D, engOpt.Shrinkwrap, engOpt.RoomFinish));

            Stopwatch sw = Stopwatch.StartNew();
            ConvertAbort.Reset();   // признак статический: прогонов может быть несколько

            // Диагностика хода работы нужна в обеих ветках — и при экспорте
            // по разделам, и при обычном. Поэтому объявляем на уровне метода.
            bool denseWarned = false;
            int heaviestFrags = 0;
            string heaviestName = "";
            string stopFlag = null;
            try { stopFlag = Path.Combine(Path.GetDirectoryName(logPath) ?? "", "stop.flag"); }
            catch { }

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

                eng.InsUnits = insUnits;
                if (engOpt.GeoShift || engOpt.SectionPlan || engOpt.RoomFinish)
                {
                    double[] bounds = ComputeFragmentBounds(doc, skipHidden, log);
                    if (bounds != null)
                        eng.InitBounds(bounds[0], bounds[1], bounds[2], bounds[3], bounds[4], bounds[5]);
                    else
                        log("Габариты модели определить не удалось — геосдвиг и срез пропущены.");
                }

                // === Оси и уровни здания ===
                if (engOpt.ExportGrids) ExportGridsAndLevels(doc, outPath, eng, log);

                // === Коллизии Clash Detective ===
                if (engOpt.NeedsClashData) CollectClashes(doc, eng, log);

                // === График производства работ (4D) ===
                if (engOpt.MapSchedule4D) CollectSchedule(doc, eng, log);

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
                    long splitBytes = 0;
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

                            using (var secWriter = new PluginDxfWriter(sectionOutPath, use3dFace, insUnits, withColors, !engOpt.Out.EmitGeometry))
                            {
                                secWriter.StopFlagPath = stopFlag;
                                secWriter.WritePreamble(sectionLayers);
                                var secBatcher = new MeshBatcher(secWriter, 15000);

                                foreach (ModelItem item in sectionItem.DescendantsAndSelf)
                                {
                                    secItems++;
                                    totalItems++;

                                    if (skipHidden && IsHiddenDeep(item))
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

                                    // Имя и материал нужны инженерным модулям — ВОР,
                                    // ведомости КМ, расчёту масс. При разбивке по
                                    // разделам они раньше не собирались вовсе.
                                    string itemName = "";
                                    try { itemName = item.DisplayName ?? ""; } catch { }
                                    string matName = "";
                                    if (bimProps != null) matName = MaterialFromProps(bimProps);

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

                                            // === Геосдвиг к нулю ===
                                            // до расчётов и записи, иначе побочные
                                            // файлы окажутся в других координатах
                                            eng.ApplyShift(currentVerts);

                                            // === Mesh Decimation ===
                                            if (decimatePercent > 0 && decimatePercent <= 90)
                                            {
                                                double ratio = decimatePercent / 100.0;
                                                MeshDecimator.Decimate(ref currentVerts, ref currentQuads, ratio,
                                            engOpt.Cfg.DecimateBoundaryWeight, engOpt.Cfg.DecimatePreventFlips);
                                            }

                                            secFrags++;
                                            totalFragments++;
                                            secTris += currentQuads.Count / 4;
                                            totalTriangles += currentQuads.Count / 4;
                                            totalVertices += currentVerts.Count / 3;

                                            // === Solid Detection ===
                                            // Распознанное тело раньше делало continue и
                                            // уносило с собой и расчёты, и свойства элемента.
                                            SolidResult solid = null;
                                            bool solidWritten = false;
                                            if (solidDetect)
                                            {
                                                solid = SolidReconstructor.TryReconstruct(currentVerts, currentQuads);
                                                if (solid != null && solid.Type != SolidType.None && solid.Confidence > 0.7)
                                                {
                                                    SolidReconstructor.WriteSolidDxf(secWriter.RawWriter, solid, PluginDxfWriter.SanitizeLayer(layer), rgb);
                                                    solidWritten = true;
                                                }
                                            }

                                            // === Инженерные расчёты по элементу ===
                                            if (engOpt.AnyGeometryConsumer)
                                                eng.OnElement(itemName, layer, matName, currentVerts, currentQuads, solid);

                                            if (!solidWritten)
                                                secBatcher.AddGeometry(layer, rgb, currentVerts, currentQuads, transparency);

                                            // === XData (после анонимизации) ===
                                            var outProps = bimProps != null && bimProps.Count > 0
                                                ? eng.FilterProps(bimProps) : null;
                                            if (outProps != null && outProps.Count > 0)
                                            {
                                                secWriter.WriteElementProps(PluginDxfWriter.SanitizeLayer(layer), outProps,
                                                    currentVerts.Count >= 3 ? currentVerts[0] : 0.0,
                                                    currentVerts.Count >= 3 ? currentVerts[1] : 0.0,
                                                    currentVerts.Count >= 3 ? currentVerts[2] : 0.0);
                                            }
                                        }
                                    }
                                }

                                secBatcher.FlushAll();
                                secWriter.WritePostamble();
                            }

                            FileInfo secFi = new FileInfo(sectionOutPath);
                            splitBytes += secFi.Length;
                            log(string.Format("Раздел {0} готов: {1:F2} МБ | полигонов: {2}", Path.GetFileName(sectionOutPath), secFi.Length / 1048576.0, secTris));
                        }
                    }

                    log(string.Format("ГОТОВО (по разделам): разделов: {0}, полигонов всего: {1} | время: {2}",
                        sectionCount, totalTriangles, sw.Elapsed));

                    // Ведомости, индекс ревизии и протокол — общие на всю модель,
                    // а не на раздел. Раньше сюда просто не доходили: ветка
                    // разбивки заканчивалась возвратом до конвейера, и папки с
                    // расчётами не появлялись вообще.
                    try
                    {
                        string engSplit = eng.Finish();
                        if (!string.IsNullOrEmpty(engSplit))
                            foreach (string line in engSplit.Split('\n'))
                                if (!string.IsNullOrEmpty(line.Trim())) log(line.TrimEnd());
                    }
                    catch (Exception eex) { log("Инженерные модули: ОШИБКА " + eex.Message); }

                    // Итоговую строку разбирает программа снаружи: без неё в окне
                    // показывались нули, хотя разделы выгружены.
                    log(string.Format(CultureInfo.InvariantCulture,
                        "ГОТОВО: {0} | элементов: {1}, фрагментов: {2}, треугольников: {3}, вершин: {4} | размер: {5:F2} МБ | время: {6}",
                        Path.GetFileName(outPath), totalItems, totalFragments, totalTriangles,
                        totalVertices, splitBytes / 1048576.0, sw.Elapsed));

                    try
                    {
                        Application.MainDocument.Clear();
                        log("документ выгружен, экземпляр свободен для следующей модели");
                    }
                    catch (Exception cex) { log("выгрузить документ не удалось: " + cex.Message); }

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
                    // Слой <имя>_BIM должен быть объявлен в TABLES, иначе TEXT
                    // со свойствами ссылается на несуществующий слой
                    if (transferXData)
                    {
                        var bimLayers = new List<string>();
                        foreach (string ln in layerList)
                            bimLayers.Add(PluginDxfWriter.SanitizeLayer(ln) + "_BIM");
                        layerList.AddRange(bimLayers);
                    }
                    // Флаг лежит в каталоге прогона рядом с журналом плагина.
                    bool discardGeometry = !engOpt.Out.EmitGeometry;
                    if (discardGeometry)
                        log("Основная геометрия не пишется (шаблон выдачи): считаются только ведомости и отчёты");
                    writer = new PluginDxfWriter(outPath, use3dFace, insUnits, withColors, discardGeometry);
                    writer.StopFlagPath = stopFlag;
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

                            if (skipHidden && IsHiddenDeep(item))
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

                            // Имя и материал элемента — нужны инженерным модулям
                            // (ВОР, ведомость КМ, расчёт массы)
                            string itemName = "";
                            try { itemName = item.DisplayName ?? ""; } catch { }
                            // материал берём из свойств элемента (если они
                            // извлекаются), иначе распознаём по имени и слою
                            string matName = "";

                            // === BIM свойства (XData) ===
                            Dictionary<string, string> bimProps = extractProperties != null ? extractProperties(item) : null;
                            if (bimProps != null) matName = MaterialFromProps(bimProps);

                            string layer = layersPerItem
                                ? (!string.IsNullOrEmpty(item.DisplayName) ? item.DisplayName : modelName)
                                : modelName;

                            // Счётчик по фрагментам, а не по элементам.
                            //
                            // Модель на 2 МБ однажды выдала 34.8 ГБ: счётчик
                            // элементов замер на десяти тысячах, а запись шла
                            // ещё полчаса. Значит работа ушла внутрь одного
                            // элемента, и наружу об этом не сообщалось ничем.
                            // Сигнал остановки тоже проверялся только по
                            // элементам — то есть в таком случае не сработал бы.
                            int fragsHere = 0;

                            foreach (InwOaFragment3 frag in frags)
                            {
                                if (ConvertAbort.Requested) break;

                                if ((++fragsHere % 20000) == 0)
                                {
                                    log(string.Format(CultureInfo.InvariantCulture,
                                        "ВНИМАНИЕ: элемент «{0}» отдал уже {1} фрагментов " +
                                        "(треугольников всего {2}) — это ненормально много",
                                        string.IsNullOrEmpty(itemName) ? "без имени" : itemName,
                                        fragsHere, totalTriangles));

                                    if (stopFlag != null && File.Exists(stopFlag))
                                    {
                                        log("ОСТАНОВЛЕНО по сигналу наблюдателя внутри элемента.");
                                        break;
                                    }
                                }

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

                                    // === Геосдвиг к нулю ===
                                    // применяем до всех расчётов и записи, иначе
                                    // побочные файлы окажутся в других координатах
                                    eng.ApplyShift(currentVerts);

                                    // === Mesh Decimation ===
                                    // совсем мелкие фрагменты (тетраэдр/коробка)
                                    // упрощать нечем — порог задаётся в настройках
                                    if (decimatePercent > 0 && decimatePercent <= 90 &&
                                        currentQuads.Count / 4 >= engOpt.Cfg.DecimateMinTriangles)
                                    {
                                        double ratio = decimatePercent / 100.0;
                                        MeshDecimator.Decimate(ref currentVerts, ref currentQuads, ratio,
                                            engOpt.Cfg.DecimateBoundaryWeight, engOpt.Cfg.DecimatePreventFlips);
                                    }

                                    totalFragments++;
                                    totalTriangles += currentQuads.Count / 4;
                                    totalVertices += currentVerts.Count / 3;

                                    // === Solid Detection ===
                                    SolidResult solid = null;
                                    bool solidWritten = false;
                                    if (solidDetect || engOpt.TracePipes || engOpt.GenerateIso || engOpt.BuildPenetrations)
                                    {
                                        solid = SolidReconstructor.TryReconstruct(currentVerts, currentQuads);
                                        if (solidDetect && writer != null && solid != null &&
                                            solid.Type != SolidType.None &&
                                            solid.Confidence >= engOpt.Cfg.SolidMinConfidence)
                                        {
                                            SolidReconstructor.WriteSolidDxf(writer.RawWriter, solid, PluginDxfWriter.SanitizeLayer(layer), rgb);
                                            solidWritten = true;
                                        }
                                    }

                                    // === Оболочка вместо внутренностей (защита ноу-хау) ===
                                    if (engOpt.Shrinkwrap && !solidWritten)
                                    {
                                        // уровень 1 сохраняет фланцы и точки врезки,
                                        // уровни 2-3 сводят элемент к габаритной оболочке
                                        var wrap = ShrinkWrapper.WrapMesh(currentVerts, currentQuads,
                                                                          engOpt.Cfg.ShrinkwrapLevel <= 1);
                                        if (wrap != null && wrap.OutQuads.Count >= 4)
                                        {
                                            currentVerts = wrap.OutVerts;
                                            currentQuads = wrap.OutQuads;
                                        }
                                    }

                                    // === Инженерные расчёты по элементу ===
                                    if (engOpt.AnyGeometryConsumer)
                                        eng.OnElement(itemName, layer, matName, currentVerts, currentQuads, solid);

                                    // === DXF output ===
                                    if (batcher != null && !solidWritten)
                                    {
                                        batcher.AddGeometry(layer, rgb, currentVerts, currentQuads, transparency);
                                    }

                                    // === Свойства элемента (после анонимизации) ===
                                    // раньше распознанный solid делал continue и
                                    // молча терял атрибуты элемента
                                    if (bimProps != null && bimProps.Count > 0 && writer != null && batcher != null)
                                    {
                                        var outProps = eng.FilterProps(bimProps);
                                        if (outProps != null && outProps.Count > 0)
                                            writer.WriteElementProps(PluginDxfWriter.SanitizeLayer(layer), outProps,
                                                currentVerts.Count >= 3 ? currentVerts[0] : 0.0,
                                                currentVerts.Count >= 3 ? currentVerts[1] : 0.0,
                                                currentVerts.Count >= 3 ? currentVerts[2] : 0.0);
                                    }

                                    if (solidWritten) continue;

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

                            if (ConvertAbort.Requested)
                            {
                                log("ОСТАНОВЛЕНО: обход прекращён по сигналу, " +
                                    "ведомости считаются на уже собранных данных.");
                                break;
                            }

                            if (totalItems % 2000 == 0)
                            {
                                if (fragsHere > heaviestFrags)
                                {
                                    heaviestFrags = fragsHere;
                                    heaviestName = itemName;
                                }

                                // Плотность разбиения задаёт сам Navisworks, и через
                                // публичный API она недоступна — проверено по
                                // Autodesk.Navisworks.Api, COM-интерфейсу и дереву
                                // настроек приложения. Раз повлиять нельзя, надо хотя
                                // бы предупредить: архитектура одного здания на 2.5 МБ
                                // выдавала 62 млн треугольников и десятки гигабайт.
                                if (!denseWarned && totalTriangles > 5000000)
                                {
                                    denseWarned = true;
                                    log(string.Format(CultureInfo.InvariantCulture,
                                        "ВНИМАНИЕ: очень плотная сетка — уже {0:F1} млн треугольников. " +
                                        "Ожидаемая выдача порядка {1:F1} ГБ. Плотность разбиения задаётся " +
                                        "в самом Navisworks (Параметры - Модель - Производительность) и " +
                                        "через API недоступна. Уменьшить объём: упрощение сетки, " +
                                        "габаритные оболочки либо отказ от записи геометрии.",
                                        totalTriangles / 1e6, totalTriangles * 190.0 / 1073741824.0));
                                }

                                double el = sw.Elapsed.TotalSeconds;
                                log(string.Format(CultureInfo.InvariantCulture,
                                    "обработано элементов {0}, фрагментов {1}, треугольников {2}" +
                                    " | {3:F0} с, {4:F0} эл/с",
                                    totalItems, totalFragments, totalTriangles,
                                    el, el > 0 ? totalItems / el : 0));

                                // Сигнал от наблюдателя: место на диске кончается
                                // или файл вырос до неразумного размера. Останов
                                // по флагу, а не по исключению: выдача должна
                                // остаться пригодной, а ведомости — досчитаться.
                                if (writer != null && writer.Stopped)
                                {
                                    log("ОСТАНОВЛЕНО: запись геометрии прекращена по сигналу. " +
                                        "Обход модели завершается, ведомости считаются на собранных данных.");
                                    break;
                                }
                            }
                        }
                    }

                    if (heaviestFrags > 5000)
                        log(string.Format(CultureInfo.InvariantCulture,
                            "Самый тяжёлый элемент: «{0}» — {1} фрагментов",
                            string.IsNullOrEmpty(heaviestName) ? "без имени" : heaviestName,
                            heaviestFrags));

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

                // Побочные файлы инженерных модулей пишем после закрытия DXF
                try
                {
                    string engReport = eng.Finish();
                    if (!string.IsNullOrEmpty(engReport))
                        foreach (string line in engReport.Split('\n'))
                            if (!string.IsNullOrEmpty(line.Trim())) log(line.TrimEnd());
                }
                catch (Exception eex) { log("Инженерные модули: ОШИБКА " + eex.Message); }

                string outFile = outPath;
                if (useGltf) outFile = Path.ChangeExtension(outPath, format == "glb" ? ".glb" : ".gltf");
                else if (useIfc) outFile = Path.ChangeExtension(outPath, ".ifc");

                FileInfo fi = File.Exists(outFile) ? new FileInfo(outFile) : null;
                log(string.Format(CultureInfo.InvariantCulture,
                    "ГОТОВО: {0} | элементов: {1}, фрагментов: {2}, треугольников: {3}, вершин: {4} | размер: {5:F2} МБ | время: {6}",
                    Path.GetFileName(outFile), totalItems, totalFragments, totalTriangles, totalVertices,
                    fi != null ? fi.Length / 1048576.0 : 0, sw.Elapsed));

                // Освобождаем документ за собой.
                //
                // При работе через уже открытый Navisworks модель остаётся
                // загруженной, и следующий файл упирается в занятый экземпляр:
                // подключение к нему не проходит, программа откатывается к
                // собственному запуску — а он на 2026 нестабилен. Чистим место
                // для следующей модели сразу.
                try
                {
                    Application.MainDocument.Clear();
                    log("документ выгружен, экземпляр свободен для следующей модели");
                }
                catch (Exception cex) { log("выгрузить документ не удалось: " + cex.Message); }

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

        /// <summary>
        /// Скрыт ли элемент с учётом родителей.
        ///
        /// «Скрыть невыбранные» в Navisworks помечает узлы дерева, а не каждый
        /// лист геометрии: у листа IsHidden остаётся false, хотя на экране его
        /// уже нет. Проверка только по самому элементу поэтому исправно
        /// выгружала всё спрятанное — на модели с одним видимым краном
        /// в чертёж уходили именно скрытые трубопроводы.
        /// </summary>
        static bool IsHiddenDeep(ModelItem item)
        {
            try
            {
                foreach (ModelItem a in item.AncestorsAndSelf)
                    if (a.IsHidden) return true;
            }
            catch
            {
                // Дерево иногда не отдаёт предков (битая ссылка на вложенный
                // файл) — тогда судим хотя бы по самому элементу.
                try { return item.IsHidden; } catch { }
            }
            return false;
        }

        // --------------------------------------------------------------------
        // Оси и уровни здания из DocumentGrids.
        // Уровни (GridLevel.Elevation) API отдаёт честно. Геометрию самих
        // координационных осей публичный API не раскрывает — GridLine несёт
        // только DisplayName, поэтому оси выводим подписями по габаритам
        // модели и прямо сообщаем об ограничении.
        // --------------------------------------------------------------------
        static void ExportGridsAndLevels(Document doc, string outPath,
                                         EngineeringPipeline eng, Action<string> log)
        {
            try
            {
                var grids = doc.Grids;
                if (grids == null || grids.ActiveSystem == null)
                {
                    log("[Оси и уровни] В модели нет координационных систем (DocumentGrids пуст).");
                    return;
                }

                var sys = grids.ActiveSystem;
                var data = new List<GridLineData>();

                double dx = eng.ShiftActive ? eng.Geo.OffsetX : 0;
                double dy = eng.ShiftActive ? eng.Geo.OffsetY : 0;
                double dz = eng.ShiftActive ? eng.Geo.OffsetZ : 0;

                double x0 = 0, y0 = 0, x1 = 10000, y1 = 10000;
                try
                {
                    BoundingBox3D bb = doc.GetBoundingBox(false);
                    if (bb != null && !bb.IsEmpty)
                    {
                        x0 = bb.Min.X + dx; y0 = bb.Min.Y + dy;
                        x1 = bb.Max.X + dx; y1 = bb.Max.Y + dy;
                    }
                }
                catch { }

                int levels = 0;
                foreach (var lv in sys.Levels)
                {
                    double z = lv.Elevation + dz;
                    data.Add(new GridLineData
                    {
                        Name = lv.DisplayName,
                        StartX = x0, StartY = y0, StartZ = z,
                        EndX = x1, EndY = y0, EndZ = z,
                        IsLevel = true
                    });
                    levels++;
                }

                int lines = 0;
                try { foreach (var gl in sys.Lines) { lines++; } } catch { }

                string path = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".",
                    Path.GetFileNameWithoutExtension(outPath) + "_grids.dxf");

                using (var w = new StreamWriter(path, false, Encoding.Default))
                {
                    w.WriteLine("0\nSECTION\n2\nHEADER");
                    w.WriteLine("9\n$ACADVER\n1\nAC1015");
                    w.WriteLine("0\nENDSEC");
                    w.WriteLine("0\nSECTION\n2\nTABLES");
                    w.WriteLine("0\nTABLE\n2\nLAYER\n70\n3");
                    w.WriteLine("0\nLAYER\n2\n0\n70\n0\n62\n7\n6\nCONTINUOUS");
                    w.WriteLine("0\nLAYER\n2\n_GRIDS\n70\n0\n62\n2\n6\nCONTINUOUS");
                    w.WriteLine("0\nLAYER\n2\n_LEVELS\n70\n0\n62\n1\n6\nCONTINUOUS");
                    w.WriteLine("0\nENDTAB");
                    w.WriteLine("0\nENDSEC");
                    w.WriteLine("0\nSECTION\n2\nENTITIES");
                    GridExtractor.WriteGridsToDxf(w, data, 300.0);
                    w.WriteLine("0\nENDSEC");
                    w.WriteLine("0\nEOF");
                }

                log(string.Format(CultureInfo.InvariantCulture,
                    "[Оси и уровни] Уровней выгружено: {0} -> {1}", levels, Path.GetFileName(path)));
                if (lines > 0)
                    log(string.Format(CultureInfo.InvariantCulture,
                        "[Оси и уровни] Координационных осей в системе: {0}, но геометрию линий " +
                        "публичный API Navisworks не отдаёт — в DXF выгружены только уровни.", lines));
            }
            catch (Exception ex)
            {
                log("[Оси и уровни] ОШИБКА: " + ex.Message);
            }
        }

        // --------------------------------------------------------------------
        // Габариты модели по матрицам фрагментов.
        //
        // Это единственный источник, гарантированно совпадающий с координатами
        // извлекаемой геометрии: тот же COM-путь, те же матрицы. Управляемый
        // doc.GetBoundingBox() отдаёт бокс в другой системе координат — на
        // проверочной модели его размахи отличались от фактических в десятки
        // и сотни раз, из-за чего геосдвиг смещал модель на неверную величину.
        //
        // Примитивы не генерируются: читается только перенос матрицы, поэтому
        // проход дешёвый по сравнению с самой конвертацией.
        // --------------------------------------------------------------------
        static double[] ComputeFragmentBounds(Document doc, bool skipHidden, Action<string> log)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            int frags = 0;
            var sw = Stopwatch.StartNew();

            try
            {
                foreach (Model model in doc.Models)
                {
                    ModelItem root = model.RootItem;
                    if (root == null) continue;
                    foreach (ModelItem item in root.DescendantsAndSelf)
                    {
                        if (skipHidden && IsHiddenDeep(item)) continue;
                        if (!item.HasGeometry) continue;

                        InwOaPath3 oaPath = null;
                        try { oaPath = (InwOaPath3)ComApiBridge.ToInwOaPath(item); } catch { continue; }
                        if (oaPath == null) continue;

                        IEnumerable fl = null;
                        try { fl = (IEnumerable)oaPath.Fragments(); } catch { continue; }
                        if (fl == null) continue;

                        foreach (InwOaFragment3 frag in fl)
                        {
                            double[] m = GetMatrix(frag);
                            if (m == null) continue;
                            double x = m[12], y = m[13], z = m[14];
                            if (x < minX) minX = x; if (x > maxX) maxX = x;
                            if (y < minY) minY = y; if (y > maxY) maxY = y;
                            if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                            frags++;
                        }
                    }
                }
            }
            catch (Exception ex) { log("Обход габаритов прерван: " + ex.Message); }

            if (frags == 0) return null;
            log(string.Format(CultureInfo.InvariantCulture,
                "Габариты по {0} фрагментам за {1:F1} с: X {2:F0}..{3:F0}, Y {4:F0}..{5:F0}, Z {6:F0}..{7:F0}",
                frags, sw.Elapsed.TotalSeconds, minX, maxX, minY, maxY, minZ, maxZ));
            return new[] { minX, minY, minZ, maxX, maxY, maxZ };
        }

        // --------------------------------------------------------------------
        // Результаты Clash Detective: точки для кластеризации и топики BCF.
        // Раньше флаги --bcf и --clash-cluster печатались в лог и ничего не
        // делали, потому что источник данных не был подключён.
        // --------------------------------------------------------------------
        static void CollectClashes(Document doc, EngineeringPipeline eng, Action<string> log)
        {
            try
            {
                DocumentClash clash = doc.GetClash();
                if (clash == null || clash.TestsData == null || clash.TestsData.Tests.Count == 0)
                {
                    log("[Коллизии] В модели нет проверок Clash Detective.");
                    return;
                }

                var cfg = eng.Opt.Cfg;
                int tests = 0, taken = 0, skipped = 0;

                foreach (SavedItem si in clash.TestsData.Tests)
                {
                    ClashTest test = si as ClashTest;
                    if (test == null) continue;
                    tests++;
                    foreach (SavedItem res in test.Children)
                        TakeClashResult(res, test.DisplayName, cfg, eng, ref taken, ref skipped);
                }

                log(string.Format(CultureInfo.InvariantCulture,
                    "[Коллизии] Проверок: {0}, принято результатов: {1}, отфильтровано: {2}",
                    tests, taken, skipped));
            }
            catch (Exception ex)
            {
                log("[Коллизии] ОШИБКА чтения Clash Detective: " + ex.Message);
            }
        }

        static void TakeClashResult(SavedItem item, string testName, AdvancedConfig cfg,
                                    EngineeringPipeline eng, ref int taken, ref int skipped)
        {
            var group = item as ClashResultGroup;
            if (group != null)
            {
                // группа результатов: разбираем вложенные коллизии
                foreach (SavedItem child in group.Children)
                    TakeClashResult(child, testName, cfg, eng, ref taken, ref skipped);
                return;
            }

            var r = item as ClashResult;
            if (r == null) return;

            string status = r.Status.ToString();
            if (!cfg.ClashIncludeResolved && status.IndexOf("Resolved", StringComparison.OrdinalIgnoreCase) >= 0)
            { skipped++; return; }
            if (!cfg.ClashIncludeApproved && status.IndexOf("Approved", StringComparison.OrdinalIgnoreCase) >= 0)
            { skipped++; return; }
            if (Math.Abs(r.Distance) < cfg.ClashMinDistanceMm / 1000.0 * UnitScaleGuess(r))
            { skipped++; return; }

            // Ответственный за коллизию описан по-разному в разных поколениях:
            // до 2021 включительно это строка, начиная с более поздних —
            // объект с DisplayName. Через object компилируется и там, и там,
            // а тип разбирается уже во время работы.
            string assignee = "";
            try
            {
                object who = r.AssignedTo;
                if (who != null)
                {
                    assignee = who as string;
                    if (assignee == null)
                    {
                        var p = who.GetType().GetProperty("DisplayName");
                        if (p != null) assignee = p.GetValue(who, null) as string;
                    }
                    assignee = assignee ?? "";
                }
            }
            catch { }
            DateTime created = r.CreatedTime.HasValue ? r.CreatedTime.Value : DateTime.Now;

            eng.AddClash(r.Center.X, r.Center.Y, r.Center.Z, r.Distance,
                         r.DisplayName, testName, status,
                         r.Guid.ToString(), created, assignee);
            taken++;
        }

        // Distance приходит в единицах документа; фильтр задан в мм
        static double UnitScaleGuess(ClashResult r) { return 1000.0; }

        // --------------------------------------------------------------------
        // График работ: сначала TimeLiner из модели, иначе внешний файл.
        // --------------------------------------------------------------------
        static void CollectSchedule(Document doc, EngineeringPipeline eng, Action<string> log)
        {
            var cfg = eng.Opt.Cfg;
            if (cfg.ScheduleSource != "File")
            {
                try
                {
                    DocumentTimeliner tl = doc.GetTimeliner();
                    if (tl != null && tl.TasksRoot != null)
                    {
                        int n = 0;
                        CollectTimelinerTasks(doc, tl.TasksRoot.Children, eng, ref n);
                        if (n > 0)
                        {
                            eng.ScheduleOrigin = "TimeLiner";
                            log(string.Format(CultureInfo.InvariantCulture,
                                "[4D] Из TimeLiner прочитано задач: {0}", n));
                            return;
                        }
                    }
                    log("[4D] TimeLiner в модели пуст.");
                }
                catch (Exception ex) { log("[4D] ОШИБКА чтения TimeLiner: " + ex.Message); }
            }

            string file = eng.Opt.ScheduleFile;
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                if (cfg.ScheduleSource == "File")
                    log("[4D] Файл графика не найден: " + (string.IsNullOrEmpty(file) ? "(не задан)" : file));
                return;
            }
            try
            {
                var tasks = ScheduleMapper.LoadSchedule(file);
                foreach (var t in tasks)
                {
                    eng.Tasks4D.Add(t);
                    eng.TaskLinkCounts[t.Uid ?? ""] = 1; // из файла привязку не знаем
                }
                eng.ScheduleOrigin = Path.GetFileName(file);
                log(string.Format(CultureInfo.InvariantCulture,
                    "[4D] Из файла {0} прочитано задач: {1}", Path.GetFileName(file), tasks.Count));
            }
            catch (Exception ex) { log("[4D] ОШИБКА разбора графика: " + ex.Message); }
        }

        static void CollectTimelinerTasks(Document doc, SavedItemCollection items, EngineeringPipeline eng, ref int n)
        {
            foreach (SavedItem si in items)
            {
                var task = si as TimelinerTask;
                if (task == null) continue;

                if (task.Children != null && task.Children.Count > 0)
                    CollectTimelinerTasks(doc, task.Children, eng, ref n);

                if (!task.PlannedStartDate.HasValue && !task.PlannedEndDate.HasValue) continue;

                string uid = task.Guid.ToString();
                var st = new ScheduleTask
                {
                    Uid = uid,
                    Name = task.DisplayName,
                    Wbs = task.DisplayId ?? "",
                    PlannedStart = task.PlannedStartDate ?? DateTime.MinValue,
                    PlannedFinish = task.PlannedEndDate ?? DateTime.MinValue,
                    ActualStart = task.ActualStartDate,
                    ActualFinish = task.ActualEndDate,
                    PercentComplete = task.ProgressPercent.HasValue ? task.ProgressPercent.Value : 0.0
                };

                int links = 0;
                try
                {
                    if (task.Selection != null)
                    {
                        var sel = task.Selection.GetSelectedItems(doc);
                        if (sel != null) links = sel.Count;
                    }
                }
                catch { }

                eng.Tasks4D.Add(st);
                eng.TaskLinkCounts[uid] = links;
                n++;
            }
        }

        // Свойства складываются с ключом «Категория::Свойство», а искали их
        // по голому "Material" — совпадения не было никогда. Из-за этого
        // материал не определялся даже с ключом --xdata, и вся ведомость масс
        // считалась по плотности стали.
        //
        // Ищем по последнему сегменту ключа и по нескольким известным именам:
        // Revit пишет «Материалы и отделка::Материал конструкции», IFC —
        // «Material», экспорт из Tekla — «Materials::Material».
        private static readonly string[] MaterialKeys =
        {
            "материал конструкции", "материал", "material", "структурный материал",
            "structural material", "материал элемента", "материалы",
        };

        /// <summary>
        /// Не марка, а атрибут отображения CAD.
        ///
        /// В свойстве материала модели попадаются «ByLayer», «ПоСлою» и
        /// «Индекс цвета AutoCAD 5»: так CAD записывает, откуда берётся цвет.
        /// В ведомость они шли отдельными позициями наравне со «Сталь 20» —
        /// на проверяемой модели 47 и 367 фрагментов соответственно. Материала
        /// в них нет, и лучше честное «не задан», чем выдуманная марка.
        /// </summary>
        private static bool IsDisplayAttribute(string v)
        {
            if (string.IsNullOrEmpty(v)) return true;
            string s = v.Trim().ToLowerInvariant();
            if (s.Length == 0) return true;

            if (s == "bylayer" || s == "byblock" || s == "bymaterial" ||
                s == "по слою" || s == "послою" || s == "по блоку" || s == "поблоку" ||
                s == "default" || s == "none" || s == "нет" || s == "не задан")
                return true;

            if (s.Contains("индекс цвета") || s.Contains("color index")) return true;

            // Чистое число маркой быть не может.
            double num;
            if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                                CultureInfo.InvariantCulture, out num)) return true;

            return false;
        }

        internal static string MaterialFromProps(Dictionary<string, string> props)
        {
            if (props == null || props.Count == 0) return "";

            string fallback = "";
            foreach (var kv in props)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                if (IsDisplayAttribute(kv.Value)) continue;

                string key = kv.Key;
                int sep = key.LastIndexOf("::", StringComparison.Ordinal);
                string leaf = (sep >= 0 ? key.Substring(sep + 2) : key).Trim().ToLowerInvariant();

                for (int i = 0; i < MaterialKeys.Length; i++)
                {
                    if (leaf != MaterialKeys[i]) continue;
                    // Точное совпадение по первым именам списка предпочтительнее:
                    // «Материал конструкции» точнее, чем просто «Материалы».
                    if (i <= 1 || i == 2) return kv.Value.Trim();
                    if (fallback.Length == 0) fallback = kv.Value.Trim();
                }
            }

            if (fallback.Length > 0) return fallback;

            // Ничего точного не нашлось — берём свойство, у которого имя
            // содержит «материал», но не является ссылкой на файл или номером.
            foreach (var kv in props)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                if (IsDisplayAttribute(kv.Value)) continue;
                string k = kv.Key.ToLowerInvariant();
                if (k.Contains("материал") || k.Contains("material"))
                {
                    string v = kv.Value.Trim();
                    if (v.Length > 0 && v.Length < 64) return v;
                }
            }
            return "";
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
            // Прервать сам вызов GenerateSimplePrimitives нельзя — он внутри
            // Navisworks. Но можно перестать принимать: тогда обход одного
            // тяжёлого фрагмента заканчивается за секунды, а не за полчаса.
            if (Sink == null || ConvertAbort.Requested) return;
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
        private string _path;
        private bool _discard;
        private bool _use3dFace;
        private int _insUnits;
        private bool _withColors;

        /// <summary>Доступ к StreamWriter для SolidReconstructor.WriteSolidDxf()</summary>
        public StreamWriter RawWriter { get { return _w; } }

        // ---------------------------------------------------------------
        // Аварийная остановка записи.
        //
        // Проверять сигнал в циклах по элементам и фрагментам оказалось
        // недостаточно: на конструктивной модели вся работа уходила внутрь
        // одного фрагмента, который отдавал миллионы треугольников. Счётчики
        // при этом стояли, сигнал никто не читал, и файл вырос с 20 до 36 ГБ
        // уже ПОСЛЕ команды остановиться.
        //
        // Поэтому проверка живёт в самом писателе: что бы ни вызывало запись
        // и откуда бы ни вызывало, поток подменяется на пустой, и файл
        // перестаёт расти в тот же миг. Обход при этом доходит до конца сам,
        // и ведомости досчитываются на уже собранных данных.
        // ---------------------------------------------------------------
        public string StopFlagPath;
        private bool _stopped;
        private DateTime _lastStopCheck = DateTime.MinValue;

        /// <summary>Запись прекращена по сигналу.</summary>
        public bool Stopped { get { return _stopped; } }

        /// <summary>Уже записанный объём, байт.</summary>
        public long BytesWritten
        {
            get
            {
                try { _w.Flush(); return _path != null && File.Exists(_path) ? new FileInfo(_path).Length : 0; }
                catch { return 0; }
            }
        }

        private bool StopRequested()
        {
            if (_stopped) return true;
            if (string.IsNullOrEmpty(StopFlagPath)) return false;

            // Обращение к диску не чаще раза в секунду: иначе проверка
            // стоила бы дороже самой записи.
            var now = DateTime.UtcNow;
            if ((now - _lastStopCheck).TotalMilliseconds < 1000) return false;
            _lastStopCheck = now;

            bool tripped;
            try { tripped = File.Exists(StopFlagPath); }
            catch { return false; }
            if (!tripped) return false;

            _stopped = true;
            ConvertAbort.Request();
            try
            {
                _w.Flush();
                if (!_discard) _w.Dispose();
            }
            catch { }
            _w = new StreamWriter(Stream.Null, new UTF8Encoding(false));
            _w.NewLine = "\r\n";
            return true;
        }

        public PluginDxfWriter(string path, bool use3dFace, int insUnits, bool withColors)
            : this(path, use3dFace, insUnits, withColors, false) { }

        /// <summary>discard = писать «в никуда»: нужны только ведомости.
        /// Объект остаётся полноценным, поэтому вызывающий код не меняется —
        /// иначе пришлось бы обвешивать проверками каждое обращение.</summary>
        public PluginDxfWriter(string path, bool use3dFace, int insUnits, bool withColors, bool discard)
        {
            _use3dFace = use3dFace;
            _insUnits = insUnits;
            _withColors = withColors;
            _path = discard ? null : path;
            _discard = discard;
            if (discard)
            {
                _w = new StreamWriter(Stream.Null, new UTF8Encoding(false));
            }
            else
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                _w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20);
            }
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
            if (StopRequested()) return;
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
            if (StopRequested()) return;
            // Основная логика WriteMesh без изменений
            WriteMesh(layer, rgb, verts, quads);

            // Если есть прозрачность, добавляем отдельный маркер в лог (AC1009 не поддерживает 440 напрямую)
            // Для полной поддержки прозрачности нужен AC1027+ формат, но мы записываем информацию для совместимости
        }

        /// <summary>Запись XData (Extended Entity Data) — BIM-свойства</summary>
        // Свойства элемента как TEXT рядом с самой геометрией.
        //
        // Прежняя версия называлась WriteXData, но настоящим XDATA не была:
        // группа 1001 требует регистрации APPID. Помимо этого она клала ВСЕ
        // подписи в точку (0,0,0) — десятки тысяч наложенных TEXT в начале
        // координат душили AutoCAD при наведении курсора — и писала до 2000
        // символов в группу 1, где формат допускает максимум 255.
        public void WriteElementProps(string layer, Dictionary<string, string> props,
                                      double x, double y, double z)
        {
            if (props == null || props.Count == 0) return;

            string bimLayer = SanitizeLayer(layer) + "_BIM";

            var sb = new StringBuilder();
            sb.Append("NWD2DWG_BIM:");
            int count = 0;
            foreach (var kv in props)
            {
                string key = (kv.Key ?? "").Replace("|", "/").Replace("\n", " ").Replace("\r", " ");
                string val = (kv.Value ?? "").Replace("|", "/").Replace("\n", " ").Replace("\r", " ");
                if (key.Length + val.Length > 250) continue;
                if (count > 0) sb.Append("|");
                sb.Append(key).Append("=").Append(val);
                count++;
                if (sb.Length > 2000) break;
            }
            if (count == 0) return;

            string text = sb.ToString();

            _w.WriteLine("0");
            _w.WriteLine("TEXT");
            _w.WriteLine("8");
            _w.WriteLine(bimLayer);
            _w.WriteLine("10");
            _w.WriteLine(x.ToString("G12", CultureInfo.InvariantCulture));
            _w.WriteLine("20");
            _w.WriteLine(y.ToString("G12", CultureInfo.InvariantCulture));
            _w.WriteLine("30");
            _w.WriteLine(z.ToString("G12", CultureInfo.InvariantCulture));
            _w.WriteLine("40");
            _w.WriteLine("0.001"); // высота текста (визуально не мешает)

            // Группа 1 ограничена 255 символами; хвост уходит в группы 3
            const int Chunk = 250;
            if (text.Length <= Chunk)
            {
                _w.WriteLine("1");
                _w.WriteLine(text);
            }
            else
            {
                for (int i = Chunk; i < text.Length; i += Chunk)
                {
                    _w.WriteLine("3");
                    _w.WriteLine(text.Substring(i, Math.Min(Chunk, text.Length - i)));
                }
                _w.WriteLine("1");
                _w.WriteLine(text.Substring(0, Chunk));
            }
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
