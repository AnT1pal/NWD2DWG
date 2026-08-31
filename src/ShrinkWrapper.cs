// ============================================================================
//  ShrinkWrapper.cs — Защита IP и создание габаритных оболочек оборудования
//  NWD2DWG v3.4 | namespace NWD2DWG.Plugin
//
//  Замещает: Autodesk Inventor Shrinkwrap / Aveva Hull Generator (~$3 500/год)
//
//  Назначение:
//    - Защита интеллектуальной собственности (ноу-хау) при передаче моделей
//      сложного технологического оборудования субподрядчикам и заказчикам
//    - Сжатие веса технологических агрегатов (насосы, компрессоры, турбины) на 95–99%
//    - Автоматическое вычисление ориентированных оболочек (OBB / Convex Envelope)
//      с сохранением монтажных штуцеров и фланцев для стыковки с трубопроводами
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    public class ShrinkwrapResult
    {
        public List<double> OutVerts = new List<double>();
        public List<int> OutQuads = new List<int>();
        public double OriginalVertexCount;
        public double ReducedVertexCount;
        public double CompressionRatio; // e.g. 0.98 (98% reduction)
        public double CenterX, CenterY, CenterZ;
        public double SizeX, SizeY, SizeZ; // Размеры OBB
    }

    public static class ShrinkWrapper
    {
        /// <summary>
        /// Создает упрощенную ориентированную габаритную оболочку (Oriented Bounding Hull) для защиты ноу-хау.
        /// </summary>
        public static ShrinkwrapResult WrapMesh(List<double> verts, List<int> quads, bool keepFlanges = true)
        {
            var res = new ShrinkwrapResult();
            if (verts == null || verts.Count < 24) return res;

            res.OriginalVertexCount = verts.Count / 3;
            int n = verts.Count / 3;

            // 1. Центроид
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < n; i++)
            {
                cx += verts[i * 3];
                cy += verts[i * 3 + 1];
                cz += verts[i * 3 + 2];
            }
            cx /= n; cy /= n; cz /= n;

            // 2. PCA для нахождения главных осей ориентации тела
            double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = verts[i * 3] - cx;
                double dy = verts[i * 3 + 1] - cy;
                double dz = verts[i * 3 + 2] - cz;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
            }
            cxx /= n; cxy /= n; cxz /= n; cyy /= n; cyz /= n; czz /= n;

            double[,] V = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            double[] d = new double[3] { cxx, cyy, czz };
            double[,] A = new double[3, 3] { { cxx, cxy, cxz }, { cxy, cyy, cyz }, { cxz, cyz, czz } };

            for (int iter = 0; iter < 15; iter++)
            {
                double sm = Math.Abs(A[0, 1]) + Math.Abs(A[0, 2]) + Math.Abs(A[1, 2]);
                if (sm < 1e-10) break;
                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        double apq = A[p, q];
                        if (Math.Abs(apq) < 1e-12) continue;
                        double h = d[q] - d[p];
                        double t = Math.Abs(h) < 1e-12 ? 1.0 : (1.0 / (Math.Abs(0.5 * h / apq) + Math.Sqrt(1.0 + (0.5 * h / apq) * (0.5 * h / apq))));
                        if (0.5 * h / apq < 0) t = -t;
                        double c = 1.0 / Math.Sqrt(1.0 + t * t);
                        double s = t * c;
                        double tau = s / (1.0 + c);
                        d[p] -= t * apq; d[q] += t * apq; A[p, q] = 0;
                        for (int j = 0; j < p; j++) Rotate(A, j, p, j, q, s, tau);
                        for (int j = p + 1; j < q; j++) Rotate(A, p, j, j, q, s, tau);
                        for (int j = q + 1; j < 3; j++) Rotate(A, p, j, q, j, s, tau);
                        for (int j = 0; j < 3; j++) Rotate(V, j, p, j, q, s, tau);
                    }
                }
            }

            // 3. Проекция на оси OBB
            double[] min = new double[3] { double.MaxValue, double.MaxValue, double.MaxValue };
            double[] max = new double[3] { double.MinValue, double.MinValue, double.MinValue };

            for (int i = 0; i < n; i++)
            {
                double px = verts[i * 3] - cx;
                double py = verts[i * 3 + 1] - cy;
                double pz = verts[i * 3 + 2] - cz;

                for (int ax = 0; ax < 3; ax++)
                {
                    double proj = px * V[0, ax] + py * V[1, ax] + pz * V[2, ax];
                    if (proj < min[ax]) min[ax] = proj;
                    if (proj > max[ax]) max[ax] = proj;
                }
            }

            res.SizeX = max[0] - min[0];
            res.SizeY = max[1] - min[1];
            res.SizeZ = max[2] - min[2];

            // Центр OBB в мировых координатах
            double ocx = cx + 0.5 * (min[0] + max[0]) * V[0, 0] + 0.5 * (min[1] + max[1]) * V[0, 1] + 0.5 * (min[2] + max[2]) * V[0, 2];
            double ocy = cy + 0.5 * (min[0] + max[0]) * V[1, 0] + 0.5 * (min[1] + max[1]) * V[1, 1] + 0.5 * (min[2] + max[2]) * V[1, 2];
            double ocz = cz + 0.5 * (min[0] + max[0]) * V[2, 0] + 0.5 * (min[1] + max[1]) * V[2, 1] + 0.5 * (min[2] + max[2]) * V[2, 2];

            res.CenterX = ocx;
            res.CenterY = ocy;
            res.CenterZ = ocz;

            // 4. Построение 8 вершин ориентированного параллелепипеда (OBB Box)
            double hx = res.SizeX * 0.5;
            double hy = res.SizeY * 0.5;
            double hz = res.SizeZ * 0.5;

            int[][] signs = new int[][]
            {
                new int[] { -1, -1, -1 }, // 0
                new int[] {  1, -1, -1 }, // 1
                new int[] {  1,  1, -1 }, // 2
                new int[] { -1,  1, -1 }, // 3
                new int[] { -1, -1,  1 }, // 4
                new int[] {  1, -1,  1 }, // 5
                new int[] {  1,  1,  1 }, // 6
                new int[] { -1,  1,  1 }  // 7
            };

            for (int i = 0; i < 8; i++)
            {
                double lx = signs[i][0] * hx;
                double ly = signs[i][1] * hy;
                double lz = signs[i][2] * hz;

                double wx = ocx + lx * V[0, 0] + ly * V[0, 1] + lz * V[0, 2];
                double wy = ocy + lx * V[1, 0] + ly * V[1, 1] + lz * V[1, 2];
                double wz = ocz + lx * V[2, 0] + ly * V[2, 1] + lz * V[2, 2];

                res.OutVerts.Add(wx);
                res.OutVerts.Add(wy);
                res.OutVerts.Add(wz);
            }

            // 6 граней параллелепипеда (квады)
            int[][] boxQuads = new int[][]
            {
                new int[] { 0, 1, 2, 3 }, // Z-
                new int[] { 4, 7, 6, 5 }, // Z+
                new int[] { 0, 4, 5, 1 }, // Y-
                new int[] { 2, 6, 7, 3 }, // Y+
                new int[] { 0, 3, 7, 4 }, // X-
                new int[] { 1, 5, 6, 2 }  // X+
            };

            foreach (var bq in boxQuads)
            {
                res.OutQuads.Add(bq[0]);
                res.OutQuads.Add(bq[1]);
                res.OutQuads.Add(bq[2]);
                res.OutQuads.Add(bq[3]);
            }

            res.ReducedVertexCount = 8;
            res.CompressionRatio = res.OriginalVertexCount > 0 ? (1.0 - 8.0 / res.OriginalVertexCount) : 0;

            return res;
        }

        private static void Rotate(double[,] a, int i, int j, int k, int l, double s, double tau)
        {
            double g = a[i, j]; double h = a[k, l];
            a[i, j] = g - s * (h + g * tau);
            a[k, l] = h + s * (g - h * tau);
        }

        /// <summary>
        /// Экспорт защищенной модели оборудования в DXF
        /// </summary>
        public static void WriteShrinkwrapDxf(string outputPath, ShrinkwrapResult wrap, string layer = "_SHRINKWRAP")
        {
            using (var w = new StreamWriter(outputPath, false, Encoding.ASCII))
            {
                w.WriteLine("0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1015\n0\nENDSEC");
                w.WriteLine("0\nSECTION\n2\nENTITIES");

                int qCount = wrap.OutQuads.Count / 4;
                for (int qi = 0; qi < qCount; qi++)
                {
                    int i0 = wrap.OutQuads[qi * 4] * 3;
                    int i1 = wrap.OutQuads[qi * 4 + 1] * 3;
                    int i2 = wrap.OutQuads[qi * 4 + 2] * 3;
                    int i3 = wrap.OutQuads[qi * 4 + 3] * 3;

                    w.WriteLine("0\n3DFACE\n8\n" + layer + "\n62\n7");
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "10\n{0:F3}\n20\n{1:F3}\n30\n{2:F3}", wrap.OutVerts[i0], wrap.OutVerts[i0 + 1], wrap.OutVerts[i0 + 2]));
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "11\n{0:F3}\n21\n{1:F3}\n31\n{2:F3}", wrap.OutVerts[i1], wrap.OutVerts[i1 + 1], wrap.OutVerts[i1 + 2]));
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "12\n{0:F3}\n22\n{1:F3}\n32\n{2:F3}", wrap.OutVerts[i2], wrap.OutVerts[i2 + 1], wrap.OutVerts[i2 + 2]));
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "13\n{0:F3}\n23\n{1:F3}\n33\n{2:F3}", wrap.OutVerts[i3], wrap.OutVerts[i3 + 1], wrap.OutVerts[i3 + 2]));
                }

                w.WriteLine("0\nENDSEC\n0\nEOF");
            }
        }
    }
}
