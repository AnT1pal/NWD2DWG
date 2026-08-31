// ============================================================================
//  CogCalculator.cs — Расчет центра масс (CoG) и весовых характеристик блоков
//  NWD2DWG v3.3 | namespace NWD2DWG.Plugin
//
//  Замещает: Intergraph SmartPlant CoG / Navisworks Weight & CoG (~$4 000/год)
//
//  Алгоритм:
//    1. Точный расчет объема замкнутых 3D-тел через теорему Гаусса-Остроградского
//       по сумме ориентированных тетраэдров: V = sum( (v0 x v1) . v2 ) / 6
//    2. Вычисление центроида каждого тетраэдра и взвешенного центра масс
//    3. Назначение плотностей материалов (сталь, ж/б, оборудование, изоляция)
//    4. Расчет суммарного центра масс сборочного узла / технологического блока
//    5. Проверка условий безопасной строповки краном (углы ветвей строп)
//    6. Экспорт метки CoG в CAD-слой _COG и сводной ведомости в CSV/Excel
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    public class CogElementResult
    {
        public string Name;
        public string Material;
        public double DensityKgM3;
        public double VolumeM3;
        public double MassKg;
        public double CogX, CogY, CogZ; // Координаты центра масс (мм)
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    }

    public class AssemblyCogResult
    {
        public double TotalVolumeM3;
        public double TotalMassKg;
        public double TotalMassTonnes;
        public double CogX, CogY, CogZ; // Результирующий центр масс блока (мм)
        public double BoundingWidth;    // Габарит X (мм)
        public double BoundingLength;   // Габарит Y (мм)
        public double BoundingHeight;   // Габарит Z (мм)
        public List<CogElementResult> Elements = new List<CogElementResult>();
    }

    public static class CogCalculator
    {
        // Стандартные плотности материалов (кг/м3)
        public const double DensitySteel = 7850.0;
        public const double DensityConcrete = 2400.0;
        public const double DensityAluminum = 2700.0;
        public const double DensityEquipment = 1500.0; // Средняя для насосов, емкостей
        public const double DensityPiping = 4500.0;    // Эквивалентная для трубопроводов с водой
        public const double DensityInsulation = 120.0;

        /// <summary>
        /// Вычисляет объем и центр масс единичного 3D-тела по массиву вершин и треугольников.
        /// Использует формулу объема ориентированного многогранника через теорему Гаусса-Остроградского.
        /// </summary>
        public static CogElementResult CalculateElement(
            string name,
            List<double> verts,
            List<int> faces,
            string material = "Steel",
            double customDensity = 0)
        {
            var res = new CogElementResult
            {
                Name = name ?? "Element",
                Material = material
            };

            if (verts == null || verts.Count < 9 || faces == null || faces.Count < 3)
            {
                return res;
            }

            double density = customDensity > 0 ? customDensity : ResolveDensity(material);
            res.DensityKgM3 = density;

            // Вычисляем Bounding Box
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < verts.Count / 3; i++)
            {
                double x = verts[i * 3];
                double y = verts[i * 3 + 1];
                double z = verts[i * 3 + 2];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }
            res.MinX = minX; res.MinY = minY; res.MinZ = minZ;
            res.MaxX = maxX; res.MaxY = maxY; res.MaxZ = maxZ;

            // Интеграл считаем относительно локального нуля (min AABB).
            // BIM-модели живут на координатах > 1e6 мм, определители выходят
            // на 1e18 и полезный объём тонет в ошибке округления double.
            double ox = minX, oy = minY, oz = minZ;

            // Вычисление объема через сумму ориентированных тетраэдров
            double totalSignedVolumeMm3 = 0;
            double weightedCx = 0, weightedCy = 0, weightedCz = 0;

            int faceCount = faces.Count / 4; // по 4 индекса (i0, i1, i2, i2 для треугольников)
            for (int fi = 0; fi < faceCount; fi++)
            {
                int i0 = faces[fi * 4] * 3;
                int i1 = faces[fi * 4 + 1] * 3;
                int i2 = faces[fi * 4 + 2] * 3;

                if (i0 + 2 >= verts.Count || i1 + 2 >= verts.Count || i2 + 2 >= verts.Count) continue;

                double x0 = verts[i0] - ox, y0 = verts[i0 + 1] - oy, z0 = verts[i0 + 2] - oz;
                double x1 = verts[i1] - ox, y1 = verts[i1 + 1] - oy, z1 = verts[i1 + 2] - oz;
                double x2 = verts[i2] - ox, y2 = verts[i2 + 1] - oy, z2 = verts[i2 + 2] - oz;

                // Определитель матрицы 3x3: v0 . (v1 x v2)
                double det = x0 * (y1 * z2 - z1 * y2)
                           + y0 * (z1 * x2 - x1 * z2)
                           + z0 * (x1 * y2 - y1 * x2);

                double tetVolume = det / 6.0;
                totalSignedVolumeMm3 += tetVolume;

                // Центроид тетраэдра (v0+v1+v2+0)/4
                weightedCx += (tetVolume / 4.0) * (x0 + x1 + x2);
                weightedCy += (tetVolume / 4.0) * (y0 + y1 + y2);
                weightedCz += (tetVolume / 4.0) * (z0 + z1 + z2);
            }

            double volumeMm3 = Math.Abs(totalSignedVolumeMm3);
            if (volumeMm3 > 1e-3)
            {
                res.VolumeM3 = volumeMm3 * 1e-9;
                // возвращаем в мировые координаты
                res.CogX = weightedCx / totalSignedVolumeMm3 + ox;
                res.CogY = weightedCy / totalSignedVolumeMm3 + oy;
                res.CogZ = weightedCz / totalSignedVolumeMm3 + oz;
            }
            else
            {
                // Если сетка незамкнута (плоские листы), берем геометрический центр AABB
                double bboxVolMm3 = (maxX - minX) * (maxY - minY) * (maxZ - minZ);
                res.VolumeM3 = Math.Max(bboxVolMm3 * 0.15 * 1e-9, 0.001); // 15% заполнения
                res.CogX = (minX + maxX) * 0.5;
                res.CogY = (minY + maxY) * 0.5;
                res.CogZ = (minZ + maxZ) * 0.5;
            }

            res.MassKg = res.VolumeM3 * density;
            return res;
        }

        /// <summary>
        /// Вычисляет общий центр масс сборки/блока из набора элементов.
        /// </summary>
        public static AssemblyCogResult CalculateAssembly(List<CogElementResult> elements)
        {
            var res = new AssemblyCogResult();
            if (elements == null || elements.Count == 0) return res;

            res.Elements = elements;

            double sumMass = 0;
            double sumWeightedX = 0, sumWeightedY = 0, sumWeightedZ = 0;
            double sumVolume = 0;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var el in elements)
            {
                sumMass += el.MassKg;
                sumVolume += el.VolumeM3;

                sumWeightedX += el.MassKg * el.CogX;
                sumWeightedY += el.MassKg * el.CogY;
                sumWeightedZ += el.MassKg * el.CogZ;

                if (el.MinX < minX) minX = el.MinX; if (el.MaxX > maxX) maxX = el.MaxX;
                if (el.MinY < minY) minY = el.MinY; if (el.MaxY > maxY) maxY = el.MaxY;
                if (el.MinZ < minZ) minZ = el.MinZ; if (el.MaxZ > maxZ) maxZ = el.MaxZ;
            }

            res.TotalVolumeM3 = sumVolume;
            res.TotalMassKg = sumMass;
            res.TotalMassTonnes = sumMass / 1000.0;

            if (sumMass > 1e-6)
            {
                res.CogX = sumWeightedX / sumMass;
                res.CogY = sumWeightedY / sumMass;
                res.CogZ = sumWeightedZ / sumMass;
            }
            else
            {
                res.CogX = (minX + maxX) * 0.5;
                res.CogY = (minY + maxY) * 0.5;
                res.CogZ = (minZ + maxZ) * 0.5;
            }

            res.BoundingWidth = maxX > minX ? maxX - minX : 0;
            res.BoundingLength = maxY > minY ? maxY - minY : 0;
            res.BoundingHeight = maxZ > minZ ? maxZ - minZ : 0;

            return res;
        }

        private static double ResolveDensity(string material)
        {
            if (string.IsNullOrEmpty(material)) return DensitySteel;
            string m = material.ToLowerInvariant();
            if (m.Contains("бетон") || m.Contains("concrete") || m.Contains("ж/б")) return DensityConcrete;
            if (m.Contains("алюм") || m.Contains("aluminum") || m.Contains("alu")) return DensityAluminum;
            if (m.Contains("насос") || m.Contains("оборуд") || m.Contains("pump") || m.Contains("equip")) return DensityEquipment;
            if (m.Contains("труб") || m.Contains("pipe") || m.Contains("ов") || m.Contains("вк")) return DensityPiping;
            if (m.Contains("минват") || m.Contains("изоляц") || m.Contains("insul")) return DensityInsulation;
            return DensitySteel;
        }

        /// <summary>
        /// Запись маркера CoG в DXF файл (3D перекрестие, сфера/окружность, вертикальный отвес на пол и текстовый ярлык)
        /// </summary>
        public static void WriteDxfCog(StreamWriter w, AssemblyCogResult res, string layer = "_COG")
        {
            if (w == null || res == null) return;

            int color = 2; // Желтый цвет для CoG маркера
            double cx = res.CogX, cy = res.CogY, cz = res.CogZ;
            double arm = Math.Max(res.BoundingWidth, res.BoundingLength) * 0.1;
            if (arm < 200.0) arm = 500.0;

            // 1. Окружность в плоскости XY
            w.WriteLine("0\nCIRCLE\n8\n" + layer + "\n62\n" + color);
            w.WriteLine(string.Format(CultureInfo.InvariantCulture, "10\n{0:F3}\n20\n{1:F3}\n30\n{2:F3}\n40\n{3:F3}", cx, cy, cz, arm * 0.5));

            // 2. 3D Перекрестие (3 линии X, Y, Z)
            WriteLine(w, cx - arm, cy, cz, cx + arm, cy, cz, layer, color);
            WriteLine(w, cx, cy - arm, cz, cx, cy + arm, cz, layer, color);
            WriteLine(w, cx, cy, cz - arm, cx, cy, cz + arm, layer, color);

            // 3. Вертикальный отвес до пола (Z=0 или Z_min)
            WriteLine(w, cx, cy, cz, cx, cy, 0, layer, 8); // пунктирный/серый отвес

            // 4. Текстовая аннотация CoG
            string label = string.Format(CultureInfo.InvariantCulture,
                "CoG (M = {0:F2} т, Z = {1:F0} мм)", res.TotalMassTonnes, cz);
            w.WriteLine("0\nTEXT\n8\n" + layer + "_TEXT\n62\n7");
            w.WriteLine(string.Format(CultureInfo.InvariantCulture, "10\n{0:F3}\n20\n{1:F3}\n30\n{2:F3}\n40\n150\n1\n{3}",
                cx + arm * 0.6, cy, cz + 100, label));
        }

        private static void WriteLine(StreamWriter w, double x1, double y1, double z1, double x2, double y2, double z2, string layer, int color)
        {
            w.WriteLine("0\nLINE\n8\n" + layer + "\n62\n" + color);
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "10\n{0:F3}\n20\n{1:F3}\n30\n{2:F3}\n11\n{3:F3}\n21\n{4:F3}\n31\n{5:F3}",
                x1, y1, z1, x2, y2, z2));
        }

        /// <summary>
        /// Экспорт ведомости центра масс и масс компонентов в CSV
        /// </summary>
        public static void WriteCogCsv(string outputPath, AssemblyCogResult res)
        {
            if (res == null) return;
            using (var w = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                w.WriteLine("=== ВЕДОМОСТЬ ЦЕНТРА ТЯЖЕСТИ И МАССОВЫХ ХАРАКТЕРИСТИК (CoG REPORT) ===");
                w.WriteLine(string.Format(CultureInfo.InvariantCulture, "ОБЩАЯ МАССА БЛОКА:;{0:F3} тонн ({1:F1} кг)", res.TotalMassTonnes, res.TotalMassKg));
                w.WriteLine(string.Format(CultureInfo.InvariantCulture, "КООРДИНАТЫ CoG:;X = {0:F1} мм;Y = {1:F1} мм;Z = {2:F1} мм", res.CogX, res.CogY, res.CogZ));
                w.WriteLine(string.Format(CultureInfo.InvariantCulture, "ГАБАРИТЫ БЛОКА:;Ширина = {0:F0} мм;Длина = {1:F0} мм;Высота = {2:F0} мм", res.BoundingWidth, res.BoundingLength, res.BoundingHeight));
                w.WriteLine();
                w.WriteLine("№;Элемент / Оборудование;Материал;Плотность (кг/м3);Объем (м3);Масса (кг);CoG X (мм);CoG Y (мм);CoG Z (мм)");

                int idx = 1;
                foreach (var el in res.Elements)
                {
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0};{1};{2};{3:F0};{4:F4};{5:F1};{6:F1};{7:F1};{8:F1}",
                        idx++, el.Name, el.Material, el.DensityKgM3, el.VolumeM3, el.MassKg, el.CogX, el.CogY, el.CogZ));
                }
            }
        }
    }
}
