// ============================================================================
//  DeliveryLog.cs — журнал выдач по объекту
//  NWD2DWG | namespace NWD2DWG.Plugin
//
//  Одна строка на прогон: когда, из какой модели, кем, что выдано.
//  Превращает разрозненные прогоны в прослеживаемую историю по объекту —
//  видно, какая ревизия когда ушла смежникам и что в неё входило.
//
//  Файл общий на объект и дописывается, а не перезаписывается: его можно
//  положить в сетевую папку бюро и вести оттуда всей группой.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    public class DeliveryRecord
    {
        public DateTime Stamp = DateTime.Now;
        public string ProjectCode = "";
        public string DocMark = "";
        public string Model = "";
        public double ModelMb;
        public DateTime ModelChanged;
        public int Elements;
        public long Triangles;
        public int FilesOut;
        public string Preset = "";
        public string User = Environment.UserName;
        public string Machine = Environment.MachineName;
        public string Version = "3.5";
        public string Note = "";
    }

    public static class DeliveryLog
    {
        public const string Header =
            "Дата;Шифр;Марка;Модель;МБ;Изменена;Элементов;Треугольников;Файлов;Шаблон;Пользователь;Машина;Версия;Примечание";

        /// <summary>Дописывает строку. Заголовок создаётся при первом обращении.</summary>
        public static bool Append(string path, DeliveryRecord r)
        {
            if (string.IsNullOrEmpty(path) || r == null) return false;
            var ci = CultureInfo.InvariantCulture;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
                var sb = new StringBuilder();
                if (fresh)
                {
                    sb.AppendLine("sep=;");
                    sb.AppendLine(Header);
                }
                sb.AppendLine(string.Format(ci,
                    "{0:yyyy-MM-dd HH:mm};{1};{2};{3};{4:F1};{5:yyyy-MM-dd};{6};{7};{8};{9};{10};{11};{12};{13}",
                    r.Stamp, C(r.ProjectCode), C(r.DocMark), C(Path.GetFileName(r.Model)),
                    r.ModelMb, r.ModelChanged, r.Elements, r.Triangles, r.FilesOut,
                    C(r.Preset), C(r.User), C(r.Machine), C(r.Version), C(r.Note)));

                // дозапись в UTF-8 без повторной метки: файл ведётся сообща,
                // и лишний BOM в середине сломал бы разбор
                File.AppendAllText(path, sb.ToString(),
                    fresh ? (Encoding)new UTF8Encoding(true) : new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        private static string C(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace(';', ',').Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>Читает журнал для отчётов и внешних инструментов.</summary>
        public static List<string[]> Read(string path)
        {
            var res = new List<string[]>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return res;
            try
            {
                foreach (string line in File.ReadAllLines(path, new UTF8Encoding(true)))
                {
                    if (line.StartsWith("sep=", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.StartsWith("Дата;", StringComparison.Ordinal)) continue;
                    if (line.Trim().Length == 0) continue;
                    res.Add(line.Split(';'));
                }
            }
            catch { }
            return res;
        }
    }
}
