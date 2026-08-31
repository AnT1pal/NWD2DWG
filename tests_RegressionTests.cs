// Регрессионные тесты на исправленные дефекты. Компилируется вместе с
// исходниками проекта (кроме NwdPlugin.cs), точка входа — TestMain.Run.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NWD2DWG;
using NWD2DWG.Plugin;

public static class TestMain
{
    static int _fail;
    static void Ok(string name) { Console.WriteLine("  [OK]   " + name); }
    static void Fail(string name, string why) { _fail++; Console.WriteLine("  [FAIL] " + name + " :: " + why); }
    static void Check(bool cond, string name, string why) { if (cond) Ok(name); else Fail(name, why); }

    [STAThread]
    public static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.GetTempPath();
        Directory.CreateDirectory(dir);
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("=== 1. DxfWriter.AddPolyface: границы чанка ===");
        TestPolyfaceChunking(dir);

        Console.WriteLine("=== 2. PrimitiveSink: сварка вершин ===");
        TestVertexWeld();

        Console.WriteLine("=== 3. CadPurger: кириллица и удаление мусора ===");
        TestPurger(dir);

        Console.WriteLine("=== 4. MeshDecimator: сжатие без разрушения ===");
        TestDecimator();

        Console.WriteLine("=== 5. Section2Plan: срез коробки ===");
        TestSectionPlan();

        Console.WriteLine("=== 6. ClashClusterer: кластеризация ===");
        TestClashClusterer(dir);

        Console.WriteLine("=== 7. CogCalculator: точность на больших координатах ===");
        TestCogPrecision();

        Console.WriteLine("=== 8. AdvancedConfig: сериализация и миграция ===");
        TestConfig(dir);

        Console.WriteLine("=== 9. Шаблоны по нормам ===");
        TestPresets();

        Console.WriteLine("=== 9б. Шаблон применяется целиком ===");
        TestPresetAppliesOutput();

        Console.WriteLine("=== 9в. Марки материалов ===");
        TestMaterialKey();

        Console.WriteLine("=== 9г. Ведомость в книге Excel ===");
        TestXlsx(dir);

        Console.WriteLine("=== 10. Профиль выдачи ===");
        TestOutputProfile(dir);

        Console.WriteLine("=== 11. Индекс ревизии и сравнение выдач ===");
        TestRevisionIndex(dir);

        Console.WriteLine("=== 11б. Перенос нуля площадки ===");
        TestBaseShift();

        Console.WriteLine("=== 12. Журнал выдач ===");
        TestDeliveryLog(dir);

        Console.WriteLine("=== 13. Маршрутизатор ИИ и хранение ключей ===");
        TestAiRouter(dir);

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "ВСЕ ТЕСТЫ ПРОЙДЕНЫ" : ("ПРОВАЛЕНО ТЕСТОВ: " + _fail));
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------
    // 1. Триангуляционный «суп» без общих вершин: 15000 треугольников =
    //    45000 уникальных вершин, что гарантированно переполняет чанк 30000.
    //    Раньше откат удалял из словаря уже использованные вершины, и индексы
    //    граней переставали соответствовать списку вершин.
    // ---------------------------------------------------------------------
    static void TestPolyfaceChunking(string dir)
    {
        const int tris = 15000;
        var verts = new List<double>(tris * 9);
        var quads = new List<int>(tris * 4);
        for (int i = 0; i < tris; i++)
        {
            double bx = i * 10.0;
            verts.Add(bx); verts.Add(0); verts.Add(0);
            verts.Add(bx + 1); verts.Add(0); verts.Add(0);
            verts.Add(bx); verts.Add(1); verts.Add(0);
            int b = i * 3;
            quads.Add(b); quads.Add(b + 1); quads.Add(b + 2); quads.Add(b + 2);
        }

        string path = Path.Combine(dir, "test_chunk.dxf");
        using (var w = new DxfWriter(path, 4))
        {
            w.BeginEntities(new[] { "TEST" });
            w.AddPolyface(verts, quads, "TEST", -1);
            w.Finish();
        }

        // Разбираем обратно и проверяем целостность индексов
        string[] lines = File.ReadAllLines(path);
        int meshes = 0, totalFaces = 0, badIndex = 0, badGeom = 0;
        var coords = new List<double[]>();
        int declaredVerts = 0, declaredFaces = 0;
        var seenTriangles = new HashSet<string>();

        for (int i = 0; i < lines.Length - 1; i++)
        {
            string c = lines[i].Trim(), v = lines[i + 1];
            if (c == "0" && v.Trim() == "POLYLINE")
            {
                if (meshes > 0) ValidateMesh(coords, declaredVerts, declaredFaces, ref badIndex);
                meshes++; coords.Clear(); declaredVerts = 0; declaredFaces = 0;
            }
            else if (c == "71" && declaredVerts == 0) int.TryParse(v.Trim(), out declaredVerts);
            else if (c == "72" && declaredFaces == 0) int.TryParse(v.Trim(), out declaredFaces);
        }

        // Полный разбор в один проход: собираем вершины и грани каждого меша
        var meshVerts = new List<double[]>();
        var meshFaces = new List<int[]>();
        int idx = 0;
        while (idx < lines.Length - 1)
        {
            string c = lines[idx].Trim();
            string v = lines[idx + 1];
            if (c == "0" && v.Trim() == "POLYLINE")
            {
                meshVerts.Clear(); meshFaces.Clear();
                idx += 2;
                int nv = 0;
                while (idx < lines.Length - 1)
                {
                    string cc = lines[idx].Trim(); string vv = lines[idx + 1];
                    if (cc == "71" && nv == 0) int.TryParse(vv.Trim(), out nv);
                    if (cc == "0" && (vv.Trim() == "SEQEND")) { idx += 2; break; }
                    if (cc == "0" && vv.Trim() == "VERTEX")
                    {
                        double x = 0, y = 0, z = 0; int flag = 0;
                        int[] f = new int[4];
                        int j = idx + 2;
                        while (j < lines.Length - 1)
                        {
                            string kc = lines[j].Trim(); string kv = lines[j + 1].Trim();
                            if (kc == "0") break;
                            if (kc == "10") double.TryParse(kv, NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                            if (kc == "20") double.TryParse(kv, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
                            if (kc == "30") double.TryParse(kv, NumberStyles.Float, CultureInfo.InvariantCulture, out z);
                            if (kc == "70") int.TryParse(kv, out flag);
                            if (kc == "71") int.TryParse(kv, out f[0]);
                            if (kc == "72") int.TryParse(kv, out f[1]);
                            if (kc == "73") int.TryParse(kv, out f[2]);
                            if (kc == "74") int.TryParse(kv, out f[3]);
                            j += 2;
                        }
                        if (flag == 192) meshVerts.Add(new[] { x, y, z });
                        else if (flag == 128) meshFaces.Add(f);
                        idx = j;
                        continue;
                    }
                    idx += 2;
                }

                if (nv != meshVerts.Count)
                    Console.WriteLine("     объявлено вершин " + nv + ", фактически " + meshVerts.Count);

                foreach (var f in meshFaces)
                {
                    totalFaces++;
                    for (int k = 0; k < 3; k++)
                    {
                        int fi = Math.Abs(f[k]);
                        if (fi < 1 || fi > meshVerts.Count) { badIndex++; goto nextFace; }
                    }
                    {
                        double[] a = meshVerts[Math.Abs(f[0]) - 1];
                        double[] b = meshVerts[Math.Abs(f[1]) - 1];
                        double[] cc2 = meshVerts[Math.Abs(f[2]) - 1];
                        // исходный треугольник: (bx,0,0) (bx+1,0,0) (bx,1,0)
                        bool okShape = Math.Abs(b[0] - a[0] - 1.0) < 1e-6 && Math.Abs(a[1]) < 1e-6 &&
                                       Math.Abs(cc2[0] - a[0]) < 1e-6 && Math.Abs(cc2[1] - 1.0) < 1e-6;
                        if (!okShape) badGeom++;
                        else seenTriangles.Add(a[0].ToString("F1", CultureInfo.InvariantCulture));
                    }
                nextFace: ;
                }
                continue;
            }
            idx += 2;
        }

        Check(meshes > 1, "меш разбит на несколько POLYLINE", "чанкование не сработало (meshes=" + meshes + ")");
        Check(badIndex == 0, "все индексы граней внутри списка вершин", "битых индексов: " + badIndex);
        Check(badGeom == 0, "геометрия граней не искажена", "искажённых треугольников: " + badGeom);
        Check(totalFaces == tris, "сохранены все грани", "записано " + totalFaces + " из " + tris);
        Check(seenTriangles.Count == tris, "нет потерянных/склеенных треугольников",
              "уникальных треугольников " + seenTriangles.Count + " из " + tris);
    }

    static void ValidateMesh(List<double[]> coords, int dv, int df, ref int bad) { }

    // ---------------------------------------------------------------------
    // 2. Сварка вершин: одинаковые координаты должны склеиваться,
    //    разные — никогда (проверка координат при коллизии хеша).
    // ---------------------------------------------------------------------
    static void TestVertexWeld()
    {
        var sink = new PrimitiveSink();
        var m = new double[16];
        for (int i = 0; i < 16; i++) m[i] = (i % 5 == 0) ? 1.0 : 0.0;
        sink.Reset(m);

        var rnd = new Random(12345);
        int n = 20000;
        var pts = new List<double[]>();
        for (int i = 0; i < n; i++)
            pts.Add(new[] { Math.Round(rnd.NextDouble() * 1e6, 3),
                            Math.Round(rnd.NextDouble() * 1e6, 3),
                            Math.Round(rnd.NextDouble() * 1e6, 3) });

        // каждый треугольник из трёх разных точек, каждая точка встречается дважды
        for (int i = 0; i + 2 < n; i += 3)
        {
            sink.Handle("Triangle", new object[] { new TV(pts[i]), new TV(pts[i + 1]), new TV(pts[i + 2]) });
            sink.Handle("Triangle", new object[] { new TV(pts[i]), new TV(pts[i + 2]), new TV(pts[i + 1]) });
        }

        int uniq = sink.Verts.Count / 3;
        Check(uniq == (n / 3) * 3, "повторные вершины склеены",
              "уникальных вершин " + uniq + ", ожидалось " + ((n / 3) * 3));

        // ни одна пара сохранённых вершин не должна совпадать по координатам
        var set = new HashSet<string>();
        int dup = 0;
        for (int i = 0; i < sink.Verts.Count; i += 3)
        {
            string k = sink.Verts[i] + "|" + sink.Verts[i + 1] + "|" + sink.Verts[i + 2];
            if (!set.Add(k)) dup++;
        }
        Check(dup == 0, "нет вершин-дублей", "дублей: " + dup);
        Console.WriteLine("     коллизий хеша обработано: " + sink.HashCollisions);
    }

    class TV
    {
        readonly Array _c;
        public TV(double[] p)
        {
            var a = Array.CreateInstance(typeof(float), new[] { 3 }, new[] { 1 });
            a.SetValue((float)p[0], 1); a.SetValue((float)p[1], 2); a.SetValue((float)p[2], 3);
            _c = a;
        }
        public Array coord { get { return _c; } }
    }

    // ---------------------------------------------------------------------
    // 3. CadPurger: кириллица должна выжить, пустые слои — исчезнуть
    // ---------------------------------------------------------------------
    static void TestPurger(string dir)
    {
        string src = Path.Combine(dir, "test_purge_in.dxf");
        var enc = Encoding.GetEncoding(1251);
        var sb = new StringBuilder();
        sb.Append("0\r\nSECTION\r\n2\r\nHEADER\r\n9\r\n$ACADVER\r\n1\r\nAC1015\r\n0\r\nENDSEC\r\n");
        sb.Append("0\r\nSECTION\r\n2\r\nTABLES\r\n0\r\nTABLE\r\n2\r\nLAYER\r\n70\r\n4\r\n");
        sb.Append("0\r\nLAYER\r\n2\r\n0\r\n70\r\n0\r\n62\r\n7\r\n");
        sb.Append("0\r\nLAYER\r\n2\r\nАР_Стены_Капитальные\r\n70\r\n0\r\n62\r\n3\r\n");
        sb.Append("0\r\nLAYER\r\n2\r\nПУСТОЙ_МУСОРНЫЙ_СЛОЙ\r\n70\r\n0\r\n62\r\n1\r\n");
        sb.Append("0\r\nLAYER\r\n2\r\nUNUSED_JUNK\r\n70\r\n0\r\n62\r\n2\r\n");
        sb.Append("0\r\nENDTAB\r\n0\r\nENDSEC\r\n");
        sb.Append("0\r\nSECTION\r\n2\r\nENTITIES\r\n");
        sb.Append("0\r\nTEXT\r\n8\r\nАР_Стены_Капитальные\r\n10\r\n0.0\r\n20\r\n0.0\r\n30\r\n0.0\r\n40\r\n2.5\r\n1\r\nСтена наружная кирпичная\r\n");
        sb.Append("0\r\nENDSEC\r\n0\r\nEOF\r\n");
        File.WriteAllText(src, sb.ToString(), enc);

        string outp = Path.Combine(dir, "test_purge_out.dxf");
        string report = CadPurger.Purge(src, outp);
        Console.WriteLine("     " + report.Trim());

        string res = File.ReadAllText(outp, enc);
        Check(res.Contains("АР_Стены_Капитальные"), "кириллический слой сохранён", "слой потерян или испорчен");
        Check(res.Contains("Стена наружная кирпичная"), "кириллический текст сохранён", "текст испорчен");
        Check(!res.Contains("ПУСТОЙ_МУСОРНЫЙ_СЛОЙ"), "неиспользуемый слой удалён", "мусорный слой остался");
        Check(!res.Contains("UNUSED_JUNK"), "второй неиспользуемый слой удалён", "UNUSED_JUNK остался");
        Check(res.Contains("\r\n0\r\n") && res.TrimEnd().EndsWith("EOF"), "структура DXF цела", "нет EOF");

        // если чистить нечего — файл не должен переписываться
        string clean = Path.Combine(dir, "test_purge_clean.dxf");
        File.Copy(outp, clean, true);
        var beforeWrite = File.GetLastWriteTimeUtc(clean);
        long beforeLen = new FileInfo(clean).Length;
        System.Threading.Thread.Sleep(1100);
        string rep2 = CadPurger.Purge(clean);
        Console.WriteLine("     " + rep2.Trim());
        Check(rep2.Contains("не найдено"), "повторная чистка распознаёт, что удалять нечего", rep2);
        Check(File.GetLastWriteTimeUtc(clean) == beforeWrite && new FileInfo(clean).Length == beforeLen,
              "файл не переписан впустую", "файл всё-таки перезаписан");

        // чистка на месте не должна терять файл
        string inplace = Path.Combine(dir, "test_purge_inplace.dxf");
        File.Copy(src, inplace, true);
        CadPurger.Purge(inplace);
        Check(File.Exists(inplace) && new FileInfo(inplace).Length > 100,
              "чистка на месте не уничтожает файл", "файл пропал или пуст");
    }

    // ---------------------------------------------------------------------
    // 4. Децимация сферы: треугольников должно стать меньше, объём — остаться
    // ---------------------------------------------------------------------
    static void TestDecimator()
    {
        var verts = new List<double>();
        var quads = new List<int>();
        int seg = 24, ring = 24;
        for (int i = 0; i <= ring; i++)
        {
            double phi = Math.PI * i / ring;
            for (int j = 0; j <= seg; j++)
            {
                double th = 2 * Math.PI * j / seg;
                verts.Add(1000 * Math.Sin(phi) * Math.Cos(th));
                verts.Add(1000 * Math.Sin(phi) * Math.Sin(th));
                verts.Add(1000 * Math.Cos(phi));
            }
        }
        for (int i = 0; i < ring; i++)
            for (int j = 0; j < seg; j++)
            {
                int a = i * (seg + 1) + j, b = a + seg + 1;
                quads.Add(a); quads.Add(b); quads.Add(a + 1); quads.Add(a + 1);
                quads.Add(a + 1); quads.Add(b); quads.Add(b + 1); quads.Add(b + 1);
            }

        int before = quads.Count / 4;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        MeshDecimator.Decimate(ref verts, ref quads, 0.7);
        sw.Stop();
        int after = quads.Count / 4;

        Check(after < before && after > 0, "число треугольников уменьшилось",
              before + " -> " + after);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "     {0} -> {1} треугольников за {2} мс", before, after, sw.ElapsedMilliseconds));

        int maxIdx = 0;
        foreach (int q in quads) if (q > maxIdx) maxIdx = q;
        Check(maxIdx < verts.Count / 3, "индексы после децимации корректны",
              "maxIdx=" + maxIdx + " verts=" + (verts.Count / 3));

        var cog = CogCalculator.CalculateElement("sphere", verts, quads, "Steel");
        double expected = 4.0 / 3.0 * Math.PI * Math.Pow(1000, 3) * 1e-9;
        double err = Math.Abs(cog.VolumeM3 - expected) / expected;
        Check(err < 0.25, "форма сохранена (объём в пределах 25%)",
              string.Format(CultureInfo.InvariantCulture, "объём {0:F4} м3 вместо {1:F4} (ошибка {2:P1})",
                            cog.VolumeM3, expected, err));
    }

    // ---------------------------------------------------------------------
    // 5. Срез куба плоскостью должен дать один замкнутый контур из 4 точек
    // ---------------------------------------------------------------------
    static void TestSectionPlan()
    {
        var verts = new List<double>();
        var quads = new List<int>();
        double[,] v = { {0,0,0},{2000,0,0},{2000,2000,0},{0,2000,0},
                        {0,0,3000},{2000,0,3000},{2000,2000,3000},{0,2000,3000} };
        for (int i = 0; i < 8; i++) { verts.Add(v[i,0]); verts.Add(v[i,1]); verts.Add(v[i,2]); }
        int[,] f = { {0,1,2},{0,2,3},{4,6,5},{4,7,6},{0,4,5},{0,5,1},
                     {1,5,6},{1,6,2},{2,6,7},{2,7,3},{3,7,4},{3,4,0} };
        for (int i = 0; i < 12; i++) { quads.Add(f[i,0]); quads.Add(f[i,1]); quads.Add(f[i,2]); quads.Add(f[i,2]); }

        var polys = Section2Plan.Slice(verts, quads, 1200.0, 5.0);
        Check(polys.Count >= 1, "срез дал контур", "полилиний: " + polys.Count);
        if (polys.Count >= 1)
        {
            var p = polys[0];
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var pt in p)
            {
                if (pt[0] < minX) minX = pt[0]; if (pt[0] > maxX) maxX = pt[0];
                if (pt[1] < minY) minY = pt[1]; if (pt[1] > maxY) maxY = pt[1];
            }
            Check(Math.Abs(maxX - minX - 2000) < 1 && Math.Abs(maxY - minY - 2000) < 1,
                  "габарит контура совпадает с кубом",
                  string.Format(CultureInfo.InvariantCulture, "{0}x{1}", maxX - minX, maxY - minY));
            Check(p.Count <= 6, "контур не содержит паразитных перелётов",
                  "точек в контуре: " + p.Count);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var big = new List<double>(); var bigQ = new List<int>();
        var rnd = new Random(7);
        for (int i = 0; i < 20000; i++)
        {
            double x = rnd.NextDouble() * 50000, y = rnd.NextDouble() * 50000;
            int b = big.Count / 3;
            big.Add(x); big.Add(y); big.Add(1000);
            big.Add(x + 50); big.Add(y); big.Add(1400);
            big.Add(x); big.Add(y + 50); big.Add(1400);
            bigQ.Add(b); bigQ.Add(b + 1); bigQ.Add(b + 2); bigQ.Add(b + 2);
        }
        Section2Plan.Slice(big, bigQ, 1200.0, 5.0);
        sw.Stop();
        Check(sw.ElapsedMilliseconds < 20000, "20 000 треугольников срезаются без зависания",
              "заняло " + sw.ElapsedMilliseconds + " мс");
        Console.WriteLine("     срез 20 000 треугольников: " + sw.ElapsedMilliseconds + " мс");
    }

    // ---------------------------------------------------------------------
    // 6. DBSCAN: три плотные группы + одиночка
    // ---------------------------------------------------------------------
    static void TestClashClusterer(string dir)
    {
        var pts = new List<ClashPoint>();
        var rnd = new Random(3);
        for (int g = 0; g < 3; g++)
            for (int i = 0; i < 30; i++)
                pts.Add(new ClashPoint(g * 10000 + rnd.NextDouble() * 200,
                                       rnd.NextDouble() * 200,
                                       rnd.NextDouble() * 200, "clash" + g + "_" + i));
        pts.Add(new ClashPoint(999999, 999999, 999999, "одиночка"));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cl = ClashClusterer.Cluster(pts, 500.0, 2);
        sw.Stop();
        Check(cl.Count == 3, "найдено ровно 3 кластера", "найдено " + cl.Count);
        int noise = 0;
        foreach (var p in pts) if (p.ClusterId <= 0) noise++;
        Check(noise == 1, "одиночная точка отнесена к шуму", "шумовых точек: " + noise);

        string p2 = Path.Combine(dir, "test_clashes.dxf");
        ClashClusterer.WriteStandaloneDxf(p2, cl);
        string txt = File.ReadAllText(p2);
        int entIdx = txt.IndexOf("2\r\nENTITIES", StringComparison.Ordinal);
        if (entIdx < 0) entIdx = txt.IndexOf("2\nENTITIES", StringComparison.Ordinal);
        string entities = entIdx >= 0 ? txt.Substring(entIdx) : txt;
        Check(!entities.Contains("\nLAYER"), "в секции ENTITIES нет записей LAYER",
              "запись таблицы LAYER попала в ENTITIES");
        Check(txt.Contains("TABLES") && txt.Contains("_CLASHES_CL1"), "слои объявлены в TABLES",
              "секция TABLES отсутствует");

        // производительность на 20 000 точек
        var many = new List<ClashPoint>();
        for (int i = 0; i < 20000; i++)
            many.Add(new ClashPoint(rnd.NextDouble() * 100000, rnd.NextDouble() * 100000, rnd.NextDouble() * 30000));
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        ClashClusterer.Cluster(many, 500.0, 2);
        sw2.Stop();
        Check(sw2.ElapsedMilliseconds < 15000, "20 000 коллизий кластеризуются без зависания",
              "заняло " + sw2.ElapsedMilliseconds + " мс");
        Console.WriteLine("     20 000 точек: " + sw2.ElapsedMilliseconds + " мс");
    }

    // ---------------------------------------------------------------------
    // 10. Профиль выдачи: пути, имена, приведение ведомости к формату
    // ---------------------------------------------------------------------
    static void TestOutputProfile(string dir)
    {
        var o = new OutputProfile
        {
            ProjectCode = "2451-14", DocMark = "КМ",
            NamePattern = "{code}-{mark}{suffix}",
            UseFolders = true, FolderReports = "02_Ведомости"
        };

        string p = o.ResolvePath(OutputProfile.Kind.Report, dir, "model", "1055", "_boq", ".csv");
        Check(p.EndsWith("2451-14-КМ_boq.csv"), "имя файла собирается по шаблону", p);
        Check(p.Contains("02_Ведомости"), "файл попадает в подпапку раздела", p);
        Check(Directory.Exists(Path.GetDirectoryName(p)), "подпапка создаётся", "папки нет");

        // пустые подстановки не оставляют висячих разделителей
        var o2 = new OutputProfile { NamePattern = "{code}-{mark}{suffix}" };
        string n2 = o2.Expand(o2.NamePattern, "model", "1055", "_cog");
        Check(!n2.StartsWith("-") && !n2.Contains("--"), "пустые подстановки схлопываются", n2);

        // приведение ведомости: колонки, разделитель, дробная часть
        string src = Path.Combine(dir, "test_report.csv");
        File.WriteAllText(src,
            "sep=;" + Environment.NewLine +
            "Элемент;Площадь (м2);Объем (м3);Масса (т)" + Environment.NewLine +
            "Балка;12.50;3.75;1.20" + Environment.NewLine,
            new UTF8Encoding(true));

        var o3 = new OutputProfile
        {
            CsvSeparator = ";", DecimalSeparator = ",",
            CsvEncoding = "Windows-1251", CsvSepHint = true
        };
        o3.NormalizeReport(src, "Масса");

        string res = File.ReadAllText(src, Encoding.GetEncoding(1251));
        Check(!res.Contains("Масса"), "лишняя колонка удалена из ведомости", res);
        Check(res.Contains("12,50") && res.Contains("3,75"),
              "дробная часть переведена под русский Excel", res);
        Check(res.Contains("Балка"), "кириллица сохранена в Windows-1251", res);
        Check(res.TrimStart().StartsWith("sep="), "строка sep= проставлена", res);

        // текст, похожий на число, не должен ломаться
        string src2 = Path.Combine(dir, "test_report2.csv");
        File.WriteAllText(src2,
            "Профиль;Длина" + Environment.NewLine +
            "Двутавр 20Б1;3.50" + Environment.NewLine,
            new UTF8Encoding(true));
        var o4 = new OutputProfile { DecimalSeparator = ",", CsvEncoding = "UTF-8", CsvSepHint = false };
        o4.NormalizeReport(src2);
        string res2 = File.ReadAllText(src2, new UTF8Encoding(true));
        Check(res2.Contains("Двутавр 20Б1"), "обозначение профиля не искажено", res2);
        Check(res2.Contains("3,50"), "число рядом с текстом переведено", res2);

        // шаблон переносит и настройки выдачи
        var cfg = new AdvancedConfig();
        var outp = new OutputProfile();
        var snap = ConfigPreset.FromConfig("Проверка", "", cfg, o);
        snap.ApplyTo(cfg, outp);
        Check(outp.ProjectCode == "2451-14" && outp.UseFolders,
              "шаблон переносит профиль выдачи", "code=" + outp.ProjectCode + " folders=" + outp.UseFolders);
    }

    // ---------------------------------------------------------------------
    // 9. Шаблон должен менять только свои поля и не ломать остальные
    // ---------------------------------------------------------------------
    static void TestPresets()
    {
        Check(ConfigPreset.All.Count >= 5, "шаблоны загружены",
              "шаблонов: " + ConfigPreset.All.Count);

        int badField = 0, badNorms = 0;
        var probe = new AdvancedConfig();
        foreach (var pr in ConfigPreset.All)
        {
            if (string.IsNullOrEmpty(pr.Norms)) badNorms++;
            foreach (var kv in pr.Values)
            {
                // ключи профиля выдачи адресуют другой объект
                bool isOut = kv.Key.StartsWith(ConfigPreset.OutPrefix, StringComparison.Ordinal);
                var fi = isOut
                    ? typeof(OutputProfile).GetField(kv.Key.Substring(ConfigPreset.OutPrefix.Length))
                    : typeof(AdvancedConfig).GetField(kv.Key);
                if (fi == null) badField++;
            }
        }
        Check(badField == 0, "все поля шаблонов существуют в настройках",
              "несуществующих полей: " + badField);
        Check(badNorms == 0, "у каждого шаблона указан нормативный ориентир",
              "без ориентира: " + badNorms);

        // шаблон меняет свои поля и не трогает чужие
        var cfg = new AdvancedConfig();
        cfg.BcfAuthor = "Иванов";
        cfg.ClearanceCellMm = 777;
        var steel = ConfigPreset.ByName("Металлоконструкции КМ / КМД");
        Check(steel != null, "шаблон находится по имени", "не найден");
        if (steel != null)
        {
            steel.ApplyTo(cfg, new OutputProfile());
            Check(cfg.SteelIncludeCustom == false && Math.Abs(cfg.SteelTolerancePct - 3.0) < 1e-9,
                  "шаблон подставил свои значения",
                  string.Format(CultureInfo.InvariantCulture, "custom={0} tol={1}",
                                cfg.SteelIncludeCustom, cfg.SteelTolerancePct));
            Check(cfg.BcfAuthor == "Иванов" && Math.Abs(cfg.ClearanceCellMm - 777) < 1e-9,
                  "поля вне шаблона не тронуты",
                  "author=" + cfg.BcfAuthor + " cell=" + cfg.ClearanceCellMm);
        }

        // после шаблона конфиг должен переживать сериализацию
        var t2 = ConfigPreset.ByName("Технологические трубопроводы");
        var c2 = new AdvancedConfig();
        if (t2 != null) t2.ApplyTo(c2, new OutputProfile());
        Check(c2.SleeveGapMediumMm > 0 && c2.PipeMaxDiameterMm > c2.PipeMinDiameterMm,
              "значения шаблона непротиворечивы",
              string.Format(CultureInfo.InvariantCulture, "gap={0} dn={1}..{2}",
                            c2.SleeveGapMediumMm, c2.PipeMinDiameterMm, c2.PipeMaxDiameterMm));
    }

    // ---------------------------------------------------------------------
    // 8. Конфиг: JSON через рефлексию должен переживать круговой рейс,
    //    а старые ключи в метрах — переводиться в миллиметры
    // ---------------------------------------------------------------------
    static void TestConfig(string dir)
    {
        var cfg = new AdvancedConfig();
        var fields = typeof(AdvancedConfig).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        // меняем каждое поле, чтобы поймать любое выпавшее из сериализации
        foreach (var f in fields)
        {
            if (f.FieldType == typeof(double)) f.SetValue(cfg, 1234.5);
            else if (f.FieldType == typeof(int)) f.SetValue(cfg, 7);
            else if (f.FieldType == typeof(bool)) f.SetValue(cfg, !(bool)f.GetValue(cfg));
            else if (f.FieldType == typeof(string)) f.SetValue(cfg, "проверка");
        }

        string path = Path.Combine(dir, "test_cfg.json");
        cfg.SaveTo(path);
        var back = AdvancedConfig.LoadFrom(path);

        int lost = 0;
        string first = null;
        foreach (var f in fields)
        {
            object a = f.GetValue(cfg), b = f.GetValue(back);
            if (!Equals(a, b)) { lost++; if (first == null) first = f.Name + ": " + a + " -> " + b; }
        }
        Check(lost == 0, "все " + fields.Length + " полей переживают запись и чтение",
              "потеряно полей: " + lost + " (" + first + ")");

        // старый файл в метрах должен подняться в миллиметры
        string legacy = Path.Combine(dir, "test_cfg_legacy.json");
        string legacyJson = "{" +
            "\"ClashEpsilon\": 1.5," +
            "\"SleeveGapMedium\": 0.05," +
            "\"SectionCutHeight\": 1.2" +
            "}";
        File.WriteAllText(legacy,
            legacyJson.Replace(",\"", "," + Environment.NewLine + "  \""),
            Encoding.UTF8);
        var mig = AdvancedConfig.LoadFrom(legacy);
        Check(Math.Abs(mig.ClashEpsilonMm - 1500) < 0.01 &&
              Math.Abs(mig.SleeveGapMediumMm - 50) < 0.01 &&
              Math.Abs(mig.SectionCutHeightMm - 1200) < 0.01,
              "старые ключи в метрах переводятся в миллиметры",
              string.Format(CultureInfo.InvariantCulture, "eps={0} gap={1} z={2}",
                            mig.ClashEpsilonMm, mig.SleeveGapMediumMm, mig.SectionCutHeightMm));

        // правила, которыми пользуются модули
        var d = new AdvancedConfig();
        Check(d.SleeveGapFor(32) == d.SleeveGapSmallMm &&
              d.SleeveGapFor(100) == d.SleeveGapMediumMm &&
              d.SleeveGapFor(500) == d.SleeveGapLargeMm,
              "зазор гильзы выбирается по диапазону DN", "правило SleeveGapFor неверно");
        Check(d.DensityFor("Concrete") == d.DensityConcrete &&
              d.DensityFor("Piping") == d.DensityPiping &&
              d.DensityFor("что-то") == d.DensitySteel,
              "плотность выбирается по материалу", "правило DensityFor неверно");
    }

    // ---------------------------------------------------------------------
    // 7. Куб 1x1x1 м, унесённый на 5 км от нуля: объём не должен «поплыть»
    // ---------------------------------------------------------------------
    static void TestCogPrecision()
    {
        double off = 5000000.0; // 5 км в мм
        var verts = new List<double>();
        var quads = new List<int>();
        double[,] v = { {0,0,0},{1000,0,0},{1000,1000,0},{0,1000,0},
                        {0,0,1000},{1000,0,1000},{1000,1000,1000},{0,1000,1000} };
        for (int i = 0; i < 8; i++) { verts.Add(v[i,0] + off); verts.Add(v[i,1] + off); verts.Add(v[i,2] + off); }
        int[,] f = { {0,2,1},{0,3,2},{4,5,6},{4,6,7},{0,1,5},{0,5,4},
                     {1,2,6},{1,6,5},{2,3,7},{2,7,6},{3,0,4},{3,4,7} };
        for (int i = 0; i < 12; i++) { quads.Add(f[i,0]); quads.Add(f[i,1]); quads.Add(f[i,2]); quads.Add(f[i,2]); }

        var r = CogCalculator.CalculateElement("cube@5km", verts, quads, "Steel");
        double err = Math.Abs(r.VolumeM3 - 1.0);
        Check(err < 1e-6, "объём куба 1 м3 на удалении 5 км",
              string.Format(CultureInfo.InvariantCulture, "получено {0:F9} м3", r.VolumeM3));
        Check(Math.Abs(r.CogX - (off + 500)) < 0.01 &&
              Math.Abs(r.CogZ - (off + 500)) < 0.01, "центр масс в мировых координатах",
              string.Format(CultureInfo.InvariantCulture, "CoG=({0:F3},{1:F3},{2:F3})", r.CogX, r.CogY, r.CogZ));
        Check(Math.Abs(r.MassKg - 7850.0) < 0.1, "масса стального куба 7850 кг",
              string.Format(CultureInfo.InvariantCulture, "{0:F2} кг", r.MassKg));
    }

    // ---------------------------------------------------------------------
    // 11. Индекс ревизии. Проверяем не «файл записался», а то, ради чего он
    //     нужен: сравнение двух выдач должно правильно относить элемент к
    //     одной из категорий и не срабатывать на шуме округления.
    // ---------------------------------------------------------------------
    static IndexEntry E(string name, double x, double y, double z, double sz, int tris)
    {
        var e = new IndexEntry { Name = name };
        e.Expand(x, y, z, x + sz, y + sz, z + sz);
        e.Fragments = 1;
        e.Triangles = tris;
        return e;
    }

    static void TestRevisionIndex(string dir)
    {
        string path = Path.Combine(dir, "rev_index.csv");

        var older = new List<IndexEntry>
        {
            E("Балка Б1",   0,    0, 0, 100, 100),
            E("Балка Б2", 1000,   0, 0, 100, 100),
            E("Плита П1", 2000,   0, 0, 100, 100),
            E("Стена С1", 3000,   0, 0, 100, 100),
            E("Ферма Ф1", 4000,   0, 0, 100, 100),
        };

        RevisionIndex.Write(path, older, "модель.nwd");
        var back = RevisionIndex.Read(path);
        Check(back.Count == older.Count, "индекс: запись и чтение",
              "прочитано " + back.Count + " из " + older.Count);
        if (back.Count == older.Count)
        {
            bool same = true;
            for (int i = 0; i < back.Count; i++)
            {
                var a = older[i]; var b = back[i];
                if (a.Name != b.Name || Math.Abs(a.MinX - b.MinX) > 1e-6 ||
                    Math.Abs(a.MaxZ - b.MaxZ) > 1e-6 || a.Triangles != b.Triangles)
                { same = false; break; }
            }
            Check(same, "индекс: значения переживают круг записи", "поля разошлись");
        }

        var newer = new List<IndexEntry>
        {
            E("Балка Б1",    0,   0, 0, 100, 100),   // без изменений
            E("Балка Б2", 1500,   0, 0, 100, 100),   // сдвинута на 500 мм
            E("Плита П1", 2000,   0, 0, 140, 100),   // изменены габариты
            E("Ферма Ф1", 4000,   0, 0, 100, 260),   // та же форма, иная сетка
            E("Люк Л1",   5000,   0, 0, 100, 100),   // добавлен
            // "Стена С1" отсутствует — удалена
        };

        var d = RevisionIndex.Compare(older, newer, 5.0, 2.0);

        int added = 0, removed = 0, moved = 0, reshaped = 0, retri = 0;
        foreach (var it in d.Items)
        {
            if (it.Kind == ChangeKind.Added) added++;
            else if (it.Kind == ChangeKind.Removed) removed++;
            else if (it.Kind == ChangeKind.Moved) moved++;
            else if (it.Kind == ChangeKind.Reshaped) reshaped++;
            else if (it.Kind == ChangeKind.Retriangulated) retri++;
        }

        Check(added == 1, "сравнение: добавленный элемент", "добавленных " + added);
        Check(removed == 1, "сравнение: удалённый элемент", "удалённых " + removed);
        Check(moved == 1, "сравнение: смещённый элемент", "смещённых " + moved);
        Check(reshaped == 1, "сравнение: изменённые габариты", "изменённых " + reshaped);
        Check(retri == 1, "сравнение: пересчитанная сетка", "пересчитанных " + retri);
        Check(d.Items.Count == 5, "сравнение: неизменный элемент не попал в отчёт",
              "строк в отчёте " + d.Items.Count);

        // Шум округления не должен выглядеть как изменение проекта.
        var noise = new List<IndexEntry> { E("Балка Б1", 0.4, 0.3, 0.2, 100, 100) };
        var dq = RevisionIndex.Compare(new List<IndexEntry> { E("Балка Б1", 0, 0, 0, 100, 100) },
                                       noise, 5.0, 2.0);
        Check(dq.Items.Count == 0, "сравнение: смещение ниже допуска игнорируется",
              "лишних строк " + dq.Items.Count);

        string csv = Path.Combine(dir, "rev_diff.csv");
        RevisionIndex.WriteCsv(csv, d, "было", "стало");
        RevisionIndex.WriteDxf(Path.ChangeExtension(csv, ".dxf"), d);
        Check(File.Exists(csv) && new FileInfo(csv).Length > 0, "сравнение: отчёт CSV", "пустой файл");
        string dxf = File.ReadAllText(Path.ChangeExtension(csv, ".dxf"));
        Check(dxf.Contains("EOF") && dxf.Contains("SECTION"), "сравнение: метки DXF",
              "структура DXF нарушена");
    }

    // ---------------------------------------------------------------------
    // 12. Журнал выдач: важна дозапись — файл ведут несколько человек, и
    //     повторный запуск не должен затирать историю.
    // ---------------------------------------------------------------------
    static void TestDeliveryLog(string dir)
    {
        string path = Path.Combine(dir, "delivery.csv");
        if (File.Exists(path)) File.Delete(path);

        for (int i = 1; i <= 3; i++)
            DeliveryLog.Append(path, new DeliveryRecord
            {
                ProjectCode = "2451-14",
                DocMark = "ТХ",
                Model = @"C:\проект\модель.nwd",
                Elements = 100 * i,
                Triangles = 1000 * i,
                FilesOut = i,
                Preset = "Технологические трубопроводы"
            });

        var rows = DeliveryLog.Read(path);
        Check(rows.Count == 3, "журнал: три прогона — три строки", "строк " + rows.Count);
        if (rows.Count == 3)
            Check(rows[0][6] == "100" && rows[2][6] == "300",
                  "журнал: история не затирается", "порядок или значения нарушены");

        string raw = File.ReadAllText(path, Encoding.UTF8);
        Check(raw.IndexOf("\uFEFF", 1, StringComparison.Ordinal) < 0,
              "журнал: метка BOM только в начале файла", "BOM встречается в середине");
        Check(raw.Contains("2451-14") && raw.Contains("ТХ"),
              "журнал: кириллица читается", "кодировка нарушена");
    }

    // ---------------------------------------------------------------------
    // 13. Маршрутизатор ИИ. Главное требование — режим закрытого контура
    //     обязан отсекать внешние адреса, иначе настройка бесполезна.
    // ---------------------------------------------------------------------
    static void TestAiRouter(string dir)
    {
        var s = new AiSettings();
        s.Providers.Clear();
        s.Providers.Add(new AiProvider { Name = "локальная", BaseUrl = "http://localhost:11434/v1", Model = "m", Enabled = true });
        s.Providers.Add(new AiProvider { Name = "внешняя",   BaseUrl = "https://api.example.com/v1", Model = "m", Enabled = true });
        s.Providers.Add(new AiProvider { Name = "без модели", BaseUrl = "http://localhost:1234/v1", Model = "", Enabled = true });

        string why;
        s.Enabled = false;
        Check(s.Route(out why).Count == 0, "роутер: выключенный помощник не ходит в сеть", "маршрут не пуст");

        s.Enabled = true;
        s.LocalOnly = true;
        var r1 = s.Route(out why);
        Check(r1.Count == 1 && r1[0].Name == "локальная",
              "роутер: закрытый контур пропускает только localhost",
              "выбрано " + r1.Count + " провайдеров");

        s.LocalOnly = false;
        var r2 = s.Route(out why);
        Check(r2.Count == 2, "роутер: без ограничения доступны оба настроенных",
              "выбрано " + r2.Count);
        Check(r2.Count == 2 && r2[0].Name == "локальная",
              "роутер: порядок перебора сохраняется", "первым идёт не локальный");

        s.Providers[0].Enabled = false;
        s.Providers[1].Enabled = false;
        Check(s.Route(out why).Count == 0 && why.Length > 0,
              "роутер: пустой маршрут объясняет причину", "причина не указана");

        // Ключ не должен лежать на диске открытым текстом.
        var p = new AiProvider();
        p.SetKey("секретный-ключ-12345");
        Check(p.HasKey && p.KeyProtected.IndexOf("секретный", StringComparison.Ordinal) < 0,
              "ключ: на диск попадает в зашифрованном виде", "ключ виден в открытом виде");
        Check(p.GetKey() == "секретный-ключ-12345", "ключ: расшифровывается обратно",
              "получено «" + p.GetKey() + "»");
        p.SetKey("");
        Check(!p.HasKey && p.GetKey() == "", "ключ: очистка работает", "ключ остался");

        string f = Path.Combine(dir, "ai_test.json");
        s.Enabled = true; s.LocalOnly = false; s.AllowModelData = true; s.MaxNamesPerRequest = 321;
        s.Providers[0].Enabled = true;
        s.Providers[0].SetKey("ключ-провайдера");
        s.SaveTo(f);
        var back2 = AiSettings.LoadFrom(f);
        Check(back2.Enabled && !back2.LocalOnly && back2.AllowModelData &&
              back2.MaxNamesPerRequest == 321,
              "настройки ИИ: флаги переживают круг записи", "значения разошлись");
        Check(back2.Providers.Count == s.Providers.Count &&
              back2.Providers[0].BaseUrl == "http://localhost:11434/v1" &&
              back2.Providers[0].GetKey() == "ключ-провайдера",
              "настройки ИИ: провайдеры и ключи восстанавливаются",
              "провайдеров " + back2.Providers.Count);
    }

    // ---------------------------------------------------------------------
    // 11б. Перенос нуля площадки. На сравнении рабочей и координационной
    //      моделей все 4308 элементов «сместились» на одни и те же 46 340 мм.
    //      Формально верно, практически бесполезно: настоящие правки тонули.
    //      Общий перенос должен вычитаться и показываться одной строкой.
    // ---------------------------------------------------------------------
    static void TestBaseShift()
    {
        const double SX = 12000, SY = -3400, SZ = 0;

        var older = new List<IndexEntry>();
        for (int i = 0; i < 40; i++)
            older.Add(E("Элемент " + i, i * 500, 0, 0, 200, 100));

        var newer = new List<IndexEntry>();
        for (int i = 0; i < 40; i++)
        {
            // Элемент 7 переехал дополнительно к общему переносу,
            // элемент 9 переделан по габариту.
            double extra = (i == 7) ? 900 : 0;
            int sz = (i == 9) ? 260 : 200;
            newer.Add(E("Элемент " + i, i * 500 + SX + extra, SY, SZ, sz, 100));
        }

        var d = RevisionIndex.Compare(older, newer, 5.0, 2.0);

        Check(d.HasBaseShift, "перенос: общий сдвиг распознан", "сдвиг не найден");
        Check(d.HasBaseShift && Math.Abs(d.BaseX - SX) < 1 && Math.Abs(d.BaseY - SY) < 1,
              "перенос: вектор определён верно",
              d.HasBaseShift ? string.Format("получено X={0:F0} Y={1:F0}", d.BaseX, d.BaseY) : "-");

        int moved = d.CountOf(ChangeKind.Moved);
        int reshaped = d.CountOf(ChangeKind.Reshaped);
        Check(moved == 1, "перенос: настоящее смещение осталось видно", "смещённых " + moved);
        Check(reshaped == 1, "перенос: изменение габарита осталось видно", "изменённых " + reshaped);
        Check(d.Same == 38, "перенос: остальные признаны неизменными", "неизменных " + d.Same);
        Check(RevisionIndex.BaseShiftNote(d).Contains("смещена"),
              "перенос: в отчёт попадает пояснение", "пояснения нет");

        // Разнонаправленные сдвиги общим переносом не считаются: иначе
        // произвольная правка половины объекта молча ушла бы из отчёта.
        var chaos = new List<IndexEntry>();
        for (int i = 0; i < 40; i++)
            chaos.Add(E("Элемент " + i, i * 500 + (i % 7) * 300, 0, 0, 200, 100));
        var d2 = RevisionIndex.Compare(older, chaos, 5.0, 2.0);
        Check(!d2.HasBaseShift, "перенос: разнобой не принимается за общий сдвиг",
              "ошибочно найден сдвиг");
    }

    // ---------------------------------------------------------------------
    // 9б. Шаблон должен применяться целиком.
    //
    // Шаблоны «Выдача: …» задают только поля профиля выдачи. Из командной
    // строки они не применялись вовсе: вызывалась перегрузка без профиля,
    // и такие поля молча отбрасывались. Программа при этом писала
    // «Применён шаблон», а выдача оставалась прежней.
    // ---------------------------------------------------------------------
    static void TestPresetAppliesOutput()
    {
        int outOnly = 0, applied = 0;

        foreach (var p in ConfigPreset.All)
        {
            var outKeys = new List<string>();
            foreach (var kv in p.Values)
                if (kv.Key.StartsWith(ConfigPreset.OutPrefix, StringComparison.Ordinal))
                    outKeys.Add(kv.Key);
            if (outKeys.Count == 0) continue;
            outOnly++;

            // Шаблон применяется поверх сохранённого профиля пользователя,
            // поэтому «равно умолчанию» ещё не значит «ничего не делает»:
            // такое значение сбрасывает чужую настройку. Проверяем сам факт
            // записи — портим целевые поля заведомо другим значением.
            var cfg = new AdvancedConfig();
            var outp = new OutputProfile();
            foreach (string key in outKeys)
            {
                var fi = typeof(OutputProfile).GetField(key.Substring(ConfigPreset.OutPrefix.Length));
                if (fi == null) continue;
                if (fi.FieldType == typeof(bool))
                    fi.SetValue(outp, !(bool)Convert.ChangeType(p.Values[key], typeof(bool), CultureInfo.InvariantCulture));
                else if (fi.FieldType == typeof(string))
                    fi.SetValue(outp, "мусор-до-применения");
                else if (fi.FieldType == typeof(double))
                    fi.SetValue(outp, -12345.0);
                else if (fi.FieldType == typeof(int))
                    fi.SetValue(outp, -12345);
            }

            p.ApplyTo(cfg, outp);

            bool ok = true;
            string bad = "";
            foreach (string key in outKeys)
            {
                var fi = typeof(OutputProfile).GetField(key.Substring(ConfigPreset.OutPrefix.Length));
                if (fi == null) { ok = false; bad = key + " — нет такого поля"; break; }
                object want = Convert.ChangeType(p.Values[key], fi.FieldType, CultureInfo.InvariantCulture);
                object got = fi.GetValue(outp);
                if (!Equals(want, got))
                {
                    ok = false;
                    bad = string.Format("{0}: ожидалось «{1}», получено «{2}»", key, want, got);
                    break;
                }
            }

            if (ok) applied++;
            else Fail("шаблон применяется: " + p.Name, bad);
        }

        Check(outOnly >= 3, "шаблоны выдачи присутствуют", "найдено " + outOnly);
        Check(applied == outOnly, "каждый шаблон записывает свои поля профиля",
              applied + " из " + outOnly);

        // Сметчику геометрия не нужна — этот шаблон обязан её отключать.
        var smeta = ConfigPreset.ByName("Выдача: только ведомости сметчику");
        Check(smeta != null, "шаблон сметчика находится по имени", "не найден");
        if (smeta != null)
        {
            var cfg2 = new AdvancedConfig();
            var o2 = new OutputProfile();
            smeta.ApplyTo(cfg2, o2);
            Check(!o2.EmitGeometry, "шаблон сметчика отключает основную геометрию",
                  "геометрия осталась включённой");
            Check(o2.CsvEncoding == "Windows-1251", "шаблон сметчика ставит кодировку Excel",
                  "кодировка " + o2.CsvEncoding);
        }
    }

    // ---------------------------------------------------------------------
    // 9г. Книга Excel.
    //
    // В настройках формат ведомостей «Xlsx» можно было выбрать, но книга не
    // создавалась — писался всё тот же CSV. Проверяем не «файл появился», а
    // что внутри лежит настоящая книга: обязательные части на месте, числа
    // записаны числами, а обозначения марок остались текстом — именно их
    // Excel любит превращать в даты.
    // ---------------------------------------------------------------------
    static void TestXlsx(string dir)
    {
        string csv = Path.Combine(dir, "xl_src.csv");
        string xlsx = Path.Combine(dir, "xl_out.xlsx");
        var enc = Encoding.UTF8;

        File.WriteAllText(csv,
            "sep=;\r\n" +
            "Профиль;Стандарт;Кол-во;Масса кг\r\n" +
            "Труба 180х180х6;ГОСТ 30245-2003;3;148,42\r\n" +
            "09Г2С;ГОСТ 19281;12;7,5\r\n" +
            "\"Уголок; равнополочный\";ГОСТ 8509;5;22\r\n", enc);

        bool ok = XlsxWriter.FromCsv(csv, xlsx, ';', enc, "Ведомость КМ");
        Check(ok && File.Exists(xlsx), "книга собрана", "FromCsv вернул " + ok);
        if (!ok || !File.Exists(xlsx)) return;

        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(xlsx))
            foreach (var e in zip.Entries)
                using (var sr = new StreamReader(e.Open(), Encoding.UTF8))
                    parts[e.FullName] = sr.ReadToEnd();

        foreach (string need in new[] { "[Content_Types].xml", "_rels/.rels",
                                        "xl/workbook.xml", "xl/_rels/workbook.xml.rels",
                                        "xl/styles.xml", "xl/worksheets/sheet1.xml" })
            Check(parts.ContainsKey(need), "часть книги: " + need, "отсутствует");

        string sheet = parts.ContainsKey("xl/worksheets/sheet1.xml") ? parts["xl/worksheets/sheet1.xml"] : "";

        Check(sheet.Contains("<v>148.42</v>"), "число записано числом, с точкой",
              "нет ячейки 148.42");
        Check(sheet.Contains(">09Г2С<"), "обозначение марки осталось текстом",
              "марка не найдена как текст");
        Check(!sheet.Contains("<v>09"), "марка не превратилась в число", "марка ушла в число");
        Check(sheet.Contains("Уголок; равнополочный"),
              "поле с разделителем внутри кавычек прочитано целиком", "поле разорвано");
        Check(sheet.Contains("s=\"1\""), "шапка выделена стилем", "стиль шапки не применён");
        Check(sheet.Contains("state=\"frozen\""), "шапка закреплена", "закрепление не задано");
        Check(!sheet.Contains("sep="), "служебная строка sep= в книгу не попала", "sep= внутри");

        string wb = parts.ContainsKey("xl/workbook.xml") ? parts["xl/workbook.xml"] : "";
        Check(wb.Contains("Ведомость КМ"), "имя листа задано", "имя листа не найдено");
    }

    // ---------------------------------------------------------------------
    // 9в. Одна марка материала — одна строка ведомости.
    //
    // На реальной модели «Сталь 20» встретилась тремя написаниями сразу:
    // обычным, строчным и с латинской C вместо кириллической. В ведомости
    // получились три позиции вместо одной: 651 + 85 + 28 строк. Для ведомости
    // материалов это брак — марку считают по одной строке.
    // ---------------------------------------------------------------------
    static void TestMaterialKey()
    {
        string a = BoqCalculator.MaterialKey("Сталь 20");
        string b = BoqCalculator.MaterialKey("сталь 20");
        string c = BoqCalculator.MaterialKey("Cталь 20");        // латинская C
        string d = BoqCalculator.MaterialKey("  Сталь   20 ");
        Check(a == b, "марка: регистр не различает", a + " != " + b);
        Check(a == c, "марка: латинский двойник приводится к кириллице", a + " != " + c);
        Check(a == d, "марка: лишние пробелы не различают", a + " != " + d);

        Check(BoqCalculator.MaterialKey("09Г2С") != BoqCalculator.MaterialKey("08Х18Н10Т"),
              "марка: разные марки не сливаются", "слились");
        Check(BoqCalculator.MaterialKey("") == "", "марка: пустое остаётся пустым",
              "пустая строка изменилась");

        // Латинские двойники встречаются и в обозначениях проката.
        Check(BoqCalculator.MaterialKey("ВСт3сп5") == BoqCalculator.MaterialKey("BCт3cп5"),
              "марка: двойники в обозначении проката", "не совпали");
    }
}
