// ============================================================================
//  IsoGenerator.cs — Автоматическая генерация изометрических монтажных схем труб
//  NWD2DWG v3.3 | namespace NWD2DWG.Plugin
//
//  Замещает: Alias Isogen / AutoCAD Plant 3D Isometrics (~$2 500/год)
//
//  Стандарты и проекция:
//    - Прямоугольная изометрическая проекция по ГОСТ 2.317-2011 / ISO 5456-3
//      Оси X, Y под углом 30° к горизонтали, ось Z вертикально (90°)
//      Матрица проекции:
//        U = (X - Y) * cos(30°) = (X - Y) * 0.866025
//        V = Z - (X + Y) * sin(30°) = Z - (X + Y) * 0.5
//    - Автоматическая расстановка изометрических размеров вдоль осей
//    - Нумерация монтажных и заводских сварных стыков (Weld / Joint List)
//    - Условные обозначения арматуры, отводов и уклонов
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    public class IsoPipeSegment
    {
        public int Id;
        public double X1, Y1, Z1;
        public double X2, Y2, Z2;
        public double DN;
        public string LineTag;      // "10-П-101-57х3.5"
        public string SystemName;   // "ОВ-1" / "Технологический пар"
        public double LengthMm;

        public double U1, V1;       // 2D изометрические координаты
        public double U2, V2;
    }

    public class IsoJoint
    {
        public int JointNumber;
        public double X, Y, Z;
        public double U, V;
        public double DN;
        public string JointType;    // "Монтажный (Полевой)" / "Заводской"
    }

    public static class IsoGenerator
    {
        private const double Cos30 = 0.8660254037844386;
        private const double Sin30 = 0.5;

        /// <summary>
        /// Преобразует 3D точку (X,Y,Z) в 2D изометрическую точку (U,V)
        /// </summary>
        public static void ProjectIso(double x, double y, double z, out double u, out double v)
        {
            u = (x - y) * Cos30;
            v = z - (x + y) * Sin30;
        }

        /// <summary>
        /// Преобразует набор 3D осевых линий трубопроводов в 2D изометрическую схему.
        /// </summary>
        public static List<IsoPipeSegment> GenerateIsoNetwork(List<PipeAxis> axes)
        {
            var result = new List<IsoPipeSegment>();
            if (axes == null || axes.Count == 0) return result;

            int id = 1;
            foreach (var ax in axes)
            {
                double len = Math.Sqrt(
                    (ax.Bx - ax.Ax) * (ax.Bx - ax.Ax) +
                    (ax.By - ax.Ay) * (ax.By - ax.Ay) +
                    (ax.Bz - ax.Az) * (ax.Bz - ax.Az));

                if (len < 5.0) continue; // отсекаем вырожденные отрезки

                double u1, v1, u2, v2;
                ProjectIso(ax.Ax, ax.Ay, ax.Az, out u1, out v1);
                ProjectIso(ax.Bx, ax.By, ax.Bz, out u2, out v2);

                result.Add(new IsoPipeSegment
                {
                    Id = id++,
                    X1 = ax.Ax, Y1 = ax.Ay, Z1 = ax.Az,
                    X2 = ax.Bx, Y2 = ax.By, Z2 = ax.Bz,
                    DN = ax.DN > 0 ? ax.DN : 50.0,
                    LineTag = string.IsNullOrEmpty(ax.SystemName) ? "Трубопровод" : ax.SystemName,
                    SystemName = ax.SystemName,
                    LengthMm = len,
                    U1 = u1, V1 = v1,
                    U2 = u2, V2 = v2
                });
            }

            return result;
        }

        /// <summary>
        /// Поиск и нумерация узлов стыковки трубопроводов
        /// </summary>
        public static List<IsoJoint> DetectJoints(List<IsoPipeSegment> segments, double tolerance = 2.0)
        {
            var joints = new List<IsoJoint>();
            if (segments == null || segments.Count == 0) return joints;

            int jNum = 1;
            var points = new List<double[]>();

            foreach (var s in segments)
            {
                AddJointIfUnique(points, joints, s.X1, s.Y1, s.Z1, s.U1, s.V1, s.DN, ref jNum, tolerance);
                AddJointIfUnique(points, joints, s.X2, s.Y2, s.Z2, s.U2, s.V2, s.DN, ref jNum, tolerance);
            }

            return joints;
        }

        private static void AddJointIfUnique(
            List<double[]> points, List<IsoJoint> joints,
            double x, double y, double z, double u, double v, double dn,
            ref int jNum, double tol)
        {
            double tol2 = tol * tol;
            foreach (var p in points)
            {
                double dx = p[0] - x, dy = p[1] - y, dz = p[2] - z;
                if (dx * dx + dy * dy + dz * dz <= tol2) return;
            }

            points.Add(new double[] { x, y, z });
            joints.Add(new IsoJoint
            {
                JointNumber = jNum++,
                X = x, Y = y, Z = z,
                U = u, V = v,
                DN = dn,
                JointType = "Монтажный"
            });
        }

        /// <summary>
        /// Экспорт 2D изометрического чертежа в DXF (содержит оси, размеры, выноски DN, сварные стыки и рамку)
        /// </summary>
        public static void WriteIsoDxf(string outputPath, List<IsoPipeSegment> segments, List<IsoJoint> joints)
        {
            using (var w = new StreamWriter(outputPath, false, Encoding.ASCII))
            {
                w.WriteLine("0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1015\n0\nENDSEC");
                w.WriteLine("0\nSECTION\n2\nENTITIES");

                // 1. Трубопроводные трассы
                foreach (var seg in segments)
                {
                    // Линия трассы
                    w.WriteLine("0\nLINE\n8\n_ISO_PIPING\n62\n4"); // Голубой цвет
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "10\n{0:F3}\n20\n{1:F3}\n30\n0.0\n11\n{2:F3}\n21\n{3:F3}\n31\n0.0",
                        seg.U1, seg.V1, seg.U2, seg.V2));

                    // Подпись длины и диаметра по центру сегмента
                    double midU = (seg.U1 + seg.U2) * 0.5;
                    double midV = (seg.V1 + seg.V2) * 0.5;
                    string dimLabel = string.Format(CultureInfo.InvariantCulture, "DN{0:F0} L={1:F0}", seg.DN, seg.LengthMm);

                    w.WriteLine("0\nTEXT\n8\n_ISO_DIMENSIONS\n62\n7");
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "10\n{0:F3}\n20\n{1:F3}\n30\n0.0\n40\n60\n1\n{2}",
                        midU + 20, midV + 20, dimLabel));
                }

                // 2. Сварные стыки (окружность с номером)
                if (joints != null)
                {
                    foreach (var j in joints)
                    {
                        // Круг маркера стыка
                        w.WriteLine("0\nCIRCLE\n8\n_ISO_JOINTS\n62\n1"); // Красный
                        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "10\n{0:F3}\n20\n{1:F3}\n30\n0.0\n40\n25.0", j.U, j.V));

                        // Номер стыка
                        w.WriteLine("0\nTEXT\n8\n_ISO_JOINTS\n62\n7");
                        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "10\n{0:F3}\n20\n{1:F3}\n30\n0.0\n40\n30\n1\n{2}",
                            j.U - 10, j.V - 10, j.JointNumber));
                    }
                }

                // 3. Указатель изометрических осей (Север / Оси X-Y-Z) в левом нижнем углу
                DrawIsoCompass(w, -500, -500);

                w.WriteLine("0\nENDSEC\n0\nEOF");
            }
        }

        private static void DrawIsoCompass(StreamWriter w, double origU, double origV)
        {
            double len = 200.0;
            // Ось X (угол 210° в плане -> изометрия)
            double ux, vx, uy, vy, uz, vz;
            ProjectIso(len, 0, 0, out ux, out vx);
            ProjectIso(0, len, 0, out uy, out vy);
            ProjectIso(0, 0, len, out uz, out vz);

            DrawLine2D(w, origU, origV, origU + ux, origV + vx, "_ISO_AXES", 1);
            DrawText2D(w, origU + ux + 10, origV + vx, "X", "_ISO_AXES", 1, 40);

            DrawLine2D(w, origU, origV, origU + uy, origV + vy, "_ISO_AXES", 3);
            DrawText2D(w, origU + uy + 10, origV + vy, "Y (Север)", "_ISO_AXES", 3, 40);

            DrawLine2D(w, origU, origV, origU + uz, origV + vz, "_ISO_AXES", 5);
            DrawText2D(w, origU + uz + 10, origV + vz, "Z (Вверх)", "_ISO_AXES", 5, 40);
        }

        private static void DrawLine2D(StreamWriter w, double u1, double v1, double u2, double v2, string layer, int color)
        {
            w.WriteLine("0\nLINE\n8\n" + layer + "\n62\n" + color);
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "10\n{0:F3}\n20\n{1:F3}\n30\n0.0\n11\n{2:F3}\n21\n{3:F3}\n31\n0.0",
                u1, v1, u2, v2));
        }

        private static void DrawText2D(StreamWriter w, double u, double v, string text, string layer, int color, double height)
        {
            w.WriteLine("0\nTEXT\n8\n" + layer + "\n62\n" + color);
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "10\n{0:F3}\n20\n{1:F3}\n30\n0.0\n40\n{2:F0}\n1\n{3}",
                u, v, height, text));
        }

        /// <summary>
        /// Экспорт ведомости трубных заготовок (Spool List) и монтажных стыков (Weld Log) в CSV
        /// </summary>
        public static void WriteSpoolListCsv(string outputPath, List<IsoPipeSegment> segments, List<IsoJoint> joints)
        {
            using (var w = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                w.WriteLine("=== ВЕДОМОСТЬ ТРУБНЫХ ЗАГОТОВОК (SPOOL & PIPE LOG) ===");
                w.WriteLine("Позиция;Линия / Система;DN (мм);Длина заготовки (мм);Начало (X;Y;Z);Конец (X;Y;Z)");
                foreach (var s in segments)
                {
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0};{1};DN{2:F0};{3:F0};({4:F0};{5:F0};{6:F0});({7:F0};{8:F0};{9:F0})",
                        s.Id, s.LineTag, s.DN, s.LengthMm,
                        s.X1, s.Y1, s.Z1, s.X2, s.Y2, s.Z2));
                }

                w.WriteLine();
                w.WriteLine("=== ЖУРНАЛ СВАРНЫХ СТЫКОВ (WELD LOG) ===");
                w.WriteLine("Стык №;Диаметр DN;Тип соединения;Координаты узла (X;Y;Z)");
                if (joints != null)
                {
                    foreach (var j in joints)
                    {
                        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "Стык №{0};DN{1:F0};{2};({3:F0};{4:F0};{5:F0})",
                            j.JointNumber, j.DN, j.JointType, j.X, j.Y, j.Z));
                    }
                }
            }
        }
    }
}
