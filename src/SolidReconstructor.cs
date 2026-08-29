using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;

namespace NWD2DWG.Plugin
{
    public enum SolidType { None, Cylinder, Box }

    public class SolidResult
    {
        public SolidType Type;
        public double CenterX, CenterY, CenterZ;
        public double AxisX, AxisY, AxisZ;
        public double Radius;
        public double Height;
        public double Width, Depth;
        public double Confidence;
    }

    public static class SolidReconstructor
    {
        // Определение простых примитивов (цилиндр, параллелепипед) из полигональной сетки
        public static SolidResult TryReconstruct(List<double> verts, List<int> quads)
        {
            SolidResult result = new SolidResult { Type = SolidType.None };
            int vertexCount = verts.Count / 3;
            if (vertexCount < 8) return result;

            // 1. Вычисление центроида
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                cx += verts[i * 3];
                cy += verts[i * 3 + 1];
                cz += verts[i * 3 + 2];
            }
            cx /= vertexCount;
            cy /= vertexCount;
            cz /= vertexCount;

            // 2. Построение ковариационной матрицы (3x3)
            double[,] cov = new double[3, 3];
            for (int i = 0; i < vertexCount; i++)
            {
                double dx = verts[i * 3] - cx;
                double dy = verts[i * 3 + 1] - cy;
                double dz = verts[i * 3 + 2] - cz;

                cov[0, 0] += dx * dx;
                cov[0, 1] += dx * dy;
                cov[0, 2] += dx * dz;
                cov[1, 0] += dy * dx;
                cov[1, 1] += dy * dy;
                cov[1, 2] += dy * dz;
                cov[2, 0] += dz * dx;
                cov[2, 1] += dz * dy;
                cov[2, 2] += dz * dz;
            }
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    cov[r, c] /= vertexCount;

            // 3. Метод Якоби для нахождения собственных векторов (PCA)
            double[,] evecs = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            double[] evals = new double[3];
            Jacobi3x3(cov, evals, evecs);

            // Сортировка собственных значений (по убыванию) и векторов
            SortEigen(evals, evecs);

            // Проверка на цилиндр
            // Ось цилиндра - это собственный вектор с наиболее отличимым разбросом. Используем ось с наибольшим (или наименьшим) spread, для простоты берём 0-ю
            double axisX = evecs[0, 0], axisY = evecs[1, 0], axisZ = evecs[2, 0];
            
            // 4. Проекция на плоскость, перпендикулярную оси
            double radiusSum = 0;
            List<double> radii = new List<double>(vertexCount);
            double minExtent = double.MaxValue;
            double maxExtent = double.MinValue;
            
            for (int i = 0; i < vertexCount; i++)
            {
                double px = verts[i * 3] - cx;
                double py = verts[i * 3 + 1] - cy;
                double pz = verts[i * 3 + 2] - cz;

                // Проекция на ось
                double dotAxis = px * axisX + py * axisY + pz * axisZ;
                if (dotAxis < minExtent) minExtent = dotAxis;
                if (dotAxis > maxExtent) maxExtent = dotAxis;

                // 5. Вычисление радиального расстояния до оси
                double rx = px - dotAxis * axisX;
                double ry = py - dotAxis * axisY;
                double rz = pz - dotAxis * axisZ;
                double r = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                radii.Add(r);
                radiusSum += r;
            }

            // 1. Проверка на параллелепипед (коробку)
            // Проецируем на 3 главные оси, ищем бимодальное распределение (кластеризация по 2 краям на каждой оси)
            double boxConfidenceSum = 0;
            double[] extents = new double[3];

            for (int axisIdx = 0; axisIdx < 3; axisIdx++)
            {
                double ax = evecs[0, axisIdx];
                double ay = evecs[1, axisIdx];
                double az = evecs[2, axisIdx];

                double minProj = double.MaxValue;
                double maxProj = double.MinValue;
                for (int i = 0; i < vertexCount; i++)
                {
                    double px = verts[i * 3] - cx;
                    double py = verts[i * 3 + 1] - cy;
                    double pz = verts[i * 3 + 2] - cz;
                    double dot = px * ax + py * ay + pz * az;
                    if (dot < minProj) minProj = dot;
                    if (dot > maxProj) maxProj = dot;
                }

                extents[axisIdx] = maxProj - minProj;

                // Проверка бимодальности
                double errorSum = 0;
                for (int i = 0; i < vertexCount; i++)
                {
                    double px = verts[i * 3] - cx;
                    double py = verts[i * 3 + 1] - cy;
                    double pz = verts[i * 3 + 2] - cz;
                    double dot = px * ax + py * ay + pz * az;
                    
                    double distMin = Math.Abs(dot - minProj);
                    double distMax = Math.Abs(dot - maxProj);
                    double dist = Math.Min(distMin, distMax);
                    errorSum += dist;
                }
                double avgError = errorSum / vertexCount;
                double axisThreshold = (maxProj - minProj) * 0.15;
                if (axisThreshold < 1e-4) axisThreshold = 1e-4;

                if (maxProj - minProj > 1e-6)
                {
                    double c = 1.0 - (avgError / axisThreshold);
                    if (c < 0) c = 0;
                    boxConfidenceSum += c;
                }
                else
                {
                    boxConfidenceSum += 1.0;
                }
            }

            double boxConfidence = boxConfidenceSum / 3.0;

            // Если вершин ровно 8 или бимодальность очень высокая — это коробка
            if ((vertexCount <= 8 && boxConfidence > 0.6) || boxConfidence > 0.85)
            {
                result.Type = SolidType.Box;
                result.CenterX = cx;
                result.CenterY = cy;
                result.CenterZ = cz;
                result.AxisX = evecs[0, 0];
                result.AxisY = evecs[1, 0];
                result.AxisZ = evecs[2, 0];
                result.Height = extents[0];
                result.Width = extents[1];
                result.Depth = extents[2];
                result.Confidence = boxConfidence;
                return result;
            }

            // 2. Проверка на цилиндр (требуется достаточное число вершин для окружности)
            if (vertexCount >= 12)
            {
                double meanRadius = radiusSum / vertexCount;
                double varSum = 0;
                foreach (double r in radii)
                {
                    varSum += (r - meanRadius) * (r - meanRadius);
                }
                double stdDevRadius = Math.Sqrt(varSum / vertexCount);

                double cylConfidence = 0;
                if (meanRadius > 0)
                {
                    double cv = stdDevRadius / meanRadius;
                    if (cv < 0.05) cylConfidence = 1.0 - (cv / 0.05);
                }

                if (cylConfidence > 0.7)
                {
                    result.Type = SolidType.Cylinder;
                    result.CenterX = cx;
                    result.CenterY = cy;
                    result.CenterZ = cz;
                    result.AxisX = axisX;
                    result.AxisY = axisY;
                    result.AxisZ = axisZ;
                    result.Radius = meanRadius;
                    result.Height = maxExtent - minExtent;
                    result.Confidence = cylConfidence;
                    return result;
                }
            }

            if (boxConfidence > 0.7)
            {
                result.Type = SolidType.Box;
                result.CenterX = cx;
                result.CenterY = cy;
                result.CenterZ = cz;
                result.AxisX = evecs[0, 0];
                result.AxisY = evecs[1, 0];
                result.AxisZ = evecs[2, 0];
                result.Height = extents[0];
                result.Width = extents[1];
                result.Depth = extents[2];
                result.Confidence = boxConfidence;
                return result;
            }

            return result;
        }

        private static void Jacobi3x3(double[,] a, double[] d, double[,] v)
        {
            int n = 3;
            for (int p = 0; p < n; p++)
            {
                for (int q = 0; q < n; q++) v[p, q] = (p == q) ? 1.0 : 0.0;
                d[p] = a[p, p];
            }

            int maxRot = 50;
            for (int i = 0; i < maxRot; i++)
            {
                double sm = 0.0;
                for (int p = 0; p < n - 1; p++)
                    for (int q = p + 1; q < n; q++)
                        sm += Math.Abs(a[p, q]);
                if (sm == 0) break;

                for (int p = 0; p < n - 1; p++)
                {
                    for (int q = p + 1; q < n; q++)
                    {
                        double h = d[q] - d[p];
                        if (Math.Abs(a[p, q]) > 1e-12)
                        {
                            double t;
                            if (Math.Abs(h) > 1e-12)
                            {
                                double theta = 0.5 * h / a[p, q];
                                t = 1.0 / (Math.Abs(theta) + Math.Sqrt(1.0 + theta * theta));
                                if (theta < 0.0) t = -t;
                            }
                            else
                            {
                                t = 1.0;
                            }

                            double c = 1.0 / Math.Sqrt(1 + t * t);
                            double s = t * c;
                            double tau = s / (1.0 + c);

                            h = t * a[p, q];
                            d[p] -= h;
                            d[q] += h;
                            a[p, q] = 0.0;

                            for (int j = 0; j <= p - 1; j++) { double g = a[j, p]; double h2 = a[j, q]; a[j, p] = g - s * (h2 + g * tau); a[j, q] = h2 + s * (g - h2 * tau); }
                            for (int j = p + 1; j <= q - 1; j++) { double g = a[p, j]; double h2 = a[j, q]; a[p, j] = g - s * (h2 + g * tau); a[j, q] = h2 + s * (g - h2 * tau); }
                            for (int j = q + 1; j < n; j++) { double g = a[p, j]; double h2 = a[q, j]; a[p, j] = g - s * (h2 + g * tau); a[q, j] = h2 + s * (g - h2 * tau); }
                            for (int j = 0; j < n; j++) { double g = v[j, p]; double h2 = v[j, q]; v[j, p] = g - s * (h2 + g * tau); v[j, q] = h2 + s * (g - h2 * tau); }
                        }
                    }
                }
            }
        }

        private static void SortEigen(double[] evals, double[,] evecs)
        {
            for (int i = 0; i < 2; i++)
            {
                int maxIdx = i;
                for (int j = i + 1; j < 3; j++)
                {
                    if (evals[j] > evals[maxIdx]) maxIdx = j;
                }
                if (maxIdx != i)
                {
                    double tmp = evals[i];
                    evals[i] = evals[maxIdx];
                    evals[maxIdx] = tmp;

                    for (int k = 0; k < 3; k++)
                    {
                        double tv = evecs[k, i];
                        evecs[k, i] = evecs[k, maxIdx];
                        evecs[k, maxIdx] = tv;
                    }
                }
            }
        }

        public static void WriteSolidDxf(StreamWriter w, SolidResult solid, string layer, int rgb)
        {
            // Экспорт примитивов в DXF 3DFACE
            if (solid.Type == SolidType.Cylinder)
            {
                int segments = 16;
                double az = solid.AxisZ, ax = solid.AxisX, ay = solid.AxisY;
                
                double len = Math.Sqrt(ax * ax + ay * ay + az * az);
                if (len > 0) { ax /= len; ay /= len; az /= len; }

                double uX = 1, uY = 0, uZ = 0;
                if (Math.Abs(ax) > 0.9) { uX = 0; uY = 1; uZ = 0; }
                
                double vX = uY * az - uZ * ay;
                double vY = uZ * ax - uX * az;
                double vZ = uX * ay - uY * ax;
                
                double vLen = Math.Sqrt(vX * vX + vY * vY + vZ * vZ);
                vX /= vLen; vY /= vLen; vZ /= vLen;

                uX = ay * vZ - az * vY;
                uY = az * vX - ax * vZ;
                uZ = ax * vY - ay * vX;

                double h2 = solid.Height / 2.0;
                double botX = solid.CenterX - ax * h2;
                double botY = solid.CenterY - ay * h2;
                double botZ = solid.CenterZ - az * h2;
                
                double topX = solid.CenterX + ax * h2;
                double topY = solid.CenterY + ay * h2;
                double topZ = solid.CenterZ + az * h2;

                List<double[]> circleTop = new List<double[]>();
                List<double[]> circleBot = new List<double[]>();
                
                for (int i = 0; i < segments; i++)
                {
                    double angle = 2.0 * Math.PI * i / segments;
                    double c = Math.Cos(angle) * solid.Radius;
                    double s = Math.Sin(angle) * solid.Radius;

                    double dx = c * uX + s * vX;
                    double dy = c * uY + s * vY;
                    double dz = c * uZ + s * vZ;

                    circleTop.Add(new double[] { topX + dx, topY + dy, topZ + dz });
                    circleBot.Add(new double[] { botX + dx, botY + dy, botZ + dz });
                }

                for (int i = 0; i < segments; i++)
                {
                    int next = (i + 1) % segments;
                    // Стенки
                    Write3DFace(w, layer, rgb, circleBot[i], circleBot[next], circleTop[next], circleTop[i]);
                    // Торцы (для простоты аппроксимируются треугольниками от края)
                    Write3DFace(w, layer, rgb, botX, botY, botZ, circleBot[i], circleBot[next], circleBot[next]);
                    Write3DFace(w, layer, rgb, topX, topY, topZ, circleTop[next], circleTop[i], circleTop[i]);
                }
            }
            else if (solid.Type == SolidType.Box)
            {
                double h2 = solid.Height / 2.0;
                double w2 = solid.Width / 2.0;
                double d2 = solid.Depth / 2.0;

                double cx = solid.CenterX;
                double cy = solid.CenterY;
                double cz = solid.CenterZ;

                double[][] pts = new double[][]
                {
                    new double[] { cx - w2, cy - d2, cz - h2 },
                    new double[] { cx + w2, cy - d2, cz - h2 },
                    new double[] { cx + w2, cy + d2, cz - h2 },
                    new double[] { cx - w2, cy + d2, cz - h2 },
                    new double[] { cx - w2, cy - d2, cz + h2 },
                    new double[] { cx + w2, cy - d2, cz + h2 },
                    new double[] { cx + w2, cy + d2, cz + h2 },
                    new double[] { cx - w2, cy + d2, cz + h2 }
                };

                int[][] faces = new int[][]
                {
                    new int[] { 0, 1, 2, 3 }, // bottom
                    new int[] { 4, 5, 6, 7 }, // top
                    new int[] { 0, 1, 5, 4 }, // front
                    new int[] { 1, 2, 6, 5 }, // right
                    new int[] { 2, 3, 7, 6 }, // back
                    new int[] { 3, 0, 4, 7 }  // left
                };

                foreach (var face in faces)
                {
                    double[] p0 = pts[face[0]];
                    double[] p1 = pts[face[1]];
                    double[] p2 = pts[face[2]];
                    double[] p3 = pts[face[3]];
                    
                    Write3DFace(w, layer, rgb, p0, p1, p2, p2);
                    Write3DFace(w, layer, rgb, p0, p2, p3, p3);
                }
            }
        }
        
        // Вспомогательный метод для записи грани в DXF
        private static void Write3DFace(StreamWriter w, string layer, int color, double[] p1, double[] p2, double[] p3, double[] p4)
        {
            var culture = CultureInfo.InvariantCulture;
            w.WriteLine("0");
            w.WriteLine("3DFACE");
            w.WriteLine("8");
            w.WriteLine(layer);
            if (color > 0)
            {
                w.WriteLine("420");
                w.WriteLine(color.ToString(culture));
            }
            else
            {
                w.WriteLine("62");
                w.WriteLine("7");
            }
            w.WriteLine("10");
            w.WriteLine(p1[0].ToString("G12", culture));
            w.WriteLine("20");
            w.WriteLine(p1[1].ToString("G12", culture));
            w.WriteLine("30");
            w.WriteLine(p1[2].ToString("G12", culture));
            w.WriteLine("11");
            w.WriteLine(p2[0].ToString("G12", culture));
            w.WriteLine("21");
            w.WriteLine(p2[1].ToString("G12", culture));
            w.WriteLine("31");
            w.WriteLine(p2[2].ToString("G12", culture));
            w.WriteLine("12");
            w.WriteLine(p3[0].ToString("G12", culture));
            w.WriteLine("22");
            w.WriteLine(p3[1].ToString("G12", culture));
            w.WriteLine("32");
            w.WriteLine(p3[2].ToString("G12", culture));
            w.WriteLine("13");
            w.WriteLine(p4[0].ToString("G12", culture));
            w.WriteLine("23");
            w.WriteLine(p4[1].ToString("G12", culture));
            w.WriteLine("33");
            w.WriteLine(p4[2].ToString("G12", culture));
        }
        
        // Вспомогательный метод с 3+ точками, если P4 нет, дублируем P3
        private static void Write3DFace(StreamWriter w, string layer, int color, double x1, double y1, double z1, double[] p2, double[] p3, double[] p4)
        {
            Write3DFace(w, layer, color, new double[]{x1, y1, z1}, p2, p3, p4);
        }
    }
}
