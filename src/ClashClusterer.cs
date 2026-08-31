// ============================================================================
//  ClashClusterer.cs — Пространственная кластеризация коллизий (DBSCAN)
//  NWD2DWG v3.1 | namespace NWD2DWG.Plugin
//
//  Замещает: iConstruct Clash Manager (~$1 500/год)
//
//  Алгоритм: DBSCAN (Density-Based Spatial Clustering of Applications with Noise)
//  Ester et al., 1996. O(N²) в худшем случае, O(N log N) с сеткой.
//
//  Применение:
//    var pts = clashes.Select(c => new ClashPoint(c.X, c.Y, c.Z, c.Name)).ToList();
//    var clusters = ClashClusterer.Cluster(pts, eps: 500.0, minPts: 2);
//    ClashClusterer.WriteDxf(writer, clusters);
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    // -------------------------------------------------------------------------
    // Входная точка коллизии (координаты в мм или мировых единицах проекта)
    // -------------------------------------------------------------------------
    public class ClashPoint
    {
        public double X, Y, Z;
        public string Name;      // имя коллизии из Navisworks / BCF
        public int    ClusterId; // -1 = шум, 0..N = номер кластера

        public ClashPoint(double x, double y, double z, string name = "")
        {
            X = x; Y = y; Z = z;
            Name = name;
            ClusterId = -2; // не посещён
        }
    }

    // -------------------------------------------------------------------------
    // Результирующий кластер
    // -------------------------------------------------------------------------
    public class ClashCluster
    {
        public int Id;
        public List<ClashPoint> Points = new List<ClashPoint>();

        // Центроид кластера
        public double Cx { get; private set; }
        public double Cy { get; private set; }
        public double Cz { get; private set; }

        public void ComputeCentroid()
        {
            if (Points.Count == 0) return;
            double sx = 0, sy = 0, sz = 0;
            foreach (var p in Points) { sx += p.X; sy += p.Y; sz += p.Z; }
            Cx = sx / Points.Count;
            Cy = sy / Points.Count;
            Cz = sz / Points.Count;
        }
    }

    // -------------------------------------------------------------------------
    // Основной класс кластеризатора
    // -------------------------------------------------------------------------
    public static class ClashClusterer
    {
        // Значения по умолчанию
        public const double DefaultEps    = 500.0; // радиус соседства (мм)
        public const int    DefaultMinPts = 2;      // мин. точек для ядра

        /// <summary>
        /// Кластеризует список точек коллизий методом DBSCAN.
        /// </summary>
        /// <param name="points">Список коллизий</param>
        /// <param name="eps">Радиус соседства (те же единицы, что координаты)</param>
        /// <param name="minPts">Минимальное число точек в ε-окрестности для ядра</param>
        /// <returns>Список кластеров (без шумовых точек ClusterId == -1)</returns>
        public static List<ClashCluster> Cluster(
            List<ClashPoint> points,
            double eps    = DefaultEps,
            int    minPts = DefaultMinPts)
        {
            if (points == null || points.Count == 0)
                return new List<ClashCluster>();

            double eps2 = eps * eps; // сравниваем квадраты — избегаем sqrt
            int clusterId = 0;
            var index = new SpatialIndex(points, eps);

            // Сбрасываем состояние
            foreach (var p in points) p.ClusterId = -2;

            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (p.ClusterId != -2) continue; // уже посещена

                // Найти ε-соседей
                List<int> neighbours = index.Query(i, eps2);

                if (neighbours.Count < minPts)
                {
                    // Помечаем как шум (может переклассифицироваться позже)
                    p.ClusterId = -1;
                    continue;
                }

                // Новый кластер
                clusterId++;
                p.ClusterId = clusterId;

                // Обходим соседей через очередь (итеративный BFS)
                var queue = new Queue<int>(neighbours);
                while (queue.Count > 0)
                {
                    int qi = queue.Dequeue();
                    var q  = points[qi];

                    // Точка была шумом — включаем в кластер без расширения
                    if (q.ClusterId == -1) q.ClusterId = clusterId;

                    // Точка уже назначена (своя или другого кластера) — пропуск
                    if (q.ClusterId != -2) continue;

                    q.ClusterId = clusterId;

                    List<int> qNeighbours = index.Query(qi, eps2);
                    if (qNeighbours.Count >= minPts)
                    {
                        // Ядровая точка — добавляем её соседей в очередь
                        foreach (int ni in qNeighbours)
                            if (points[ni].ClusterId == -2 || points[ni].ClusterId == -1)
                                queue.Enqueue(ni);
                    }
                }
            }

            // Собираем кластеры
            var clusterMap = new Dictionary<int, ClashCluster>();
            foreach (var p in points)
            {
                if (p.ClusterId <= 0) continue; // шум или не обработан
                ClashCluster cl;
                if (!clusterMap.TryGetValue(p.ClusterId, out cl))
                {
                    cl = new ClashCluster { Id = p.ClusterId };
                    clusterMap[p.ClusterId] = cl;
                }
                cl.Points.Add(p);
            }

            var result = new List<ClashCluster>(clusterMap.Values);
            result.Sort((a, b) => b.Points.Count.CompareTo(a.Points.Count)); // по убыванию размера

            foreach (var cl in result) cl.ComputeCentroid();

            return result;
        }

        // Равномерная сетка со стороной eps: соседи точки лежат максимум в 27
        // соседних ячейках. В шапке модуля было заявлено O(N log N), а по факту
        // RegionQuery перебирал все точки — O(N^2) на каждый вызов.
        private class SpatialIndex
        {
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
            private readonly List<ClashPoint> _pts;
            private readonly double _cell;

            public SpatialIndex(List<ClashPoint> pts, double cell)
            {
                _pts = pts;
                _cell = cell > 1e-9 ? cell : 1e-9;
                for (int i = 0; i < pts.Count; i++)
                {
                    long k = Key(pts[i].X, pts[i].Y, pts[i].Z);
                    List<int> b;
                    if (!_cells.TryGetValue(k, out b)) { b = new List<int>(); _cells[k] = b; }
                    b.Add(i);
                }
            }

            private long Key(double x, double y, double z)
            {
                long cx = (long)Math.Floor(x / _cell);
                long cy = (long)Math.Floor(y / _cell);
                long cz = (long)Math.Floor(z / _cell);
                return (cx * 73856093L) ^ (cy * 19349663L) ^ (cz * 83492791L);
            }

            public List<int> Query(int idx, double eps2)
            {
                var res = new List<int>();
                ClashPoint p = _pts[idx];
                long cx = (long)Math.Floor(p.X / _cell);
                long cy = (long)Math.Floor(p.Y / _cell);
                long cz = (long)Math.Floor(p.Z / _cell);
                for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                for (long dz = -1; dz <= 1; dz++)
                {
                    long k = ((cx + dx) * 73856093L) ^ ((cy + dy) * 19349663L) ^ ((cz + dz) * 83492791L);
                    List<int> b;
                    if (!_cells.TryGetValue(k, out b)) continue;
                    foreach (int j in b)
                    {
                        double ex = _pts[j].X - p.X, ey = _pts[j].Y - p.Y, ez = _pts[j].Z - p.Z;
                        if (ex * ex + ey * ey + ez * ez <= eps2) res.Add(j);
                    }
                }
                return res;
            }
        }

        // -------------------------------------------------------------------------
        // Статистика для лога
        // -------------------------------------------------------------------------
        public static string GetSummary(List<ClashPoint> allPoints, List<ClashCluster> clusters)
        {
            int noise = 0;
            foreach (var p in allPoints)
                if (p.ClusterId <= 0) noise++;

            var sb = new StringBuilder();
            sb.AppendLine(string.Format(
                "[ClashClusterer] Всего коллизий: {0} | Кластеров: {1} | Шум (дубли/изоляция): {2} ({3:0}%)",
                allPoints.Count, clusters.Count, noise,
                allPoints.Count > 0 ? 100.0 * noise / allPoints.Count : 0));

            foreach (var cl in clusters)
                sb.AppendLine(string.Format(
                    "  Кластер #{0}: {1} коллизий, центроид ({2:F0}, {3:F0}, {4:F0})",
                    cl.Id, cl.Points.Count,
                    cl.Cx, cl.Cy, cl.Cz));
            return sb.ToString();
        }

        // -------------------------------------------------------------------------
        // Запись кластеров в DXF (точки-маркеры и подписи)
        // -------------------------------------------------------------------------
        // Цвета AutoCAD по индексу:
        //   1=красный  2=жёлтый  3=зелёный  4=голубой  5=синий  6=фиолетовый
        //   7=белый   ...  256-й = BYLAYER
        private static readonly int[] PaletteAci =
        {
            1, 2, 3, 4, 5, 6, 30, 40, 50, 60,
            70, 80, 90, 100, 110, 120, 130, 140, 150, 160
        };

        /// <summary>
        /// Добавляет геометрию кластеров в открытый DXF StreamWriter.
        /// Слой: _CLASHES_CL{N}  (один слой на кластер)
        /// </summary>
        public static void WriteDxf(StreamWriter w, List<ClashCluster> clusters)
        {
            if (w == null || clusters == null) return;

            foreach (var cl in clusters)
            {
                string layer  = "_CLASHES_CL" + cl.Id.ToString();
                int    color  = PaletteAci[(cl.Id - 1) % PaletteAci.Length];

                // Запись LAYER — это таблица, её нельзя писать в ENTITIES.
                // Слои объявляются в секции TABLES (см. WriteTablesSection).

                // --- Точка центроида как POINT-сущность ---
                WritePoint(w, cl.Cx, cl.Cy, cl.Cz, layer, color);

                // --- Подпись: кол-во коллизий ---
                string label = string.Format(
                    CultureInfo.InvariantCulture,
                    "CLUSTER #{0} ({1} hits)", cl.Id, cl.Points.Count);
                WriteText(w, cl.Cx, cl.Cy, cl.Cz + 200, label, layer, color, 150);

                // --- Маркеры для каждой точки кластера (маленькие POINT) ---
                foreach (var p in cl.Points)
                    WritePoint(w, p.X, p.Y, p.Z, layer, color);
            }
        }

        // Объявление слоёв кластеров в секции TABLES
        private static void WriteTablesSection(StreamWriter w, List<ClashCluster> clusters)
        {
            w.WriteLine("0\nSECTION\n2\nTABLES");
            w.WriteLine("0\nTABLE\n2\nLAYER");
            w.WriteLine("70\n" + (clusters.Count + 1));
            w.WriteLine("0\nLAYER\n2\n0\n70\n0\n62\n7\n6\nCONTINUOUS");
            foreach (var cl in clusters)
            {
                int color = PaletteAci[(cl.Id - 1) % PaletteAci.Length];
                w.WriteLine("0\nLAYER\n2\n_CLASHES_CL" + cl.Id +
                            "\n70\n0\n62\n" + color + "\n6\nCONTINUOUS");
            }
            w.WriteLine("0\nENDTAB");
            w.WriteLine("0\nENDSEC");
        }

        // -------------------------------------------------------------------------
        // Утилиты DXF
        // -------------------------------------------------------------------------
        private static void WritePoint(StreamWriter w,
            double x, double y, double z,
            string layer, int color)
        {
            w.WriteLine("0\nPOINT");
            w.WriteLine("8\n" + layer);
            w.WriteLine("62\n" + color);
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "10\n{0:F4}\n20\n{1:F4}\n30\n{2:F4}", x, y, z));
        }

        private static void WriteText(StreamWriter w,
            double x, double y, double z,
            string text, string layer, int color, double height)
        {
            w.WriteLine("0\nTEXT");
            w.WriteLine("8\n" + layer);
            w.WriteLine("62\n" + color);
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "10\n{0:F4}\n20\n{1:F4}\n30\n{2:F4}", x, y, z));
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "40\n{0:F2}", height));
            w.WriteLine("1\n" + text);
        }

        // -------------------------------------------------------------------------
        // Запись standalone DXF-файла кластеров (для независимого вывода)
        // -------------------------------------------------------------------------
        public static void WriteStandaloneDxf(string outputPath, List<ClashCluster> clusters)
        {
            using (var w = new StreamWriter(outputPath, false, Encoding.Default))
            {
                // Минимальный заголовок DXF
                w.WriteLine("0\nSECTION\n2\nHEADER");
                w.WriteLine("9\n$ACADVER\n1\nAC1015"); // AutoCAD 2000+
                w.WriteLine("0\nENDSEC");

                WriteTablesSection(w, clusters);

                w.WriteLine("0\nSECTION\n2\nENTITIES");
                WriteDxf(w, clusters);
                w.WriteLine("0\nENDSEC");

                w.WriteLine("0\nEOF");
            }
        }

        // -------------------------------------------------------------------------
        // Интеграционная точка: принимает raw-список точек из BCF или напрямую
        // из NwdPlugin, кластеризует и возвращает строку лога + путь к DXF
        // -------------------------------------------------------------------------
        public static string Process(
            List<ClashPoint> clashPoints,
            string           baseDxfPath,
            double           eps    = DefaultEps,
            int              minPts = DefaultMinPts)
        {
            if (clashPoints == null || clashPoints.Count == 0)
                return "[ClashClusterer] Нет точек коллизий для кластеризации.";

            var clusters = Cluster(clashPoints, eps, minPts);
            string summary = GetSummary(clashPoints, clusters);

            // Записываем _clashes.dxf рядом с основным выводом
            string outPath = Path.ChangeExtension(baseDxfPath, null) + "_clashes.dxf";
            WriteStandaloneDxf(outPath, clusters);

            return summary + "[ClashClusterer] Записан: " + outPath;
        }
    }
}
