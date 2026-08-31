// ============================================================================
//  NWD2DWG — BoqCalculator.cs
//  Модуль расчета физических объемов (Bill of Quantities / ВОР)
//  и экспорта сводной ведомости материалов в CSV / Excel.
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
    public class BoqItem
    {
        public string Category;
        public string Name;
        public string Material;
        public int ElementCount;
        public double TotalAreaM2;      // Площадь поверхности (м²)
        public double TotalVolumeM3;    // Приближенный объем (м³)
        public double TotalLengthM;     // Длина (м)
        public double EstimatedMassKg;  // Расчетная масса (кг)
    }

    public class BoqCalculator
    {
        private readonly Dictionary<string, BoqItem> _items = new Dictionary<string, BoqItem>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Добавить геометрический фрагмент в расчет объемов
        /// </summary>
        // Марка материала приходит из модели как её набрал проектировщик.
        // На реальном объекте «Сталь 20» встретилась тремя написаниями сразу:
        // «Сталь 20», «сталь 20» и «Cталь 20» с латинской C — и разошлась в
        // ведомости на три позиции вместо одной. Латинские двойники кириллицы
        // в русских марках — обычное дело: их набирают по привычке к
        // английской раскладке, а на вид они неотличимы.
        //
        // Поэтому для группировки марка приводится к одному виду: латинские
        // двойники заменяются кириллицей, регистр и лишние пробелы убираются.
        // В саму ведомость при этом попадает первое встреченное написание —
        // подменять то, что написал проектировщик, мы не вправе.
        private static readonly string LatinLookalikes  = "ACEHKMOPTXBacepxyo";
        private static readonly string CyrillicTwins    = "АСЕНКМОРТХВасерхуо";

        internal static string MaterialKey(string material)
        {
            if (string.IsNullOrEmpty(material)) return "";
            var sb = new StringBuilder(material.Length);
            foreach (char c in material)
            {
                int i = LatinLookalikes.IndexOf(c);
                char ch = i >= 0 ? CyrillicTwins[i] : c;
                if (char.IsWhiteSpace(ch))
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                }
                else sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString().Trim();
        }

        public void AddMesh(string category, string name, string material, IList<double> verts, IList<int> quads)
        {
            if (verts == null || verts.Count < 9 || quads == null || quads.Count < 4) return;

            // Приводить нужно обе части ключа. При группировке по материалу
            // марка попадает и в категорию тоже: если привести только колонку
            // материала, «Сталь 20» и «Cталь 20» с латинской C всё равно
            // разойдутся по разным позициям — что и случилось на проверке.
            string key = MaterialKey(category ?? "General") + "::" + MaterialKey(material ?? "Default");
            BoqItem item;
            if (!_items.TryGetValue(key, out item))
            {
                item = new BoqItem
                {
                    Category = category ?? "General",
                    Name = name ?? "Element",
                    Material = material ?? "Default",
                    ElementCount = 0,
                    TotalAreaM2 = 0,
                    TotalVolumeM3 = 0,
                    TotalLengthM = 0,
                    EstimatedMassKg = 0
                };
                _items[key] = item;
            }

            item.ElementCount++;

            // 1. Вычисление площади поверхности и знакового объема (Divergence Theorem / Gauss theorem)
            double areaSum = 0.0;
            double volSum = 0.0;
            int triCount = quads.Count / 4;

            for (int t = 0; t < triCount; t++)
            {
                int i1 = quads[t * 4] * 3;
                int i2 = quads[t * 4 + 1] * 3;
                int i3 = quads[t * 4 + 2] * 3;

                double ax = verts[i1], ay = verts[i1 + 1], az = verts[i1 + 2];
                double bx = verts[i2], by = verts[i2 + 1], bz = verts[i2 + 2];
                double cx = verts[i3], cy = verts[i3 + 1], cz = verts[i3 + 2];

                // Векторные произведения
                double abx = bx - ax, aby = by - ay, abz = bz - az;
                double acx = cx - ax, acy = cy - ay, acz = cz - az;

                double nx = aby * acz - abz * acy;
                double ny = abz * acx - abx * acz;
                double nz = abx * acy - aby * acx;

                double triArea = 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
                areaSum += triArea;

                // Знаковый объем пирамиды от начала координат
                volSum += (ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx)) / 6.0;
            }

            // Перевод из миллиметров (мм², мм³) в метры (м², м³)
            double areaM2 = areaSum / 1000000.0;
            double volM3 = Math.Abs(volSum) / 1000000000.0;

            item.TotalAreaM2 += areaM2;
            item.TotalVolumeM3 += volM3;

            // Плотность материала (по умолчанию 2400 кг/м³ для бетона или 7850 кг/м³ для стали)
            double density = 2400.0;
            string matLower = (material ?? "").ToLowerInvariant();
            if (matLower.Contains("steel") || matLower.Contains("метал") || matLower.Contains("сталь")) density = 7850.0;
            else if (matLower.Contains("wood") || matLower.Contains("дерев")) density = 600.0;
            else if (matLower.Contains("pipe") || matLower.Contains("труб")) density = 1500.0;

            item.EstimatedMassKg += volM3 * density;
        }

        /// <summary>
        /// Экспорт сводной ведомости объемов работ в CSV (совместим с Excel)
        /// </summary>
        public void ExportCsv(string csvPath) { ExportCsv(csvPath, 0.0); }

        /// <summary>Позиции объёмом меньше minVolumeM3 в ведомость не попадают.</summary>
        public void ExportCsv(string csvPath, double minVolumeM3)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Категория / Раздел;Материал;Геом. фрагментов;Площадь (м2);Объем (м3);Примерная масса (т)");

            foreach (var kv in _items)
            {
                var it = kv.Value;
                if (minVolumeM3 > 0 && it.TotalVolumeM3 < minVolumeM3) continue;
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "\"{0}\";\"{1}\";{2};{3:F2};{4:F3};{5:F2}",
                    it.Category, it.Material, it.ElementCount,
                    it.TotalAreaM2, it.TotalVolumeM3, it.EstimatedMassKg / 1000.0));
            }

            string dir = Path.GetDirectoryName(csvPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
        }

        public ICollection<BoqItem> GetItems() => _items.Values;
    }
}
