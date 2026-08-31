// ============================================================================
//  RoomFinishSchedule.cs — Авторасчет ведомости отделки помещений по ГОСТ 21.501-2018
//  NWD2DWG v3.4 | namespace NWD2DWG.Plugin
//
//  Замещает: коммерческие плагины квартирографии и отделки Revit/Navisworks (~$2 000/год)
//
//  Функционал:
//    - Расчет площади пола и потолка (м2) по замкнутым 2D-полигонам
//    - Расчет периметра и валовой площади стен (м2)
//    - Автоматический вычет площадей дверных и оконных проемов
//    - Расчет длины плинтуса (м) за вычетом ширины дверных проемов
//    - Экспорт ведомости отделки по ГОСТ 21.501-2018 (Форма 7) в CSV/Excel
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    public class RoomOpening
    {
        public double WidthMm;
        public double HeightMm;
        public bool IsDoor; // true = дверь (вычитается и из стены, и из плинтуса), false = окно (только из стены)

        public double AreaM2 { get { return (WidthMm * HeightMm) * 1e-6; } }
        public double WidthM { get { return WidthMm * 1e-3; } }
    }

    public class RoomData
    {
        public string Number;        // "101"
        public string Name;          // "Насосная станция пожаротушения"
        public string FloorType;     // "П-1 (Керамогранит)"
        public string WallType;      // "С-1 (Окраска водно-дисперсионная)"
        public string CeilingType;   // "Пот-1 (Потолок Грильято)"
        public double HeightMm;      // Высота помещения (мм)
        public List<double[]> Contour2D = new List<double[]>(); // Вершины контура [X, Y] (мм)
        public List<RoomOpening> Openings = new List<RoomOpening>();

        // Расчетные характеристики
        public double FloorAreaM2 { get; private set; }
        public double PerimeterM { get; private set; }
        public double GrossWallAreaM2 { get; private set; }
        public double OpeningsAreaM2 { get; private set; }
        public double NetWallAreaM2 { get; private set; }
        public double SkirtingLengthM { get; private set; }

        public void Calculate()
        {
            if (Contour2D == null || Contour2D.Count < 3) return;

            // 1. Площадь пола по формуле Гаусса (Shoelace formula)
            double area2 = 0;
            double perim = 0;
            int n = Contour2D.Count;

            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                double x1 = Contour2D[i][0];
                double y1 = Contour2D[i][1];
                double x2 = Contour2D[next][0];
                double y2 = Contour2D[next][1];

                area2 += (x1 * y2 - x2 * y1);
                double edgeLen = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                perim += edgeLen;
            }

            FloorAreaM2 = Math.Abs(area2) * 0.5 * 1e-6; // мм2 -> м2
            PerimeterM = perim * 1e-3;                   // мм -> м

            // 2. Площадь стен
            double hM = HeightMm > 0 ? HeightMm * 1e-3 : 3.0; // по умолчанию 3.0 м
            GrossWallAreaM2 = PerimeterM * hM;

            // 3. Вычет проемов
            double sumOpeningsArea = 0;
            double sumDoorWidth = 0;

            foreach (var op in Openings)
            {
                sumOpeningsArea += op.AreaM2;
                if (op.IsDoor) sumDoorWidth += op.WidthM;
            }

            OpeningsAreaM2 = sumOpeningsArea;
            NetWallAreaM2 = Math.Max(0.0, GrossWallAreaM2 - sumOpeningsArea);
            SkirtingLengthM = Math.Max(0.0, PerimeterM - sumDoorWidth);
        }
    }

    public static class RoomFinishSchedule
    {
        /// <summary>
        /// Формирование ведомости отделки помещений по ГОСТ 21.501-2018 (Форма 7) в формате CSV
        /// </summary>
        public static void WriteFinishScheduleCsv(string outputPath, List<RoomData> rooms)
        {
            if (rooms == null || rooms.Count == 0) return;

            foreach (var r in rooms) r.Calculate();

            using (var w = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                w.WriteLine("=== ВЕДОМОСТЬ ОТДЕЛКИ ПОМЕЩЕНИЙ (ГОСТ 21.501-2018, ФОРМА 7) ===");
                w.WriteLine("Номер;Наименование помещения;Потолок: Вид отделки;Потолок: Площадь (м2);Стены: Вид отделки;Стены: Площадь (м2);Пол: Тип покрытия;Пол: Площадь (м2);Плинтус: Длина (м)");

                double totalFloorArea = 0;
                double totalWallArea = 0;
                double totalSkirting = 0;

                foreach (var r in rooms)
                {
                    totalFloorArea += r.FloorAreaM2;
                    totalWallArea += r.NetWallAreaM2;
                    totalSkirting += r.SkirtingLengthM;

                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0};{1};{2};{3:F2};{4};{5:F2};{6};{7:F2};{8:F2}",
                        r.Number,
                        r.Name,
                        string.IsNullOrEmpty(r.CeilingType) ? "Покраска" : r.CeilingType,
                        r.FloorAreaM2,
                        string.IsNullOrEmpty(r.WallType) ? "Штукатурка, покраска" : r.WallType,
                        r.NetWallAreaM2,
                        string.IsNullOrEmpty(r.FloorType) ? "Керамогранит" : r.FloorType,
                        r.FloorAreaM2,
                        r.SkirtingLengthM));
                }

                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    ";ИТОГО ПО ЭТАЖУ / ЗДАНИЮ;;{0:F2};;{1:F2};;{2:F2};{3:F2}",
                    totalFloorArea, totalWallArea, totalFloorArea, totalSkirting));
            }
        }

        /// <summary>
        /// Экспорт контуров помещений с маркерами площадей в DXF
        /// </summary>
        public static void WriteRoomsDxf(string outputPath, List<RoomData> rooms, string layer = "_ROOMS")
        {
            using (var w = new StreamWriter(outputPath, false, Encoding.ASCII))
            {
                w.WriteLine("0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1015\n0\nENDSEC");
                w.WriteLine("0\nSECTION\n2\nENTITIES");

                foreach (var r in rooms)
                {
                    r.Calculate();
                    if (r.Contour2D.Count < 3) continue;

                    // 1. Замкнутая полилиния контура помещения
                    w.WriteLine("0\nLWPOLYLINE\n8\n" + layer + "\n62\n6\n70\n1\n90\n" + r.Contour2D.Count);
                    double cx = 0, cy = 0;
                    foreach (var pt in r.Contour2D)
                    {
                        cx += pt[0]; cy += pt[1];
                        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "10\n{0:F3}\n20\n{1:F3}", pt[0], pt[1]));
                    }
                    cx /= r.Contour2D.Count;
                    cy /= r.Contour2D.Count;

                    // 2. Текстовый маркер помещения
                    string label = string.Format(CultureInfo.InvariantCulture,
                        "{0}\n{1}\nS={2:F1} м2", r.Number, r.Name, r.FloorAreaM2);

                    w.WriteLine("0\nTEXT\n8\n" + layer + "_TEXT\n62\n7");
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "10\n{0:F3}\n20\n{1:F3}\n30\n0.0\n40\n120\n1\n{2}",
                        cx, cy, label));
                }

                w.WriteLine("0\nENDSEC\n0\nEOF");
            }
        }
    }
}
