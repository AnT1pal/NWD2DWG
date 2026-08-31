// ============================================================================
//  ClearanceValidator.cs — Проверка высоты проходов и эвакуационных путей
//  NWD2DWG v3.2 | namespace NWD2DWG.Plugin
//
//  Замещает: Solibri Clearance Checker (~$3 200/год)
//
//  Алгоритм:
//    1. Строим 2D сетку зон (grid cells) на плане этажа
//    2. Для каждой ячейки находим:
//       - минимальный Z пола (нижняя поверхность)
//       - максимальный Z препятствия над головой (коммуникации / конструкции)
//    3. Высота прохода = Z_obstacle_bottom - Z_floor_top
//    4. Если высота < minClearance (по умолчанию 2000 мм, СП 118.13330) → нарушение
//    5. Нарушения выводятся в слой _CLEARANCE_VIOLATIONS как прямоугольные SOLID
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    // -------------------------------------------------------------------------
    // Описание нарушения
    // -------------------------------------------------------------------------
    public class ClearanceViolation
    {
        public double X, Y;          // центр ячейки (план)
        public double Z;             // высотная отметка нарушения
        public double CellSize;      // размер ячейки (мм)
        public double Clearance;     // фактическая высота (мм)
        public double Required;      // требуемая высота (мм)
        public double Deficit;       // нехватка = Required - Clearance (мм)
        public string NearestObj;    // имя мешающего объекта
    }

    // -------------------------------------------------------------------------
    // Элемент сцены для проверки (упрощённый AABB)
    // -------------------------------------------------------------------------
    public class SceneBox
    {
        public double MinX, MinY, MinZ;
        public double MaxX, MaxY, MaxZ;
        public string Name;
        public bool   IsFloor; // true = пол/перекрытие, false = препятствие над головой
    }

    // -------------------------------------------------------------------------
    // Основной класс валидатора
    // -------------------------------------------------------------------------
    public static class ClearanceValidator
    {
        // Минимальная высота прохода по СП 118.13330 / СП 1.13130 (мм)
        public const double DefaultMinClearance = 2000.0;

        // Размер ячейки сетки проверки (мм) — баланс точность/скорость
        public const double DefaultCellSize = 500.0;

        /// <summary>
        /// Проверяет зазоры над проходами по всей сцене.
        /// </summary>
        /// <param name="boxes">Все AABB объектов сцены</param>
        /// <param name="minClearance">Минимально допустимая высота прохода (мм)</param>
        /// <param name="cellSize">Шаг сетки проверки (мм)</param>
        public static List<ClearanceViolation> Validate(
            List<SceneBox> boxes,
            double minClearance = DefaultMinClearance,
            double cellSize     = DefaultCellSize)
        {
            var violations = new List<ClearanceViolation>();
            if (boxes == null || boxes.Count == 0) return violations;

            // Определяем границы сцены в плане
            double sceneMinX = double.MaxValue, sceneMinY = double.MaxValue;
            double sceneMaxX = double.MinValue, sceneMaxY = double.MinValue;

            foreach (var b in boxes)
            {
                if (b.MinX < sceneMinX) sceneMinX = b.MinX;
                if (b.MinY < sceneMinY) sceneMinY = b.MinY;
                if (b.MaxX > sceneMaxX) sceneMaxX = b.MaxX;
                if (b.MaxY > sceneMaxY) sceneMaxY = b.MaxY;
            }

            // Разделяем объекты на полы и препятствия
            var floors    = new List<SceneBox>();
            var obstacles = new List<SceneBox>();
            foreach (var b in boxes)
            {
                if (b.IsFloor) floors.Add(b);
                else           obstacles.Add(b);
            }

            // Обходим сетку ячеек в плане
            int cols = (int)Math.Ceiling((sceneMaxX - sceneMinX) / cellSize);
            int rows = (int)Math.Ceiling((sceneMaxY - sceneMinY) / cellSize);

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    double cx = sceneMinX + (c + 0.5) * cellSize;
                    double cy = sceneMinY + (r + 0.5) * cellSize;

                    // Найти верхнюю поверхность пола в этой ячейке
                    double floorZ = double.MinValue;
                    foreach (var fl in floors)
                    {
                        if (cx >= fl.MinX && cx <= fl.MaxX &&
                            cy >= fl.MinY && cy <= fl.MaxY)
                        {
                            if (fl.MaxZ > floorZ) floorZ = fl.MaxZ;
                        }
                    }
                    if (floorZ == double.MinValue) continue; // нет пола в ячейке

                    // Найти нижнее препятствие над полом в этой ячейке
                    double obstZ    = double.MaxValue;
                    string obstName = "";
                    foreach (var ob in obstacles)
                    {
                        if (cx >= ob.MinX && cx <= ob.MaxX &&
                            cy >= ob.MinY && cy <= ob.MaxY)
                        {
                            // Препятствие должно быть выше пола
                            if (ob.MinZ > floorZ && ob.MinZ < obstZ)
                            {
                                obstZ    = ob.MinZ;
                                obstName = ob.Name;
                            }
                        }
                    }
                    if (obstZ == double.MaxValue) continue; // нет препятствий

                    double clearance = obstZ - floorZ;
                    if (clearance < minClearance)
                    {
                        violations.Add(new ClearanceViolation
                        {
                            X          = cx,
                            Y          = cy,
                            Z          = floorZ,
                            CellSize   = cellSize,
                            Clearance  = clearance,
                            Required   = minClearance,
                            Deficit    = minClearance - clearance,
                            NearestObj = obstName
                        });
                    }
                }
            }

            return violations;
        }

        // -------------------------------------------------------------------------
        // Статистика
        // -------------------------------------------------------------------------
        public static string GetSummary(List<ClearanceViolation> violations, double minClearance)
        {
            if (violations.Count == 0)
                return string.Format(
                    "[ClearanceValidator] Нарушений не найдено (порог {0:F0} мм) ✓", minClearance);

            double maxDeficit = 0;
            foreach (var v in violations)
                if (v.Deficit > maxDeficit) maxDeficit = v.Deficit;

            return string.Format(
                "[ClearanceValidator] НАРУШЕНИЙ: {0} | Макс. нехватка: {1:F0} мм | Порог: {2:F0} мм",
                violations.Count, maxDeficit, minClearance);
        }

        // -------------------------------------------------------------------------
        // Запись нарушений в DXF (SOLID — закрашенный прямоугольник)
        // -------------------------------------------------------------------------
        public static void WriteDxf(StreamWriter w, List<ClearanceViolation> violations)
        {
            if (w == null || violations == null) return;

            string layer = "_CLEARANCE_VIOLATIONS";
            int    color = 1; // красный = нарушение

            foreach (var v in violations)
            {
                double half = v.CellSize / 2.0;

                // SOLID (заполненный четырёхугольник) — план нарушения
                w.WriteLine("0\nSOLID");
                w.WriteLine("8\n" + layer);
                w.WriteLine("62\n" + color);
                // 4 угла SOLID (порядок AutoCAD: 10,11,12,13)
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "10\n{0:F3}\n20\n{1:F3}\n30\n{2:F3}",
                    v.X - half, v.Y - half, v.Z));
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "11\n{0:F3}\n21\n{1:F3}\n31\n{2:F3}",
                    v.X + half, v.Y - half, v.Z));
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "12\n{0:F3}\n22\n{1:F3}\n32\n{2:F3}",
                    v.X - half, v.Y + half, v.Z));
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "13\n{0:F3}\n23\n{1:F3}\n33\n{2:F3}",
                    v.X + half, v.Y + half, v.Z));

                // TEXT — подпись нехватки
                string label = string.Format(CultureInfo.InvariantCulture,
                    "H={0:F0}мм (−{1:F0})", v.Clearance, v.Deficit);
                w.WriteLine("0\nTEXT");
                w.WriteLine("8\n" + layer + "_TEXT");
                w.WriteLine("62\n7");
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "10\n{0:F3}\n20\n{1:F3}\n30\n{2:F3}",
                    v.X, v.Y, v.Z + 100));
                w.WriteLine("40\n80");
                w.WriteLine("1\n" + label);
            }
        }

        // -------------------------------------------------------------------------
        // Standalone DXF
        // -------------------------------------------------------------------------
        public static void WriteStandaloneDxf(string outputPath, List<ClearanceViolation> violations)
        {
            using (var w = new StreamWriter(outputPath, false, Encoding.ASCII))
            {
                w.WriteLine("0\nSECTION\n2\nHEADER");
                w.WriteLine("9\n$ACADVER\n1\nAC1015");
                w.WriteLine("0\nENDSEC");
                w.WriteLine("0\nSECTION\n2\nENTITIES");
                WriteDxf(w, violations);
                w.WriteLine("0\nENDSEC");
                w.WriteLine("0\nEOF");
            }
        }

        // -------------------------------------------------------------------------
        // CSV-отчёт о нарушениях
        // -------------------------------------------------------------------------
        public static void WriteCsv(string outputPath, List<ClearanceViolation> violations)
        {
            using (var w = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                w.WriteLine("X;Y;Z пола;Высота (мм);Требуется (мм);Нехватка (мм);Мешающий объект");
                foreach (var v in violations)
                {
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0:F0};{1:F0};{2:F0};{3:F0};{4:F0};{5:F0};{6}",
                        v.X, v.Y, v.Z, v.Clearance, v.Required, v.Deficit, v.NearestObj));
                }
            }
        }

        // -------------------------------------------------------------------------
        // Интеграционная точка
        // -------------------------------------------------------------------------
        public static string Process(
            List<SceneBox> boxes,
            string         basePath,
            double         minClearance = DefaultMinClearance,
            double         cellSize     = DefaultCellSize)
        {
            if (boxes == null || boxes.Count == 0)
                return "[ClearanceValidator] Нет объектов сцены для проверки.";

            var violations = Validate(boxes, minClearance, cellSize);
            string summary = GetSummary(violations, minClearance);

            string dxfPath = Path.ChangeExtension(basePath, null) + "_clearance.dxf";
            string csvPath = Path.ChangeExtension(basePath, null) + "_clearance.csv";

            WriteStandaloneDxf(dxfPath, violations);
            if (violations.Count > 0)
                WriteCsv(csvPath, violations);

            return summary + "\n[ClearanceValidator] → " + dxfPath;
        }
    }
}
