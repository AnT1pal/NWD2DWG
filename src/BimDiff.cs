// ============================================================================
//  NWD2DWG — BimDiff.cs
//  Модуль 3D BIM Diff — сравнение двух версий моделей
//  (Добавлено = Зеленый ACI 3, Удалено = Красный ACI 1, Изменено = Желтый ACI 2).
//
//  Разработчик: Baidurov Pavel / BaidurovLabs
//  Лицензия: GNU General Public License v3.0 (GPLv3)
// ============================================================================

using System;
using System.Collections.Generic;

namespace NWD2DWG.Plugin
{
    public enum DiffStatus { Added, Deleted, Modified, Unchanged }

    public class DiffElement
    {
        public string Guid;
        public string Name;
        public string Category;
        public List<double> Verts;
        public List<int> Quads;
        public DiffStatus Status;
        public int ColorAci => Status == DiffStatus.Added ? 3 // Green
                             : Status == DiffStatus.Deleted ? 1 // Red
                             : Status == DiffStatus.Modified ? 2 // Yellow
                             : 7; // White/Unchanged
    }

    public static class BimDiffEngine
    {
        /// <summary>
        /// Сравнение двух списков элементов по GUID и сигнатурам геометрии (AABB + хеш вершин)
        /// </summary>
        public static List<DiffElement> Compare(IDictionary<string, DiffElement> oldModel, IDictionary<string, DiffElement> newModel)
        {
            var results = new List<DiffElement>();
            if (oldModel == null) oldModel = new Dictionary<string, DiffElement>();
            if (newModel == null) newModel = new Dictionary<string, DiffElement>();

            // 1. Проверяем элементы новой модели
            foreach (var kv in newModel)
            {
                string id = kv.Key;
                DiffElement newElem = kv.Value;

                DiffElement oldElem;
                if (!oldModel.TryGetValue(id, out oldElem))
                {
                    // Новый элемент
                    newElem.Status = DiffStatus.Added;
                    results.Add(newElem);
                }
                else
                {
                    // Элемент есть в обеих моделях — проверяем геометрию
                    if (IsGeometryChanged(oldElem.Verts, newElem.Verts))
                    {
                        newElem.Status = DiffStatus.Modified;
                        results.Add(newElem);
                    }
                    else
                    {
                        newElem.Status = DiffStatus.Unchanged;
                    }
                }
            }

            // 2. Проверяем удаленные элементы из старой модели
            foreach (var kv in oldModel)
            {
                string id = kv.Key;
                DiffElement oldElem = kv.Value;

                if (!newModel.ContainsKey(id))
                {
                    oldElem.Status = DiffStatus.Deleted;
                    results.Add(oldElem);
                }
            }

            return results;
        }

        private static bool IsGeometryChanged(List<double> v1, List<double> v2, double tolerance = 1.0)
        {
            if (v1 == null || v2 == null) return v1 != v2;
            if (v1.Count != v2.Count) return true;

            int count = v1.Count;
            for (int i = 0; i < count; i++)
            {
                if (Math.Abs(v1[i] - v2[i]) > tolerance) return true;
            }
            return false;
        }
    }
}
