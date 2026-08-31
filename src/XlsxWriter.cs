// ============================================================================
//  XlsxWriter.cs — ведомости в формате Excel
//  NWD2DWG | namespace NWD2DWG.Plugin
//
//  В настройках формат ведомостей можно было выбрать «Xlsx», но программа всё
//  равно писала CSV: признак объявили и нигде не использовали. Обещание в
//  интерфейсе без исполнения — худший вид ошибки, потому что заметить его
//  можно только по содержимому папки.
//
//  Книга собирается вручную, без внешних библиотек: xlsx — это zip с
//  несколькими файлами XML. Пишется минимальный набор частей, который Excel и
//  LibreOffice открывают без вопросов. Строки идут встроенными (inline),
//  поэтому таблица общих строк не нужна вовсе.
//
//  Зачем это нужно, если есть CSV: у CSV нет типов и нет кодировки внутри
//  файла. Русский Excel открывает его правильно только с подсказкой «sep=» и
//  в Windows-1251, числа с запятой распознаются не всегда, а длинные
//  обозначения вроде 08Х18Н10Т иногда превращаются в даты. В xlsx число
//  хранится числом, текст текстом, кодировка всегда UTF-8.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace NWD2DWG.Plugin
{
    public static class XlsxWriter
    {
        /// <summary>
        /// Собирает книгу из уже готовой ведомости CSV.
        ///
        /// Источником берётся именно CSV: он к этому моменту уже приведён к
        /// профилю выдачи — нужные колонки, разделитель, кодировка. Разбирать
        /// его повторно дешевле, чем дублировать по всем модулям вторую ветку
        /// записи и потом следить, чтобы они не разошлись.
        /// </summary>
        public static bool FromCsv(string csvPath, string xlsxPath, char separator,
                                   Encoding csvEncoding, string sheetName)
        {
            if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath)) return false;

            List<string[]> rows;
            try
            {
                rows = new List<string[]>();
                foreach (string line in File.ReadAllLines(csvPath, csvEncoding ?? Encoding.UTF8))
                {
                    // Подсказка для CSV в книге не нужна: у листа есть колонки.
                    if (line.StartsWith("sep=", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.Length == 0) { rows.Add(new string[0]); continue; }
                    rows.Add(SplitCsv(line, separator));
                }
            }
            catch { return false; }

            if (rows.Count == 0) return false;

            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(xlsxPath));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(xlsxPath)) File.Delete(xlsxPath);

                using (var zip = ZipFile.Open(xlsxPath, ZipArchiveMode.Create))
                {
                    Add(zip, "[Content_Types].xml", ContentTypes());
                    Add(zip, "_rels/.rels", RootRels());
                    Add(zip, "xl/workbook.xml", Workbook(sheetName));
                    Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
                    Add(zip, "xl/styles.xml", Styles());
                    Add(zip, "xl/worksheets/sheet1.xml", Sheet(rows));
                }
                return true;
            }
            catch { return false; }
        }

        // --------------------------------------------------------------------
        // Разбор строки CSV с учётом кавычек
        // --------------------------------------------------------------------
        private static string[] SplitCsv(string line, char sep)
        {
            var res = new List<string>();
            var cur = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // Удвоенная кавычка внутри поля — это одна кавычка.
                        if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else cur.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == sep) { res.Add(cur.ToString()); cur.Length = 0; }
                else cur.Append(c);
            }
            res.Add(cur.ToString());
            return res.ToArray();
        }

        // --------------------------------------------------------------------
        // Лист
        // --------------------------------------------------------------------
        private static string Sheet(List<string[]> rows)
        {
            var sb = new StringBuilder(1 << 16);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

            // Заголовок закрепляем: ведомости длинные, без этого ими неудобно
            // пользоваться — а именно для удобства xlsx и выбирают.
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
            sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            sb.Append("</sheetView></sheetViews>");

            sb.Append("<sheetData>");
            for (int r = 0; r < rows.Count; r++)
            {
                var cells = rows[r];
                sb.Append("<row r=\"").Append(r + 1).Append("\">");
                for (int c = 0; c < cells.Length; c++)
                {
                    string v = cells[c];
                    if (string.IsNullOrEmpty(v)) continue;
                    string reference = Ref(c) + (r + 1);

                    double num;
                    if (r > 0 && TryNumber(v, out num))
                    {
                        sb.Append("<c r=\"").Append(reference).Append("\">")
                          .Append("<v>").Append(num.ToString("R", CultureInfo.InvariantCulture))
                          .Append("</v></c>");
                    }
                    else
                    {
                        // Первая строка — шапка, ей стиль с полужирным.
                        sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"")
                          .Append(r == 0 ? " s=\"1\"" : "").Append(">")
                          .Append("<is><t xml:space=\"preserve\">").Append(Esc(v))
                          .Append("</t></is></c>");
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        /// <summary>
        /// Число ли это. Принимаем и точку, и запятую: ведомость может быть
        /// приведена к русскому Excel, где разделитель — запятая.
        ///
        /// Обозначения вроде 09Г2С числом не являются и остаются текстом —
        /// именно из-за таких Excel и портит ведомости, распознавая их сам.
        /// </summary>
        private static bool TryNumber(string s, out double value)
        {
            value = 0;
            s = s.Trim();
            if (s.Length == 0 || s.Length > 24) return false;

            string norm = s.Replace(',', '.').Replace(" ", "");
            return double.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string Ref(int col)
        {
            var sb = new StringBuilder(3);
            col++;
            while (col > 0)
            {
                int rem = (col - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                col = (col - 1) / 26;
            }
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default:
                        // Управляющие символы в XML недопустимы.
                        if (c >= 0x20 || c == '\t') sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // --------------------------------------------------------------------
        // Обязательные части книги
        // --------------------------------------------------------------------
        private static void Add(ZipArchive zip, string path, string xml)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using (var s = entry.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                w.Write(xml);
        }

        private static string ContentTypes()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                 + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                 + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                 + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                 + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                 + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"
                 + "</Types>";
        }

        private static string RootRels()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                 + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
                 + "</Relationships>";
        }

        private static string Workbook(string sheetName)
        {
            string name = Esc(string.IsNullOrEmpty(sheetName) ? "Ведомость" : sheetName);
            // Имя листа в Excel ограничено 31 знаком и не терпит : \ / ? * [ ]
            if (name.Length > 31) name = name.Substring(0, 31);
            foreach (char bad in new[] { ':', '\\', '/', '?', '*', '[', ']' })
                name = name.Replace(bad, '-');

            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\""
                 + " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                 + "<sheets><sheet name=\"" + name + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets>"
                 + "</workbook>";
        }

        private static string WorkbookRels()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                 + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                 + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
                 + "</Relationships>";
        }

        private static string Styles()
        {
            // Два стиля: обычный и полужирный для шапки. Больше не требуется.
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                 + "<fonts count=\"2\">"
                 + "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>"
                 + "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>"
                 + "</fonts>"
                 + "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>"
                 + "<borders count=\"1\"><border/></borders>"
                 + "<cellStyleXfs count=\"1\"><xf/></cellStyleXfs>"
                 + "<cellXfs count=\"2\">"
                 + "<xf xfId=\"0\"/>"
                 + "<xf xfId=\"0\" fontId=\"1\" applyFont=\"1\"/>"
                 + "</cellXfs>"
                 + "</styleSheet>";
        }
    }
}
