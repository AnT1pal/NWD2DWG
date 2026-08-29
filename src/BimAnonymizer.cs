// ============================================================================
//  NWD2DWG — BimAnonymizer.cs & PropertyHeatmap.cs
//  Модули фильтрации коммерческих атрибутов и построения тепловых карт свойств.
//
//  Разработчик: Baidurov Pavel / BaidurovLabs
//  Лицензия: GNU General Public License v3.0 (GPLv3)
// ============================================================================

using System;
using System.Collections.Generic;

namespace NWD2DWG.Plugin
{
    public static class BimAnonymizer
    {
        // Список ключевых слов конфиденциальных свойств для удаления
        private static readonly string[] SensitiveKeywords = new[]
        {
            "cost", "price", "стоимость", "цена", "supplier", "поставщик",
            "contractor", "подрядчик", "author", "автор", "email", "phone",
            "comment", "примечание", "internal", "внутренн"
        };

        /// <summary>
        /// Очистка словаря свойств от коммерческих и конфиденциальных данных
        /// </summary>
        public static Dictionary<string, string> SanitizeProperties(IDictionary<string, string> rawProps)
        {
            if (rawProps == null) return null;
            var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in rawProps)
            {
                string key = kv.Key.ToLowerInvariant();
                bool isSensitive = false;
                foreach (string kw in SensitiveKeywords)
                {
                    if (key.Contains(kw))
                    {
                        isSensitive = true;
                        break;
                    }
                }
                if (!isSensitive)
                {
                    clean[kv.Key] = kv.Value;
                }
            }
            return clean;
        }
    }

    public static class PropertyHeatmap
    {
        /// <summary>
        /// Вычисление цвета ACI (1..7) по значению выбранного свойства (Heatmap)
        /// </summary>
        public static int GetHeatmapColorAci(string propertyValue)
        {
            if (string.IsNullOrEmpty(propertyValue)) return 7; // White

            // Хэш значения в диапазон базовых цветов AutoCAD (1=Red, 2=Yellow, 3=Green, 4=Cyan, 5=Blue, 6=Magenta)
            int hash = Math.Abs(propertyValue.GetHashCode());
            int[] palette = new[] { 1, 2, 3, 4, 5, 6 };
            return palette[hash % palette.Length];
        }
    }
}
