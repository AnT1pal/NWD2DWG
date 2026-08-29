// ============================================================================
//  NWD2DWG — GeoTransform.cs
//  Модуль геодезической привязки, вычисления смещения к началу координат (0,0,0)
//  и экспорта файлов геопривязки (.wld, .prj, transform.json).
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
    public class GeoTransformResult
    {
        public double OffsetX;
        public double OffsetY;
        public double OffsetZ;
        public double MinX, MinY, MinZ;
        public double MaxX, MaxY, MaxZ;
        public double CenterX, CenterY, CenterZ;
        public bool IsShifted;
    }

    public static class GeoTransform
    {
        /// <summary>
        /// Вычисление AABB и смещения к нулю.
        /// Если координаты вершин превышают threshold (по умолчанию 5000 мм / 5 м),
        /// вычисляется смещение для сдвига геометрии в локальный ноль.
        /// </summary>
        public static GeoTransformResult AnalyzeBounds(IList<double> verts, double threshold = 5000.0)
        {
            var res = new GeoTransformResult();
            if (verts == null || verts.Count < 3) return res;

            res.MinX = res.MaxX = verts[0];
            res.MinY = res.MaxY = verts[1];
            res.MinZ = res.MaxZ = verts[2];

            int count = verts.Count;
            for (int i = 3; i < count; i += 3)
            {
                double x = verts[i], y = verts[i + 1], z = verts[i + 2];
                if (x < res.MinX) res.MinX = x; else if (x > res.MaxX) res.MaxX = x;
                if (y < res.MinY) res.MinY = y; else if (y > res.MaxY) res.MaxY = y;
                if (z < res.MinZ) res.MinZ = z; else if (z > res.MaxZ) res.MaxZ = z;
            }

            res.CenterX = (res.MinX + res.MaxX) / 2.0;
            res.CenterY = (res.MinY + res.MaxY) / 2.0;
            res.CenterZ = (res.MinZ + res.MaxZ) / 2.0;

            double distFromOrigin = Math.Sqrt(res.CenterX * res.CenterX + res.CenterY * res.CenterY + res.CenterZ * res.CenterZ);
            if (distFromOrigin > threshold)
            {
                res.IsShifted = true;
                res.OffsetX = Math.Round(res.CenterX, 0);
                res.OffsetY = Math.Round(res.CenterY, 0);
                res.OffsetZ = Math.Round(res.MinZ, 0);
            }
            else
            {
                res.IsShifted = false;
                res.OffsetX = 0;
                res.OffsetY = 0;
                res.OffsetZ = 0;
            }

            return res;
        }

        /// <summary>
        /// Применение смещения к массиву вершин (сдвиг в локальный ноль)
        /// </summary>
        public static void ApplyShift(IList<double> verts, double offsetX, double offsetY, double offsetZ)
        {
            if (verts == null || (offsetX == 0 && offsetY == 0 && offsetZ == 0)) return;
            int count = verts.Count;
            for (int i = 0; i < count; i += 3)
            {
                verts[i] -= offsetX;
                verts[i + 1] -= offsetY;
                verts[i + 2] -= offsetZ;
            }
        }

        /// <summary>
        /// Сохранение файлов обратной геопривязки (.json, .wld)
        /// </summary>
        public static void SaveGeoreferenceFiles(string baseFilePath, GeoTransformResult geo, int insUnits = 4)
        {
            if (geo == null || !geo.IsShifted) return;

            string baseDir = Path.GetDirectoryName(baseFilePath);
            string baseName = Path.GetFileNameWithoutExtension(baseFilePath);
            if (string.IsNullOrEmpty(baseDir)) baseDir = ".";

            // 1. JSON метаданные
            string jsonPath = Path.Combine(baseDir, baseName + "_georef.json");
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"generator\": \"NWD2DWG v3.0\",");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"timestamp\": \"{0:O}\",", DateTime.Now));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  \"unitsCode\": {0},", insUnits));
            sb.AppendLine("  \"offset\": {");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "    \"x\": {0:F4},", geo.OffsetX));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "    \"y\": {0:F4},", geo.OffsetY));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "    \"z\": {0:F4}", geo.OffsetZ));
            sb.AppendLine("  },");
            sb.AppendLine("  \"originalBounds\": {");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "    \"min\": [{0:F4}, {1:F4}, {2:F4}],", geo.MinX, geo.MinY, geo.MinZ));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "    \"max\": [{0:F4}, {1:F4}, {2:F4}],", geo.MaxX, geo.MaxY, geo.MaxZ));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "    \"center\": [{0:F4}, {1:F4}, {2:F4}]", geo.CenterX, geo.CenterY, geo.CenterZ));
            sb.AppendLine("  }");
            sb.AppendLine("}");
            try { File.WriteAllText(jsonPath, sb.ToString(), Encoding.UTF8); } catch { }

            // 2. World File (.wld) для AutoCAD Map / Civil 3D
            string wldPath = Path.Combine(baseDir, baseName + ".wld");
            var sbWld = new StringBuilder();
            sbWld.AppendLine(string.Format(CultureInfo.InvariantCulture, "0.0, 0.0\t{0:F4}, {1:F4}", geo.OffsetX, geo.OffsetY));
            sbWld.AppendLine(string.Format(CultureInfo.InvariantCulture, "100.0, 100.0\t{0:F4}, {1:F4}", geo.OffsetX + 100.0, geo.OffsetY + 100.0));
            try { File.WriteAllText(wldPath, sbWld.ToString(), Encoding.ASCII); } catch { }
        }
    }
}
