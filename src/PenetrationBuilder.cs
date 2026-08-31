// ============================================================================
//  PenetrationBuilder.cs — Автоматическая расстановка гильз и проёмов
//  NWD2DWG v3.2 | namespace NWD2DWG.Plugin
//
//  Замещает: MagiCAD Opening Provision (~$2 500/год)
//
//  Алгоритм:
//    1. Принимает список осей труб (из PipeTracer) и список плоскостей
//       стен/плит (AABB-ориентированные плоскости из сетки)
//    2. Для каждой трубной оси: Ray-Plane Intersection с каждой плоскостью
//    3. Если пересечение найдено → вычисляем:
//       - Центр гильзы (X, Y, Z)
//       - Диаметр гильзы = DN + 50 мм (ГОСТ Р 21.1101-2013)
//       - Нормаль плоскости (направление сверления)
//    4. Группируем по конструктивному элементу (стена / перекрытие)
//    5. Записываем в DXF слой _OPENINGS + Excel-спецификацию
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    // -------------------------------------------------------------------------
    // Ось трубы (результат PipeTracer или ручной ввод)
    // -------------------------------------------------------------------------
    public class PipeAxis
    {
        public double Ax, Ay, Az; // начало оси
        public double Bx, By, Bz; // конец оси
        public double DN;          // условный диаметр (мм)
        public string SystemName;  // "ОВ-101" / "ВК-205" и т.д.

        // Направление вектора оси (нормированное)
        public void GetDirection(out double dx, out double dy, out double dz)
        {
            dx = Bx - Ax; dy = By - Ay; dz = Bz - Az;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len > 1e-9) { dx /= len; dy /= len; dz /= len; }
        }
    }

    // -------------------------------------------------------------------------
    // Плоскость конструктивного элемента (стена / перекрытие)
    // -------------------------------------------------------------------------
    public class ConstructionPlane
    {
        public double Nx, Ny, Nz; // нормаль плоскости (единичная)
        public double D;           // расстояние от начала координат (Nx*X+Ny*Y+Nz*Z = D)
        public double Thickness;   // толщина конструкции (мм), для определения длины гильзы
        public string ElementName; // "Стена_ОС-01" / "Перекрытие_Эт3"
        public string ElementType; // "Wall" / "Floor" / "Ceiling"

        // AABB конструкции (для проверки, что пересечение внутри элемента)
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    }

    // -------------------------------------------------------------------------
    // Результирующая гильза / проём
    // -------------------------------------------------------------------------
    public class Penetration
    {
        public double Cx, Cy, Cz;   // центр гильзы
        public double Nx, Ny, Nz;   // нормаль (направление сверления)
        public double SleeveD;       // диаметр гильзы (DN + 50 мм)
        public double SleeveL;       // длина гильзы = толщина конструкции
        public double DN;            // условный диаметр трубы
        public string PipeSystem;    // имя системы трубопровода
        public string Element;       // имя конструктивного элемента
        public string ElementType;
        public int    Id;
    }

    // -------------------------------------------------------------------------
    // Основной класс
    // -------------------------------------------------------------------------
    public static class PenetrationBuilder
    {
        // Запас гильзы по ГОСТ Р 21.1101: DN + 50 мм (каждую сторону +25)
        public const double SleeveClearance = 50.0;

        /// <summary>
        /// Находит все пересечения трубных осей с конструктивными плоскостями.
        /// </summary>
        public static List<Penetration> Build(
            List<PipeAxis>         pipes,
            List<ConstructionPlane> planes)
        {
            return Build(pipes, planes, null, 0.0);
        }

        /// <summary>
        /// gapRule — зазор гильзы по DN (СП 73.13330), extension — выпуск за конструкцию.
        /// Раньше и то и другое было зашито константой SleeveClearance.
        /// </summary>
        public static List<Penetration> Build(
            List<PipeAxis>          pipes,
            List<ConstructionPlane> planes,
            Func<double, double>    gapRule,
            double                  extensionMm)
        {
            var result = new List<Penetration>();
            int id = 1;

            foreach (var pipe in pipes)
            {
                double dx, dy, dz;
                pipe.GetDirection(out dx, out dy, out dz);

                double rayLen = Math.Sqrt(
                    (pipe.Bx - pipe.Ax) * (pipe.Bx - pipe.Ax) +
                    (pipe.By - pipe.Ay) * (pipe.By - pipe.Ay) +
                    (pipe.Bz - pipe.Az) * (pipe.Bz - pipe.Az));

                foreach (var plane in planes)
                {
                    // Ray-Plane Intersection:
                    // t = (D - dot(N, rayOrigin)) / dot(N, rayDir)
                    double denom = plane.Nx * dx + plane.Ny * dy + plane.Nz * dz;
                    if (Math.Abs(denom) < 1e-9) continue; // параллельно плоскости

                    double t = (plane.D - (plane.Nx * pipe.Ax + plane.Ny * pipe.Ay + plane.Nz * pipe.Az))
                               / denom;

                    // Пересечение должно быть внутри отрезка оси трубы
                    if (t < 0 || t > rayLen) continue;

                    // Точка пересечения
                    double px = pipe.Ax + t * dx;
                    double py = pipe.Ay + t * dy;
                    double pz = pipe.Az + t * dz;

                    // Проверяем, что точка внутри AABB конструкции
                    double eps = 50.0; // допуск 50 мм
                    if (px < plane.MinX - eps || px > plane.MaxX + eps) continue;
                    if (py < plane.MinY - eps || py > plane.MaxY + eps) continue;
                    if (pz < plane.MinZ - eps || pz > plane.MaxZ + eps) continue;

                    result.Add(new Penetration
                    {
                        Id          = id++,
                        Cx          = px, Cy = py, Cz = pz,
                        Nx          = plane.Nx, Ny = plane.Ny, Nz = plane.Nz,
                        DN          = pipe.DN,
                        SleeveD     = pipe.DN + (gapRule != null ? gapRule(pipe.DN) : SleeveClearance),
                        SleeveL     = (plane.Thickness > 0 ? plane.Thickness : 200.0) + 2.0 * extensionMm,
                        PipeSystem  = pipe.SystemName,
                        Element     = plane.ElementName,
                        ElementType = plane.ElementType
                    });
                }
            }

            return result;
        }

        // -------------------------------------------------------------------------
        // Запись DXF: каждая гильза = окружность (CIRCLE) в слое _OPENINGS
        // -------------------------------------------------------------------------
        public static void WriteDxf(StreamWriter w, List<Penetration> penetrations)
        {
            if (w == null || penetrations == null) return;

            foreach (var pen in penetrations)
            {
                string layer = "_OPENINGS";
                int    color = pen.ElementType == "Floor" ? 5 : 1; // синий=перекрытие, красный=стена

                // CIRCLE — вид в плане (проекция на XY)
                w.WriteLine("0\nCIRCLE");
                w.WriteLine("8\n" + layer);
                w.WriteLine("62\n" + color);
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "10\n{0:F3}\n20\n{1:F3}\n30\n{2:F3}",
                    pen.Cx, pen.Cy, pen.Cz));
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "40\n{0:F3}", pen.SleeveD / 2.0));

                // TEXT — подпись: DN / гильза
                string label = string.Format(
                    CultureInfo.InvariantCulture,
                    "Гил.{0} DN{1:F0}/D{2:F0}", pen.Id, pen.DN, pen.SleeveD);
                w.WriteLine("0\nTEXT");
                w.WriteLine("8\n" + layer + "_TEXT");
                w.WriteLine("62\n7");
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "10\n{0:F3}\n20\n{1:F3}\n30\n{2:F3}",
                    pen.Cx + pen.SleeveD / 2.0 + 50, pen.Cy, pen.Cz));
                w.WriteLine("40\n80");
                w.WriteLine("1\n" + label);
            }
        }

        // -------------------------------------------------------------------------
        // Excel-спецификация (CSV для максимальной совместимости)
        // -------------------------------------------------------------------------
        public static void WriteCsv(string outputPath, List<Penetration> penetrations)
        {
            using (var w = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                w.WriteLine("№;Система;Элемент;X;Y;Z;DN трубы;Диаметр гильзы;Длина гильзы;Тип");
                foreach (var p in penetrations)
                {
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0};{1};{2};{3:F0};{4:F0};{5:F0};DN{6:F0};{7:F0} мм;{8:F0} мм;{9}",
                        p.Id, p.PipeSystem, p.Element,
                        p.Cx, p.Cy, p.Cz,
                        p.DN, p.SleeveD, p.SleeveL, p.ElementType));
                }
            }
        }

        // -------------------------------------------------------------------------
        // Standalone DXF
        // -------------------------------------------------------------------------
        public static void WriteStandaloneDxf(string outputPath, List<Penetration> penetrations)
        {
            using (var w = new StreamWriter(outputPath, false, Encoding.ASCII))
            {
                w.WriteLine("0\nSECTION\n2\nHEADER");
                w.WriteLine("9\n$ACADVER\n1\nAC1015");
                w.WriteLine("0\nENDSEC");
                w.WriteLine("0\nSECTION\n2\nENTITIES");
                WriteDxf(w, penetrations);
                w.WriteLine("0\nENDSEC");
                w.WriteLine("0\nEOF");
            }
        }

        // -------------------------------------------------------------------------
        // Интеграционная точка
        // -------------------------------------------------------------------------
        public static string Process(
            List<PipeAxis>          pipes,
            List<ConstructionPlane> planes,
            string                  basePath)
        {
            if (pipes == null || pipes.Count == 0)
                return "[PenetrationBuilder] Нет трубных осей — запустите TracePipes сначала.";
            if (planes == null || planes.Count == 0)
                return "[PenetrationBuilder] Нет конструктивных плоскостей.";

            var pens = Build(pipes, planes);

            string dxfPath = Path.ChangeExtension(basePath, null) + "_openings.dxf";
            string csvPath = Path.ChangeExtension(basePath, null) + "_openings.csv";

            WriteStandaloneDxf(dxfPath, pens);
            WriteCsv(csvPath, pens);

            return string.Format(
                "[PenetrationBuilder] Найдено {0} гильз → {1} + {2}",
                pens.Count, dxfPath, csvPath);
        }
    }
}
