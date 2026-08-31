// ============================================================================
//  Section2Plan.cs — Автоматическая нарезка 3D сеток в 2D поэтажные планы
//  NWD2DWG v3.1 | namespace NWD2DWG.Plugin
//
//  Замещает: Cadmatic eShare 2D (~$2 200/год)
//
//  Алгоритм:
//    1. Проходим по всем треугольникам PolyfaceMesh
//    2. Находим рёбра, пересекающие горизонтальную плоскость Z = sliceZ
//    3. Собираем отрезки пересечения в граф связности
//    4. Трассируем замкнутые полилинии (стены, колонны, проёмы)
//    5. Упрощаем Douglas-Peucker (ε = 5 мм)
//    6. Записываем в DXF как LWPOLYLINE
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    public static class Section2Plan
    {
        // Порог упрощения Douglas-Peucker (мм)
        public const double DefaultDpEps = 5.0;

        // Порог слияния концевых точек отрезков (мм)
        public const double MergeEps = 1.0;

        // -------------------------------------------------------------------------
        // Основной метод: срезает 3D сетку на высоте sliceZ, возвращает полилинии
        // -------------------------------------------------------------------------
        /// <param name="verts">Плоский список координат: X0,Y0,Z0,X1,Y1,Z1,...</param>
        /// <param name="faces">Плоский список граней: i0,i1,i2,i2 (4 инта на треугольник)</param>
        /// <param name="sliceZ">Высота горизонтального среза (те же единицы, что вершины)</param>
        /// <param name="dpEps">Порог упрощения Douglas-Peucker</param>
        /// <returns>Список замкнутых 2D полилиний (каждая — список точек XY)</returns>
        public static List<List<double[]>> Slice(
            List<double> verts,
            List<int>    faces,
            double       sliceZ,
            double       dpEps = DefaultDpEps)
        {
            // Шаг 1: собрать все отрезки пересечения треугольников с плоскостью Z
            var segments = new List<double[]>(); // каждый: [x0,y0, x1,y1]

            int faceCount = faces.Count / 4;
            for (int fi = 0; fi < faceCount; fi++)
            {
                int i0 = faces[fi * 4 + 0];
                int i1 = faces[fi * 4 + 1];
                int i2 = faces[fi * 4 + 2];
                // i3 == i2 для треугольников (квад-хранилище)

                double[] v0 = GetVert(verts, i0);
                double[] v1 = GetVert(verts, i1);
                double[] v2 = GetVert(verts, i2);

                // Найти пересечения рёбер треугольника с плоскостью Z = sliceZ
                var isects = new List<double[]>();
                TryEdge(v0, v1, sliceZ, isects);
                TryEdge(v1, v2, sliceZ, isects);
                TryEdge(v2, v0, sliceZ, isects);

                if (isects.Count >= 2)
                {
                    // Берём первые два — отрезок сечения
                    segments.Add(new double[]
                    {
                        isects[0][0], isects[0][1],
                        isects[1][0], isects[1][1]
                    });
                }
            }

            if (segments.Count == 0)
                return new List<List<double[]>>();

            // Шаг 2: объединить концы отрезков в граф и трассировать полилинии
            var polylines = TracePolylines(segments, MergeEps);

            // Шаг 3: упростить каждую полилинию Douglas-Peucker
            var result = new List<List<double[]>>();
            foreach (var poly in polylines)
            {
                var simplified = DouglasPeucker(poly, dpEps);
                if (simplified.Count >= 2)
                    result.Add(simplified);
            }

            return result;
        }

        // -------------------------------------------------------------------------
        // Пересечение ребра (a→b) с плоскостью Z = z
        // -------------------------------------------------------------------------
        private static void TryEdge(double[] a, double[] b, double z, List<double[]> pts)
        {
            double za = a[2], zb = b[2];
            // Ребро должно пересекать плоскость (разные знаки относительно z)
            if ((za <= z && zb > z) || (zb <= z && za > z))
            {
                double t = (z - za) / (zb - za);
                double x = a[0] + t * (b[0] - a[0]);
                double y = a[1] + t * (b[1] - a[1]);
                pts.Add(new double[] { x, y });
            }
        }

        private static double[] GetVert(List<double> verts, int idx)
        {
            int i = idx * 3;
            return new double[] { verts[i], verts[i + 1], verts[i + 2] };
        }

        // -------------------------------------------------------------------------
        // Трассировка полилиний из набора несвязанных отрезков
        // Объединяем концы через ε-слияние в граф смежности
        // -------------------------------------------------------------------------
        private static List<List<double[]>> TracePolylines(List<double[]> segments, double eps)
        {
            // Квантуем концы отрезков для слияния
            var nodes   = new List<double[]>();   // уникальные узлы [x, y]
            var adj     = new List<List<int>>();  // списки смежности

            // Пространственный хеш вместо линейного перебора всех узлов.
            // На срезе реального этажа это 100k+ отрезков: перебор давал
            // O(N^2) сравнений и функция просто не возвращала управление.
            double cell = Math.Max(eps, 1e-9) * 2.0;
            var grid = new Dictionary<long, List<int>>();
            Func<long, long, long> ckey = (cx, cy) => (cx << 32) ^ (cy & 0xFFFFFFFFL);

            Func<double, double, int> findOrAdd = (x, y) =>
            {
                double eps2 = eps * eps;
                long gx0 = (long)Math.Floor(x / cell), gy0 = (long)Math.Floor(y / cell);
                for (long gx = gx0 - 1; gx <= gx0 + 1; gx++)
                {
                    for (long gy = gy0 - 1; gy <= gy0 + 1; gy++)
                    {
                        List<int> bucket;
                        if (!grid.TryGetValue(ckey(gx, gy), out bucket)) continue;
                        foreach (int i in bucket)
                        {
                            double dx = nodes[i][0] - x, dy = nodes[i][1] - y;
                            if (dx * dx + dy * dy <= eps2) return i;
                        }
                    }
                }
                nodes.Add(new double[] { x, y });
                adj.Add(new List<int>());
                int id = nodes.Count - 1;
                long k = ckey(gx0, gy0);
                List<int> b;
                if (!grid.TryGetValue(k, out b)) { b = new List<int>(); grid[k] = b; }
                b.Add(id);
                return id;
            };

            foreach (var seg in segments)
            {
                int a = findOrAdd(seg[0], seg[1]);
                int b = findOrAdd(seg[2], seg[3]);
                if (a == b) continue; // вырожденный отрезок
                if (!adj[a].Contains(b)) adj[a].Add(b);
                if (!adj[b].Contains(a)) adj[b].Add(a);
            }

            // Трассируем цепочки ПО РЁБРАМ, а не обходом узлов в глубину.
            // DFS по узлам в местах примыкания стен (степень > 2) перепрыгивал
            // в чужую ветку и рисовал диагональные перелёты через весь план,
            // а вся связная компонента схлопывалась в одну полилинию.
            var result   = new List<List<double[]>>();
            var usedEdge = new HashSet<long>();
            Func<int, int, long> ek = (a, b) =>
                a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

            // Сначала стартуем из концов и развилок (степень != 2), затем из
            // оставшихся узлов степени 2 — это замкнутые контуры.
            var starts = new List<int>();
            for (int i = 0; i < nodes.Count; i++) if (adj[i].Count != 2) starts.Add(i);
            for (int i = 0; i < nodes.Count; i++) if (adj[i].Count == 2) starts.Add(i);

            foreach (int s in starts)
            {
                foreach (int first in adj[s])
                {
                    if (usedEdge.Contains(ek(s, first))) continue;

                    var chain = new List<double[]> { nodes[s] };
                    int prev = s, cur = first;
                    usedEdge.Add(ek(prev, cur));
                    chain.Add(nodes[cur]);

                    while (adj[cur].Count == 2 && cur != s)
                    {
                        int next = adj[cur][0] == prev ? adj[cur][1] : adj[cur][0];
                        long e2 = ek(cur, next);
                        if (usedEdge.Contains(e2)) break;
                        usedEdge.Add(e2);
                        chain.Add(nodes[next]);
                        prev = cur; cur = next;
                    }

                    if (chain.Count >= 2) result.Add(chain);
                }
            }

            return result;
        }

        // -------------------------------------------------------------------------
        // Douglas-Peucker упрощение 2D полилинии
        // -------------------------------------------------------------------------
        private static List<double[]> DouglasPeucker(List<double[]> pts, double eps)
        {
            if (pts.Count <= 2) return pts;

            // Найти точку максимального отклонения от хорды [first → last]
            double maxDist = 0;
            int    maxIdx  = 0;

            double[] first = pts[0], last = pts[pts.Count - 1];
            double lx = last[0] - first[0], ly = last[1] - first[1];
            double lineLen2 = lx * lx + ly * ly;

            for (int i = 1; i < pts.Count - 1; i++)
            {
                double d;
                if (lineLen2 < 1e-12)
                {
                    double dx = pts[i][0] - first[0], dy = pts[i][1] - first[1];
                    d = Math.Sqrt(dx * dx + dy * dy);
                }
                else
                {
                    // Перпендикулярное расстояние от точки до прямой
                    double t = ((pts[i][0] - first[0]) * lx + (pts[i][1] - first[1]) * ly) / lineLen2;
                    double px = first[0] + t * lx - pts[i][0];
                    double py = first[1] + t * ly - pts[i][1];
                    d = Math.Sqrt(px * px + py * py);
                }
                if (d > maxDist) { maxDist = d; maxIdx = i; }
            }

            if (maxDist > eps)
            {
                // Рекурсивно упрощаем обе части
                var left  = DouglasPeucker(pts.GetRange(0, maxIdx + 1), eps);
                var right = DouglasPeucker(pts.GetRange(maxIdx, pts.Count - maxIdx), eps);
                // Убираем дублирующую точку стыка
                left.RemoveAt(left.Count - 1);
                left.AddRange(right);
                return left;
            }
            else
            {
                // Все промежуточные точки убираем
                return new List<double[]> { first, last };
            }
        }

        // -------------------------------------------------------------------------
        // Запись 2D плана в DXF (LWPOLYLINE)
        // -------------------------------------------------------------------------
        public static void WriteDxf(StreamWriter w, List<List<double[]>> polylines,
            string layer = "_PLAN", int color = 3)
        {
            if (w == null || polylines == null) return;

            foreach (var poly in polylines)
            {
                if (poly.Count < 2) continue;

                // Проверяем замкнутость
                double[] f = poly[0], l = poly[poly.Count - 1];
                double dfx = f[0] - l[0], dfy = f[1] - l[1];
                bool closed = (dfx * dfx + dfy * dfy) < MergeEps * MergeEps;

                w.WriteLine("0\nLWPOLYLINE");
                w.WriteLine("8\n" + layer);
                w.WriteLine("62\n" + color);
                // Флаг замкнутости
                w.WriteLine("70\n" + (closed ? "1" : "0"));
                // Число вершин
                w.WriteLine("90\n" + poly.Count);

                foreach (var pt in poly)
                {
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "10\n{0:F4}\n20\n{1:F4}", pt[0], pt[1]));
                }
            }
        }

        // -------------------------------------------------------------------------
        // Standalone DXF-файл с 2D планом
        // -------------------------------------------------------------------------
        public static void WriteStandaloneDxf(string outputPath, List<List<double[]>> polylines,
                                              string layer = "_PLAN")
        {
            if (string.IsNullOrEmpty(layer)) layer = "_PLAN";
            // ASCII портил кириллицу в именах слоёв (АР_Стены_План -> ????)
            using (var w = new StreamWriter(outputPath, false, Encoding.Default))
            {
                w.WriteLine("0\nSECTION\n2\nHEADER");
                w.WriteLine("9\n$ACADVER\n1\nAC1015");
                w.WriteLine("0\nENDSEC");
                // Слой обязан быть объявлен в TABLES, иначе строгие читатели DXF
                // отвергнут ссылку на него из ENTITIES
                w.WriteLine("0\nSECTION\n2\nTABLES");
                w.WriteLine("0\nTABLE\n2\nLAYER\n70\n1");
                w.WriteLine("0\nLAYER\n2\n" + layer + "\n70\n0\n62\n3\n6\nCONTINUOUS");
                w.WriteLine("0\nENDTAB");
                w.WriteLine("0\nENDSEC");
                w.WriteLine("0\nSECTION\n2\nENTITIES");
                WriteDxf(w, polylines, layer);
                w.WriteLine("0\nENDSEC");
                w.WriteLine("0\nEOF");
            }
        }

        // -------------------------------------------------------------------------
        // Интеграционная точка: нарезает сетку, пишет _plan.dxf и возвращает лог
        // -------------------------------------------------------------------------
        public static string Process(
            List<double> verts,
            List<int>    faces,
            double       sliceZ,
            string       baseDxfPath,
            double       dpEps = DefaultDpEps)
        {
            if (verts == null || verts.Count == 0 || faces == null || faces.Count == 0)
                return "[Section2Plan] Нет геометрии для нарезки.";

            var polylines = Slice(verts, faces, sliceZ, dpEps);

            string outPath = Path.ChangeExtension(baseDxfPath, null) + "_plan.dxf";
            WriteStandaloneDxf(outPath, polylines);

            return string.Format(
                "[Section2Plan] Срез Z={0:F0}: {1} полилиний → {2}",
                sliceZ, polylines.Count, outPath);
        }
    }
}
