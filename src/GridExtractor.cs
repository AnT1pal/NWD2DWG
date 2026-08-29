// ============================================================================
//  NWD2DWG — GridExtractor.cs
//  Модуль извлечения строительных координационных осей и уровней
//  с записью в DXF (слои _GRIDS и _LEVELS).
//
//  Разработчик: Baidurov Pavel / BaidurovLabs
//  Лицензия: GNU General Public License v3.0 (GPLv3)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    public class GridLineData
    {
        public string Name;
        public double StartX, StartY, StartZ;
        public double EndX, EndY, EndZ;
        public bool IsLevel; // true = отметка уровня (горизонтальная плоскость), false = координационная ось
    }

    public static class GridExtractor
    {
        /// <summary>
        /// Запись координационных осей и отметок уровней в открытый DXF поток
        /// </summary>
        public static void WriteGridsToDxf(StreamWriter w, IList<GridLineData> grids, double textHeight = 500.0)
        {
            if (w == null || grids == null || grids.Count == 0) return;
            var ci = CultureInfo.InvariantCulture;

            foreach (var g in grids)
            {
                string layer = g.IsLevel ? "_LEVELS" : "_GRIDS";
                int colorAci = g.IsLevel ? 1 : 2; // 1=Red, 2=Yellow

                // 1. Отрезок оси (LINE)
                w.WriteLine("0");
                w.WriteLine("LINE");
                w.WriteLine("8");
                w.WriteLine(layer);
                w.WriteLine("62");
                w.WriteLine(colorAci.ToString(ci));
                w.WriteLine("10");
                w.WriteLine(g.StartX.ToString("G12", ci));
                w.WriteLine("20");
                w.WriteLine(g.StartY.ToString("G12", ci));
                w.WriteLine("30");
                w.WriteLine(g.StartZ.ToString("G12", ci));
                w.WriteLine("11");
                w.WriteLine(g.EndX.ToString("G12", ci));
                w.WriteLine("21");
                w.WriteLine(g.EndY.ToString("G12", ci));
                w.WriteLine("31");
                w.WriteLine(g.EndZ.ToString("G12", ci));

                // 2. Марка оси на концах (TEXT + CIRCLE)
                if (!string.IsNullOrEmpty(g.Name))
                {
                    double circleRadius = textHeight * 1.2;

                    // Круг на старте
                    w.WriteLine("0");
                    w.WriteLine("CIRCLE");
                    w.WriteLine("8");
                    w.WriteLine(layer);
                    w.WriteLine("62");
                    w.WriteLine(colorAci.ToString(ci));
                    w.WriteLine("10");
                    w.WriteLine(g.StartX.ToString("G12", ci));
                    w.WriteLine("20");
                    w.WriteLine(g.StartY.ToString("G12", ci));
                    w.WriteLine("30");
                    w.WriteLine(g.StartZ.ToString("G12", ci));
                    w.WriteLine("40");
                    w.WriteLine(circleRadius.ToString("G12", ci));

                    // Текст марки на старте
                    w.WriteLine("0");
                    w.WriteLine("TEXT");
                    w.WriteLine("8");
                    w.WriteLine(layer);
                    w.WriteLine("62");
                    w.WriteLine("7"); // Белый текст
                    w.WriteLine("10");
                    w.WriteLine((g.StartX - textHeight * 0.4).ToString("G12", ci));
                    w.WriteLine("20");
                    w.WriteLine((g.StartY - textHeight * 0.4).ToString("G12", ci));
                    w.WriteLine("30");
                    w.WriteLine(g.StartZ.ToString("G12", ci));
                    w.WriteLine("40");
                    w.WriteLine(textHeight.ToString("G12", ci));
                    w.WriteLine("1");
                    w.WriteLine(g.Name);
                }
            }
        }
    }
}
