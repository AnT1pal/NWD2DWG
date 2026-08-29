// ============================================================================
//  NWD2DWG — PipeTracer.cs
//  Модуль трассировки осевых линий трубопроводов (Pipe Centerlines)
//  с вычислением условного диаметра (DN) и выводом в 3D Polyline.
//
//  Разработчик: Baidurov Pavel / BaidurovLabs
//  Лицензия: GNU General Public License v3.0 (GPLv3)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace NWD2DWG.Plugin
{
    public class PipeSegment
    {
        public double StartX, StartY, StartZ;
        public double EndX, EndY, EndZ;
        public double Diameter; // Внешний диаметр
        public double Length;
        public string SystemName;
    }

    public static class PipeTracer
    {
        /// <summary>
        /// Попытка извлечь осевую линию из цилиндрического объекта или распознанного SolidResult
        /// </summary>
        public static PipeSegment TraceFromSolid(SolidResult solid, string systemName = "Piping")
        {
            if (solid == null || solid.Type != SolidType.Cylinder || solid.Confidence < 0.7)
                return null;

            double halfH = solid.Height / 2.0;
            var seg = new PipeSegment
            {
                StartX = solid.CenterX - solid.AxisX * halfH,
                StartY = solid.CenterY - solid.AxisY * halfH,
                StartZ = solid.CenterZ - solid.AxisZ * halfH,
                EndX = solid.CenterX + solid.AxisX * halfH,
                EndY = solid.CenterY + solid.AxisY * halfH,
                EndZ = solid.CenterZ + solid.AxisZ * halfH,
                Diameter = solid.Radius * 2.0,
                Length = solid.Height,
                SystemName = systemName
            };
            return seg;
        }

        /// <summary>
        /// Запись осевых линий труб в DXF слой _PIPE_AXIS
        /// </summary>
        public static void WritePipeAxesToDxf(StreamWriter w, IList<PipeSegment> pipes, int colorAci = 4) // 4=Cyan
        {
            if (w == null || pipes == null || pipes.Count == 0) return;
            var ci = CultureInfo.InvariantCulture;

            foreach (var p in pipes)
            {
                string layer = "_PIPE_AXIS";

                // Осевая линия (LINE)
                w.WriteLine("0");
                w.WriteLine("LINE");
                w.WriteLine("8");
                w.WriteLine(layer);
                w.WriteLine("62");
                w.WriteLine(colorAci.ToString(ci));
                w.WriteLine("10");
                w.WriteLine(p.StartX.ToString("G12", ci));
                w.WriteLine("20");
                w.WriteLine(p.StartY.ToString("G12", ci));
                w.WriteLine("30");
                w.WriteLine(p.StartZ.ToString("G12", ci));
                w.WriteLine("11");
                w.WriteLine(p.EndX.ToString("G12", ci));
                w.WriteLine("21");
                w.WriteLine(p.EndY.ToString("G12", ci));
                w.WriteLine("31");
                w.WriteLine(p.EndZ.ToString("G12", ci));

                // Текстовая аннотация диаметра по центру отрезка
                double midX = (p.StartX + p.EndX) / 2.0;
                double midY = (p.StartY + p.EndY) / 2.0;
                double midZ = (p.StartZ + p.EndZ) / 2.0;
                double txtHeight = Math.Max(20.0, p.Diameter * 0.4);

                w.WriteLine("0");
                w.WriteLine("TEXT");
                w.WriteLine("8");
                w.WriteLine(layer);
                w.WriteLine("62");
                w.WriteLine("7"); // White
                w.WriteLine("10");
                w.WriteLine(midX.ToString("G12", ci));
                w.WriteLine("20");
                w.WriteLine(midY.ToString("G12", ci));
                w.WriteLine("30");
                w.WriteLine(midZ.ToString("G12", ci));
                w.WriteLine("40");
                w.WriteLine(txtHeight.ToString("G12", ci));
                w.WriteLine("1");
                w.WriteLine(string.Format(ci, "DN{0:F0} L={1:F0}", p.Diameter, p.Length));
            }
        }
    }
}
