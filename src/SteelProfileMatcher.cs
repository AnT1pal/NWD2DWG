// ============================================================================
//  SteelProfileMatcher.cs — Распознавание металлопроката по ГОСТ и ведомость КМ/КМД
//  NWD2DWG v3.2 | namespace NWD2DWG.Plugin
//
//  Замещает: модули сортамента металлоконструкций коммерческих BIM-пакетов
//
//  Сортаменты по стандартам РФ:
//    - Двутавры стальные горячекатаные (ГОСТ 26020-83 / СТО АСЧМ 20-93)
//    - Швеллеры стальные горячекатаные (ГОСТ 8240-97)
//    - Профили стальные гнутые замкнутые сварные квадратные/прямоугольные (ГОСТ 30245-2003)
//    - Трубы стальные бесшовные / электросварные круглые (ГОСТ 8732-78 / ГОСТ 10704-91)
//    - Уголки стальные горячекатаные равнополочные (ГОСТ 8509-93)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    public enum SteelType
    {
        Unknown,
        IBeam,        // Двутавр
        Channel,      // Швеллер
        SquareTube,   // Профильная труба квадратная
        RectTube,     // Профильная труба прямоугольная
        RoundTube,    // Труба круглая
        Angle,        // Уголок
        Plate         // Лист / пластина
    }

    public class SteelStandardItem
    {
        public string Designation; // "20Б1", "16П", "100х100х5", "159х5", "75х6"
        public string Gost;        // "ГОСТ 26020-83", "ГОСТ 8240-97"
        public SteelType Type;
        public double H;           // Высота/внешний габарит (мм)
        public double B;           // Ширина (мм)
        public double T;           // Толщина полки / стенки (мм)
        public double MassPerMeter;// Масса 1 пог. м (кг/м)

        public SteelStandardItem(string des, string gost, SteelType type, double h, double b, double t, double mass)
        {
            Designation = des;
            Gost = gost;
            Type = type;
            H = h;
            B = b;
            T = t;
            MassPerMeter = mass;
        }
    }

    public class SteelMatchResult
    {
        public bool Matched;
        public string Designation;
        public string Gost;
        public SteelType Type;
        public double Length;         // Длина элемента (мм)
        public double MeasuredH;      // Замеренная высота (мм)
        public double MeasuredB;      // Замеренная ширина (мм)
        public double MassPerMeter;   // кг/м
        public double TotalMass;      // Итоговая масса (кг)
        public double Confidence;     // Качество сопоставления (0..1)
        public double StartX, StartY, StartZ;
        public double EndX, EndY, EndZ;
    }

    public static class SteelProfileMatcher
    {
        private static readonly List<SteelStandardItem> Database = new List<SteelStandardItem>();

        static SteelProfileMatcher()
        {
            InitDatabase();
        }

        private static void InitDatabase()
        {
            // Двутавры (ГОСТ 26020-83)
            Add(new SteelStandardItem("Двутавр 10Б1", "ГОСТ 26020-83", SteelType.IBeam, 100, 55, 5.1, 8.1));
            Add(new SteelStandardItem("Двутавр 12Б1", "ГОСТ 26020-83", SteelType.IBeam, 120, 64, 5.7, 10.4));
            Add(new SteelStandardItem("Двутавр 14Б1", "ГОСТ 26020-83", SteelType.IBeam, 140, 73, 6.2, 12.9));
            Add(new SteelStandardItem("Двутавр 16Б1", "ГОСТ 26020-83", SteelType.IBeam, 160, 82, 6.7, 15.7));
            Add(new SteelStandardItem("Двутавр 18Б1", "ГОСТ 26020-83", SteelType.IBeam, 180, 91, 7.3, 18.8));
            Add(new SteelStandardItem("Двутавр 20Б1", "ГОСТ 26020-83", SteelType.IBeam, 200, 100, 7.5, 21.3));
            Add(new SteelStandardItem("Двутавр 25Б1", "ГОСТ 26020-83", SteelType.IBeam, 248, 124, 8.0, 25.7));
            Add(new SteelStandardItem("Двутавр 30Б1", "ГОСТ 26020-83", SteelType.IBeam, 296, 150, 9.0, 32.9));
            Add(new SteelStandardItem("Двутавр 35Б1", "ГОСТ 26020-83", SteelType.IBeam, 346, 174, 9.5, 41.4));
            Add(new SteelStandardItem("Двутавр 40Б1", "ГОСТ 26020-83", SteelType.IBeam, 396, 199, 10.5, 56.6));
            Add(new SteelStandardItem("Двутавр 20К1", "ГОСТ 26020-83", SteelType.IBeam, 195, 200, 10.0, 41.4));
            Add(new SteelStandardItem("Двутавр 25К1", "ГОСТ 26020-83", SteelType.IBeam, 246, 250, 12.0, 62.4));
            Add(new SteelStandardItem("Двутавр 30К1", "ГОСТ 26020-83", SteelType.IBeam, 296, 300, 13.5, 84.8));

            // Швеллеры (ГОСТ 8240-97)
            Add(new SteelStandardItem("Швеллер 8П", "ГОСТ 8240-97", SteelType.Channel, 80, 40, 7.4, 7.05));
            Add(new SteelStandardItem("Швеллер 10П", "ГОСТ 8240-97", SteelType.Channel, 100, 46, 7.6, 8.59));
            Add(new SteelStandardItem("Швеллер 12П", "ГОСТ 8240-97", SteelType.Channel, 120, 52, 7.8, 10.4));
            Add(new SteelStandardItem("Швеллер 14П", "ГОСТ 8240-97", SteelType.Channel, 140, 58, 8.1, 12.3));
            Add(new SteelStandardItem("Швеллер 16П", "ГОСТ 8240-97", SteelType.Channel, 160, 64, 8.4, 14.2));
            Add(new SteelStandardItem("Швеллер 18П", "ГОСТ 8240-97", SteelType.Channel, 180, 70, 8.7, 16.3));
            Add(new SteelStandardItem("Швеллер 20П", "ГОСТ 8240-97", SteelType.Channel, 200, 76, 9.0, 18.4));
            Add(new SteelStandardItem("Швеллер 24П", "ГОСТ 8240-97", SteelType.Channel, 240, 90, 9.7, 24.0));
            Add(new SteelStandardItem("Швеллер 30П", "ГОСТ 8240-97", SteelType.Channel, 300, 100, 10.2, 31.8));

            // Профильные трубы квадратные и прямоугольные (ГОСТ 30245-2003)
            Add(new SteelStandardItem("Труба гн. 50х50х3", "ГОСТ 30245-2003", SteelType.SquareTube, 50, 50, 3.0, 4.31));
            Add(new SteelStandardItem("Труба гн. 60х60х3", "ГОСТ 30245-2003", SteelType.SquareTube, 60, 60, 3.0, 5.25));
            Add(new SteelStandardItem("Труба гн. 80х80х4", "ГОСТ 30245-2003", SteelType.SquareTube, 80, 80, 4.0, 9.33));
            Add(new SteelStandardItem("Труба гн. 100х100х4", "ГОСТ 30245-2003", SteelType.SquareTube, 100, 100, 4.0, 11.84));
            Add(new SteelStandardItem("Труба гн. 100х100х5", "ГОСТ 30245-2003", SteelType.SquareTube, 100, 100, 5.0, 14.54));
            Add(new SteelStandardItem("Труба гн. 120х120х5", "ГОСТ 30245-2003", SteelType.SquareTube, 120, 120, 5.0, 17.68));
            Add(new SteelStandardItem("Труба гн. 140х140х5", "ГОСТ 30245-2003", SteelType.SquareTube, 140, 140, 5.0, 20.82));
            Add(new SteelStandardItem("Труба гн. 160х160х6", "ГОСТ 30245-2003", SteelType.SquareTube, 160, 160, 6.0, 28.34));
            Add(new SteelStandardItem("Труба гн. 180х180х6", "ГОСТ 30245-2003", SteelType.SquareTube, 180, 180, 6.0, 32.11));
            Add(new SteelStandardItem("Труба гн. 200х200х6", "ГОСТ 30245-2003", SteelType.SquareTube, 200, 200, 6.0, 35.88));

            Add(new SteelStandardItem("Труба гн. 80х40х3", "ГОСТ 30245-2003", SteelType.RectTube, 80, 40, 3.0, 5.25));
            Add(new SteelStandardItem("Труба гн. 100х50х4", "ГОСТ 30245-2003", SteelType.RectTube, 100, 50, 4.0, 8.70));
            Add(new SteelStandardItem("Труба гн. 120х60х4", "ГОСТ 30245-2003", SteelType.RectTube, 120, 60, 4.0, 10.59));
            Add(new SteelStandardItem("Труба гн. 140х80х5", "ГОСТ 30245-2003", SteelType.RectTube, 140, 80, 5.0, 16.11));
            Add(new SteelStandardItem("Труба гн. 160х80х5", "ГОСТ 30245-2003", SteelType.RectTube, 160, 80, 5.0, 17.68));
            Add(new SteelStandardItem("Труба гн. 200х100х6", "ГОСТ 30245-2003", SteelType.RectTube, 200, 100, 6.0, 26.46));

            // Круглые трубы (ГОСТ 8732-78 / ГОСТ 10704-91)
            Add(new SteelStandardItem("Труба 57х3.5", "ГОСТ 8732-78", SteelType.RoundTube, 57, 57, 3.5, 4.62));
            Add(new SteelStandardItem("Труба 76х4", "ГОСТ 8732-78", SteelType.RoundTube, 76, 76, 4.0, 7.10));
            Add(new SteelStandardItem("Труба 89х4", "ГОСТ 8732-78", SteelType.RoundTube, 89, 89, 4.0, 8.38));
            Add(new SteelStandardItem("Труба 108х4.5", "ГОСТ 8732-78", SteelType.RoundTube, 108, 108, 4.5, 11.49));
            Add(new SteelStandardItem("Труба 133х4.5", "ГОСТ 8732-78", SteelType.RoundTube, 133, 133, 4.5, 14.26));
            Add(new SteelStandardItem("Труба 159х5", "ГОСТ 8732-78", SteelType.RoundTube, 159, 159, 5.0, 18.99));
            Add(new SteelStandardItem("Труба 219х6", "ГОСТ 8732-78", SteelType.RoundTube, 219, 219, 6.0, 31.52));
            Add(new SteelStandardItem("Труба 273х7", "ГОСТ 8732-78", SteelType.RoundTube, 273, 273, 7.0, 45.92));
            Add(new SteelStandardItem("Труба 325х8", "ГОСТ 8732-78", SteelType.RoundTube, 325, 325, 8.0, 62.54));

            // Уголки равнополочные (ГОСТ 8509-93)
            Add(new SteelStandardItem("Уголок 40х4", "ГОСТ 8509-93", SteelType.Angle, 40, 40, 4.0, 2.42));
            Add(new SteelStandardItem("Уголок 50х5", "ГОСТ 8509-93", SteelType.Angle, 50, 50, 5.0, 3.77));
            Add(new SteelStandardItem("Уголок 63х5", "ГОСТ 8509-93", SteelType.Angle, 63, 63, 5.0, 4.81));
            Add(new SteelStandardItem("Уголок 75х6", "ГОСТ 8509-93", SteelType.Angle, 75, 75, 6.0, 6.89));
            Add(new SteelStandardItem("Уголок 90х7", "ГОСТ 8509-93", SteelType.Angle, 90, 90, 7.0, 9.64));
            Add(new SteelStandardItem("Уголок 100х8", "ГОСТ 8509-93", SteelType.Angle, 100, 100, 8.0, 12.25));
            Add(new SteelStandardItem("Уголок 125х8", "ГОСТ 8509-93", SteelType.Angle, 125, 125, 8.0, 15.46));
            Add(new SteelStandardItem("Уголок 140х9", "ГОСТ 8509-93", SteelType.Angle, 140, 140, 9.0, 19.41));
            Add(new SteelStandardItem("Уголок 160х10", "ГОСТ 8509-93", SteelType.Angle, 160, 160, 10.0, 24.67));
        }

        private static void Add(SteelStandardItem item)
        {
            Database.Add(item);
        }

        /// <summary>
        /// Выполняет PCA-анализ 3D-сетки элемента, определяет продольную ось и сопоставляет поперечное сечение с сортаментом ГОСТ.
        /// </summary>
        public static SteelMatchResult MatchMesh(List<double> verts) { return MatchMesh(verts, 15.0); }

        /// <summary>tolerancePct — допустимое расхождение габаритов сечения с сортаментом.</summary>
        public static SteelMatchResult MatchMesh(List<double> verts, double tolerancePct)
        {
            var res = new SteelMatchResult { Matched = false, Type = SteelType.Unknown };
            if (verts == null || verts.Count < 24) return res; // минимум 8 вершин

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

            // 2. Матрица ковариации
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

            // 3. Вычисление главных осей (собственные векторы 3х3 через итерации Якоби)
            double[,] V = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            double[] d = new double[3] { cxx, cyy, czz };
            double[,] A = new double[3, 3] {
                { cxx, cxy, cxz },
                { cxy, cyy, cyz },
                { cxz, cyz, czz }
            };

            for (int iter = 0; iter < 20; iter++)
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
                        double t;
                        if (Math.Abs(h) < 1e-12) t = 1.0;
                        else
                        {
                            double theta = 0.5 * h / apq;
                            t = 1.0 / (Math.Abs(theta) + Math.Sqrt(1.0 + theta * theta));
                            if (theta < 0) t = -t;
                        }

                        double c = 1.0 / Math.Sqrt(1.0 + t * t);
                        double s = t * c;
                        double tau = s / (1.0 + c);

                        d[p] -= t * apq;
                        d[q] += t * apq;
                        A[p, q] = 0;

                        for (int j = 0; j < p; j++) Rotate(A, j, p, j, q, s, tau);
                        for (int j = p + 1; j < q; j++) Rotate(A, p, j, j, q, s, tau);
                        for (int j = q + 1; j < 3; j++) Rotate(A, p, j, q, j, s, tau);
                        for (int j = 0; j < 3; j++) Rotate(V, j, p, j, q, s, tau);
                    }
                }
            }

            // Находим главную ось удлинения (максимальное собственное число)
            int mainAxis = 0;
            if (d[1] > d[mainAxis]) mainAxis = 1;
            if (d[2] > d[mainAxis]) mainAxis = 2;

            double axisX = V[0, mainAxis];
            double axisY = V[1, mainAxis];
            double axisZ = V[2, mainAxis];

            // Проекция на главную ось и ортогональные оси
            int axisU = (mainAxis + 1) % 3;
            int axisV = (mainAxis + 2) % 3;

            double minMain = double.MaxValue, maxMain = double.MinValue;
            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;

            for (int i = 0; i < n; i++)
            {
                double px = verts[i * 3] - cx;
                double py = verts[i * 3 + 1] - cy;
                double pz = verts[i * 3 + 2] - cz;

                double pMain = px * axisX + py * axisY + pz * axisZ;
                double pU = px * V[0, axisU] + py * V[1, axisU] + pz * V[2, axisU];
                double pV = px * V[0, axisV] + py * V[1, axisV] + pz * V[2, axisV];

                if (pMain < minMain) minMain = pMain;
                if (pMain > maxMain) maxMain = pMain;
                if (pU < minU) minU = pU;
                if (pU > maxU) maxU = pU;
                if (pV < minV) minV = pV;
                if (pV > maxV) maxV = pV;
            }

            double length = maxMain - minMain;
            double dim1 = maxU - minU;
            double dim2 = maxV - minV;

            double hMeasured = Math.Max(dim1, dim2);
            double bMeasured = Math.Min(dim1, dim2);

            if (length < 50.0 || hMeasured < 10.0) return res; // слишком малый элемент

            res.Length = length;
            res.MeasuredH = hMeasured;
            res.MeasuredB = bMeasured;

            res.StartX = cx + minMain * axisX;
            res.StartY = cy + minMain * axisY;
            res.StartZ = cz + minMain * axisZ;

            res.EndX = cx + maxMain * axisX;
            res.EndY = cy + maxMain * axisY;
            res.EndZ = cz + maxMain * axisZ;

            // Поиск наилучшего совпадения в базе ГОСТ
            SteelStandardItem bestItem = null;
            double bestError = double.MaxValue;

            foreach (var item in Database)
            {
                double itemH = Math.Max(item.H, item.B);
                double itemB = Math.Min(item.H, item.B);

                double errH = Math.Abs(hMeasured - itemH) / itemH;
                double errB = Math.Abs(bMeasured - itemB) / itemB;
                double totalErr = Math.Sqrt(errH * errH + errB * errB);

                if (totalErr < bestError)
                {
                    bestError = totalErr;
                    bestItem = item;
                }
            }

            double tol = Math.Max(0.005, tolerancePct / 100.0);
            if (bestItem != null && bestError < tol)
            {
                res.Matched = true;
                res.Designation = bestItem.Designation;
                res.Gost = bestItem.Gost;
                res.Type = bestItem.Type;
                res.MassPerMeter = bestItem.MassPerMeter;
                res.TotalMass = (length / 1000.0) * bestItem.MassPerMeter;
                res.Confidence = Math.Max(0.0, 1.0 - bestError);
            }
            else
            {
                // Не найден точный профиль из сортамента — оцениваем как кастомный прокат/лист
                res.Matched = false;
                res.Designation = string.Format(CultureInfo.InvariantCulture, "Профиль {0:F0}x{1:F0}", hMeasured, bMeasured);
                res.Gost = "Индивидуальный";
                res.Type = (Math.Abs(hMeasured - bMeasured) < 5) ? SteelType.SquareTube : SteelType.Plate;
                // Оценка массы для стали 7850 кг/м3 при толщине стенки ~6 мм
                double perimeter = 2.0 * (hMeasured + bMeasured);
                res.MassPerMeter = (perimeter * 6.0 * 1e-6) * 7850.0;
                res.TotalMass = (length / 1000.0) * res.MassPerMeter;
                res.Confidence = 0.5;
            }

            return res;
        }

        private static void Rotate(double[,] a, int i, int j, int k, int l, double s, double tau)
        {
            double g = a[i, j];
            double h = a[k, l];
            a[i, j] = g - s * (h + g * tau);
            a[k, l] = h + s * (g - h * tau);
        }

        /// <summary>
        /// Генерация сводной ведомости металлопроката (КМ/КМД) в CSV/Excel
        /// </summary>
        public static void WriteSteelBomCsv(string outputPath, List<SteelMatchResult> results)
        {
            if (results == null || results.Count == 0) return;

            // Группировка по профилю и ГОСТу
            var groups = new Dictionary<string, List<SteelMatchResult>>();
            foreach (var r in results)
            {
                string key = r.Designation + " (" + r.Gost + ")";
                if (!groups.ContainsKey(key)) groups[key] = new List<SteelMatchResult>();
                groups[key].Add(r);
            }

            using (var w = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                w.WriteLine("=== ТЕХНИЧЕСКАЯ СПЕЦИФИКАЦИЯ СТАЛИ (ВЕДОМОСТЬ ЭЛЕМЕНТОВ КМ/КМД) ===");
                w.WriteLine("№;Профиль;Стандарт;Кол-во (шт);Общая длина (м);Масса 1 м.п. (кг);Общая масса (кг);Примечание");

                int idx = 1;
                double grandTotalLength = 0;
                double grandTotalMass = 0;

                foreach (var kvp in groups)
                {
                    var list = kvp.Value;
                    var sample = list[0];

                    double totalLenM = 0;
                    double totalMass = 0;
                    foreach (var item in list)
                    {
                        totalLenM += item.Length / 1000.0;
                        totalMass += item.TotalMass;
                    }

                    grandTotalLength += totalLenM;
                    grandTotalMass += totalMass;

                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0};{1};{2};{3};{4:F2};{5:F2};{6:F2};{7}",
                        idx++,
                        sample.Designation,
                        sample.Gost,
                        list.Count,
                        totalLenM,
                        sample.MassPerMeter,
                        totalMass,
                        sample.Matched ? "По сортаменту" : "Кастом"));
                }

                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    ";ИТОГО МЕТАЛЛОПРОКАТА;;{0};{1:F2};;{2:F2};",
                    results.Count, grandTotalLength, grandTotalMass));
            }
        }
    }
}
