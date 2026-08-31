// ============================================================================
//  RevisionIndex.cs — индекс выдачи и сравнение ревизий
//  NWD2DWG | namespace NWD2DWG.Plugin
//
//  Каждый прогон записывает компактный индекс модели: по строке на элемент
//  с габаритами и объёмом геометрии. Сравнение двух таких индексов отвечает
//  на главный вопрос еженедельной выдачи — «что изменилось с прошлого раза».
//
//  Сравниваются именно ИНДЕКСЫ, а не модели: для этого не нужен ни Navisworks,
//  ни исходные файлы. Индекс весит доли процента от модели, его можно переслать
//  смежнику и сравнить у него.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    public class IndexEntry
    {
        public string Name;
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public int Fragments;
        public long Triangles;

        public double Cx { get { return (MinX + MaxX) / 2; } }
        public double Cy { get { return (MinY + MaxY) / 2; } }
        public double Cz { get { return (MinZ + MaxZ) / 2; } }
        public double Dx { get { return MaxX - MinX; } }
        public double Dy { get { return MaxY - MinY; } }
        public double Dz { get { return MaxZ - MinZ; } }

        public void Expand(double x0, double y0, double z0, double x1, double y1, double z1)
        {
            if (Fragments == 0)
            {
                MinX = x0; MinY = y0; MinZ = z0;
                MaxX = x1; MaxY = y1; MaxZ = z1;
                return;
            }
            if (x0 < MinX) MinX = x0; if (x1 > MaxX) MaxX = x1;
            if (y0 < MinY) MinY = y0; if (y1 > MaxY) MaxY = y1;
            if (z0 < MinZ) MinZ = z0; if (z1 > MaxZ) MaxZ = z1;
        }
    }

    public enum ChangeKind { Added, Removed, Moved, Reshaped, Retriangulated }

    public class DiffItem
    {
        public ChangeKind Kind;
        public string Name;
        public double X, Y, Z;        // где показать метку
        public double ShiftMm;        // смещение центра
        public double SizeDeltaMm;    // изменение габарита
        public long TriDelta;
        public string Comment;
    }

    public class DiffResult
    {
        public readonly List<DiffItem> Items = new List<DiffItem>();
        public int OldCount, NewCount, Same;

        // Общий сдвиг всей модели, если он найден. Возникает при смене нуля
        // площадки: сместились не элементы, а система координат.
        public bool   HasBaseShift;
        public double BaseX, BaseY, BaseZ;
        public int    BaseShiftCount;

        public double BaseLength
        {
            get { return Math.Sqrt(BaseX * BaseX + BaseY * BaseY + BaseZ * BaseZ); }
        }

        public int CountOf(ChangeKind k)
        {
            int n = 0;
            foreach (var it in Items) if (it.Kind == k) n++;
            return n;
        }
    }

    public static class RevisionIndex
    {
        public const string Header =
            "Элемент;MinX;MinY;MinZ;MaxX;MaxY;MaxZ;Фрагментов;Треугольников";

        // --------------------------------------------------------------------
        // Запись и чтение
        // --------------------------------------------------------------------
        public static void Write(string path, ICollection<IndexEntry> entries, string modelName)
        {
            var ci = CultureInfo.InvariantCulture;
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine("# NWD2DWG revision index v1");
                w.WriteLine("# model=" + (modelName ?? ""));
                w.WriteLine(string.Format(ci, "# created={0:yyyy-MM-ddTHH:mm:ss}", DateTime.Now));
                w.WriteLine(Header);
                foreach (var e in entries)
                    w.WriteLine(string.Format(ci, "{0};{1:F1};{2:F1};{3:F1};{4:F1};{5:F1};{6:F1};{7};{8}",
                        (e.Name ?? "").Replace(';', ','),
                        e.MinX, e.MinY, e.MinZ, e.MaxX, e.MaxY, e.MaxZ,
                        e.Fragments, e.Triangles));
            }
        }

        public static List<IndexEntry> Read(string path)
        {
            var ci = CultureInfo.InvariantCulture;
            var res = new List<IndexEntry>();
            foreach (string line in File.ReadAllLines(path, new UTF8Encoding(true)))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("Элемент;", StringComparison.Ordinal)) continue;
                string[] c = line.Split(';');
                if (c.Length < 9) continue;
                var e = new IndexEntry { Name = c[0] };
                double d;
                if (!double.TryParse(c[1], NumberStyles.Float, ci, out d)) continue; e.MinX = d;
                if (!double.TryParse(c[2], NumberStyles.Float, ci, out d)) continue; e.MinY = d;
                if (!double.TryParse(c[3], NumberStyles.Float, ci, out d)) continue; e.MinZ = d;
                if (!double.TryParse(c[4], NumberStyles.Float, ci, out d)) continue; e.MaxX = d;
                if (!double.TryParse(c[5], NumberStyles.Float, ci, out d)) continue; e.MaxY = d;
                if (!double.TryParse(c[6], NumberStyles.Float, ci, out d)) continue; e.MaxZ = d;
                int n; long t;
                int.TryParse(c[7], NumberStyles.Integer, ci, out n); e.Fragments = n;
                long.TryParse(c[8], NumberStyles.Integer, ci, out t); e.Triangles = t;
                res.Add(e);
            }
            return res;
        }

        // --------------------------------------------------------------------
        // Сравнение
        // --------------------------------------------------------------------
        /// <param name="tolMm">смещение центра, ниже которого элемент считается неизменным</param>
        /// <param name="triTolPct">изменение числа треугольников в процентах</param>
        public static DiffResult Compare(List<IndexEntry> older, List<IndexEntry> newer,
                                         double tolMm = 5.0, double triTolPct = 2.0)
        {
            var res = new DiffResult { OldCount = older.Count, NewCount = newer.Count };

            var oldMap = BuildMap(older);
            var newMap = BuildMap(newer);

            DetectBaseShift(oldMap, newMap, res, tolMm);

            foreach (var kv in newMap)
            {
                IndexEntry o;
                if (!oldMap.TryGetValue(kv.Key, out o))
                {
                    var n = kv.Value;
                    res.Items.Add(new DiffItem
                    {
                        Kind = ChangeKind.Added, Name = n.Name,
                        X = n.Cx, Y = n.Cy, Z = n.Cz,
                        TriDelta = n.Triangles,
                        Comment = "новый элемент"
                    });
                    continue;
                }

                var w = kv.Value;
                // Общий сдвиг вычитается: иначе перенос нуля площадки выглядит
                // как переделка всей модели, и в отчёте тонут настоящие правки.
                double dx = w.Cx - o.Cx - res.BaseX;
                double dy = w.Cy - o.Cy - res.BaseY;
                double dz = w.Cz - o.Cz - res.BaseZ;
                double shift = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                double dSize = Math.Max(Math.Abs(w.Dx - o.Dx),
                               Math.Max(Math.Abs(w.Dy - o.Dy), Math.Abs(w.Dz - o.Dz)));
                long dTri = w.Triangles - o.Triangles;
                double triPct = o.Triangles > 0 ? Math.Abs(dTri) * 100.0 / o.Triangles : (dTri != 0 ? 100 : 0);

                // Габарит проверяется первым: изменение размера почти всегда
                // сдвигает и центр, поэтому обратный порядок показывал бы
                // «смещён» там, где элемент на самом деле переделан.
                // Перемещение без правки — это когда размеры совпали.
                if (dSize > tolMm)
                {
                    res.Items.Add(new DiffItem
                    {
                        Kind = ChangeKind.Reshaped, Name = w.Name,
                        X = w.Cx, Y = w.Cy, Z = w.Cz,
                        ShiftMm = shift, SizeDeltaMm = dSize, TriDelta = dTri,
                        Comment = string.Format(CultureInfo.InvariantCulture,
                            shift > tolMm ? "габарит изменён на {0:F0} мм, центр сместился на {1:F0} мм"
                                          : "габарит изменён на {0:F0} мм", dSize, shift)
                    });
                }
                else if (shift > tolMm)
                {
                    res.Items.Add(new DiffItem
                    {
                        Kind = ChangeKind.Moved, Name = w.Name,
                        X = w.Cx, Y = w.Cy, Z = w.Cz,
                        ShiftMm = shift, SizeDeltaMm = dSize, TriDelta = dTri,
                        Comment = string.Format(CultureInfo.InvariantCulture, "смещён на {0:F0} мм", shift)
                    });
                }
                else if (triPct > triTolPct)
                {
                    // геометрия перестроена без смещения: часто это правка формы,
                    // которую по габаритам не видно
                    res.Items.Add(new DiffItem
                    {
                        Kind = ChangeKind.Retriangulated, Name = w.Name,
                        X = w.Cx, Y = w.Cy, Z = w.Cz,
                        TriDelta = dTri,
                        Comment = string.Format(CultureInfo.InvariantCulture,
                                                "геометрия перестроена ({0:+#;-#;0} треуг.)", dTri)
                    });
                }
                else res.Same++;
            }

            foreach (var kv in oldMap)
            {
                if (newMap.ContainsKey(kv.Key)) continue;
                var o = kv.Value;
                res.Items.Add(new DiffItem
                {
                    Kind = ChangeKind.Removed, Name = o.Name,
                    X = o.Cx, Y = o.Cy, Z = o.Cz,
                    TriDelta = -o.Triangles,
                    Comment = "элемент удалён"
                });
            }

            res.Items.Sort((a, b) => a.Kind.CompareTo(b.Kind));
            return res;
        }

        // --------------------------------------------------------------------
        // Поиск общего сдвига.
        //
        // Когда проект переносят на другой ноль площадки, смещаются все
        // элементы разом и на одну и ту же величину. Без этой проверки отчёт
        // объявляет изменившимся весь объект: на реальном сравнении рабочей и
        // координационной моделей так получилось 4308 строк «смещён» вместо
        // одной строки «модель сдвинута».
        //
        // Ищем самый частый вектор сдвига. Признаём его общим, только если он
        // у большинства сопоставленных элементов: случайное совпадение у
        // половины объекта невозможно, а вот равномерный перенос — обычное дело.
        // --------------------------------------------------------------------
        private static void DetectBaseShift(Dictionary<string, IndexEntry> oldMap,
                                            Dictionary<string, IndexEntry> newMap,
                                            DiffResult res, double tolMm)
        {
            var votes = new Dictionary<string, int>(StringComparer.Ordinal);
            var vectors = new Dictionary<string, double[]>(StringComparer.Ordinal);
            int matched = 0;

            // Округление до допуска: одинаковый перенос не даёт побитово равных
            // чисел — координаты приходят из разных прогонов.
            double bucket = Math.Max(1.0, tolMm);

            foreach (var kv in newMap)
            {
                IndexEntry o;
                if (!oldMap.TryGetValue(kv.Key, out o)) continue;
                matched++;
                var w = kv.Value;
                double dx = w.Cx - o.Cx, dy = w.Cy - o.Cy, dz = w.Cz - o.Cz;
                string key = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}",
                    Math.Round(dx / bucket), Math.Round(dy / bucket), Math.Round(dz / bucket));
                int n;
                votes.TryGetValue(key, out n);
                votes[key] = n + 1;
                if (n == 0) vectors[key] = new double[] { dx, dy, dz };
            }

            if (matched < 10) return;

            string best = null;
            int bestVotes = 0;
            foreach (var kv in votes)
                if (kv.Value > bestVotes) { bestVotes = kv.Value; best = kv.Key; }

            if (best == null || bestVotes * 2 <= matched) return;   // не большинство

            var v = vectors[best];
            double len = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (len <= tolMm) return;                               // сдвига нет

            // Берём среднее по всем элементам этой группы: одиночный вектор
            // несёт округление конкретного элемента.
            double sx = 0, sy = 0, sz = 0;
            int cnt = 0;
            foreach (var kv in newMap)
            {
                IndexEntry o;
                if (!oldMap.TryGetValue(kv.Key, out o)) continue;
                var w = kv.Value;
                double dx = w.Cx - o.Cx, dy = w.Cy - o.Cy, dz = w.Cz - o.Cz;
                string key = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}",
                    Math.Round(dx / bucket), Math.Round(dy / bucket), Math.Round(dz / bucket));
                if (key != best) continue;
                sx += dx; sy += dy; sz += dz; cnt++;
            }
            if (cnt == 0) return;

            res.HasBaseShift = true;
            res.BaseX = sx / cnt;
            res.BaseY = sy / cnt;
            res.BaseZ = sz / cnt;
            res.BaseShiftCount = bestVotes;
        }

        /// <summary>Строка о переносе нуля площадки либо пустая.</summary>
        public static string BaseShiftNote(DiffResult d)
        {
            if (d == null || !d.HasBaseShift) return "";
            return string.Format(CultureInfo.InvariantCulture,
                "модель целиком смещена на {0:F0} мм (X {1:F0}, Y {2:F0}, Z {3:F0}) " +
                "у {4} элементов — перенос учтён, ниже показаны отклонения от него",
                d.BaseLength, d.BaseX, d.BaseY, d.BaseZ, d.BaseShiftCount);
        }

        // Сопоставление идёт по имени. Класть в ключ координату нельзя: тогда
        // сдвинутый элемент получал бы другой ключ и попадал в «удалён плюс
        // добавлен» — то есть категория «смещён» была бы недостижима, а ради
        // неё сравнение и делается.
        //
        // Имена в индексе уникальны, он собирается по имени элемента. Счётчик
        // повторов оставлен на случай индекса из стороннего источника: тогда
        // n-й одноимённый элемент сопоставляется с n-м.
        private static Dictionary<string, IndexEntry> BuildMap(List<IndexEntry> list)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var map = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
            foreach (var e in list)
            {
                int n;
                seen.TryGetValue(e.Name, out n);
                seen[e.Name] = n + 1;
                map[n == 0 ? e.Name
                           : e.Name + "#" + n.ToString(CultureInfo.InvariantCulture)] = e;
            }
            return map;
        }

        // --------------------------------------------------------------------
        // Отчёты
        // --------------------------------------------------------------------
        public static string Summary(DiffResult d)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "было {0}, стало {1} | добавлено {2}, удалено {3}, смещено {4}, изменена форма {5}, перестроено {6}, без изменений {7}",
                d.OldCount, d.NewCount,
                d.CountOf(ChangeKind.Added), d.CountOf(ChangeKind.Removed),
                d.CountOf(ChangeKind.Moved), d.CountOf(ChangeKind.Reshaped),
                d.CountOf(ChangeKind.Retriangulated), d.Same);
        }

        private static string KindName(ChangeKind k)
        {
            switch (k)
            {
                case ChangeKind.Added: return "Добавлен";
                case ChangeKind.Removed: return "Удалён";
                case ChangeKind.Moved: return "Смещён";
                case ChangeKind.Reshaped: return "Изменён габарит";
                default: return "Перестроена геометрия";
            }
        }

        private static int KindColor(ChangeKind k)
        {
            switch (k)
            {
                case ChangeKind.Added: return 3;    // зелёный
                case ChangeKind.Removed: return 1;  // красный
                case ChangeKind.Moved: return 2;    // жёлтый
                case ChangeKind.Reshaped: return 6; // фиолетовый
                default: return 4;                  // голубой
            }
        }

        public static void WriteCsv(string path, DiffResult d, string oldName, string newName)
        {
            var ci = CultureInfo.InvariantCulture;
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine("sep=;");
                w.WriteLine("Сравнение ревизий;" + (oldName ?? "") + " -> " + (newName ?? ""));
                w.WriteLine(Summary(d).Replace(" | ", ";"));
                string note = BaseShiftNote(d);
                if (note.Length > 0) w.WriteLine("Перенос нуля;" + note);
                w.WriteLine();
                w.WriteLine("Изменение;Элемент;X;Y;Z;Смещение мм;Изм. габарита мм;Изм. треугольников;Комментарий");
                foreach (var it in d.Items)
                    w.WriteLine(string.Format(ci, "{0};{1};{2:F0};{3:F0};{4:F0};{5:F0};{6:F0};{7};{8}",
                        KindName(it.Kind), (it.Name ?? "").Replace(';', ','),
                        it.X, it.Y, it.Z, it.ShiftMm, it.SizeDeltaMm, it.TriDelta, it.Comment));
            }
        }

        public static void WriteDxf(string path, DiffResult d)
        {
            var ci = CultureInfo.InvariantCulture;
            using (var w = new StreamWriter(path, false, Encoding.Default))
            {
                w.WriteLine("0\nSECTION\n2\nHEADER");
                w.WriteLine("9\n$ACADVER\n1\nAC1015");
                w.WriteLine("0\nENDSEC");

                w.WriteLine("0\nSECTION\n2\nTABLES");
                w.WriteLine("0\nTABLE\n2\nLAYER\n70\n6");
                w.WriteLine("0\nLAYER\n2\n0\n70\n0\n62\n7\n6\nCONTINUOUS");
                foreach (ChangeKind k in Enum.GetValues(typeof(ChangeKind)))
                    w.WriteLine("0\nLAYER\n2\n_DIFF_" + k.ToString().ToUpperInvariant() +
                                "\n70\n0\n62\n" + KindColor(k) + "\n6\nCONTINUOUS");
                w.WriteLine("0\nENDTAB");
                w.WriteLine("0\nENDSEC");

                w.WriteLine("0\nSECTION\n2\nENTITIES");
                foreach (var it in d.Items)
                {
                    string layer = "_DIFF_" + it.Kind.ToString().ToUpperInvariant();
                    int col = KindColor(it.Kind);

                    // крестик-маркер и подпись
                    const double r = 250.0;
                    WriteLine(w, it.X - r, it.Y, it.Z, it.X + r, it.Y, it.Z, layer, col);
                    WriteLine(w, it.X, it.Y - r, it.Z, it.X, it.Y + r, it.Z, layer, col);
                    WriteLine(w, it.X, it.Y, it.Z - r, it.X, it.Y, it.Z + r, layer, col);

                    w.WriteLine("0\nTEXT");
                    w.WriteLine("8\n" + layer);
                    w.WriteLine("62\n" + col.ToString(ci));
                    w.WriteLine(string.Format(ci, "10\n{0:F1}\n20\n{1:F1}\n30\n{2:F1}",
                                it.X + r, it.Y, it.Z + r));
                    w.WriteLine("40\n200");
                    w.WriteLine("1\n" + KindName(it.Kind) + ": " + (it.Name ?? ""));
                }
                w.WriteLine("0\nENDSEC");
                w.WriteLine("0\nEOF");
            }
        }

        private static void WriteLine(StreamWriter w, double x1, double y1, double z1,
                                      double x2, double y2, double z2, string layer, int col)
        {
            var ci = CultureInfo.InvariantCulture;
            w.WriteLine("0\nLINE");
            w.WriteLine("8\n" + layer);
            w.WriteLine("62\n" + col.ToString(ci));
            w.WriteLine(string.Format(ci, "10\n{0:F1}\n20\n{1:F1}\n30\n{2:F1}", x1, y1, z1));
            w.WriteLine(string.Format(ci, "11\n{0:F1}\n21\n{1:F1}\n31\n{2:F1}", x2, y2, z2));
        }
    }
}
