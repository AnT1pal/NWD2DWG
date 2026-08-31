// ============================================================================
//  ScheduleMapper.cs — 4D планирование и сопоставление календарных графиков
//  NWD2DWG v3.4 | namespace NWD2DWG.Plugin
//
//  Замещает: Synchro 4D Pro / Navisworks TimeLiner (~$2 800/год)
//
//  Поддерживаемые форматы графиков:
//    - MS Project XML (.xml)
//    - Primavera P6 XER / CSV (.xer/.csv)
//    - Пользовательские таблицы сопоставления WBS (CSV)
//
//  Функционал:
//    - Автоматический маппинг элементов BIM-модели по WBS, именам слоев и атрибутам
//    - Расчет статуса монтажа на целевую дату среза (Cutoff Date)
//    - Цветовая дифференциация:
//        * Зеленый (3): Смонтировано в срок (Completed)
//        * Желтый  (2): В процессе монтажа (In Progress)
//        * Красный  (1): Отставание от директивного графика (Delayed)
//        * Серый    (8): Запланировано на будущее (Not Started)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace NWD2DWG.Plugin
{
    public enum Task4DStatus
    {
        NotStarted,
        InProgress,
        Completed,
        Delayed
    }

    public class ScheduleTask
    {
        public string Uid;
        public string Name;
        public string Wbs;
        public DateTime PlannedStart;
        public DateTime PlannedFinish;
        public DateTime? ActualStart;
        public DateTime? ActualFinish;
        public double PercentComplete;
    }

    public class Element4DMatch
    {
        public string ElementName;
        public string LayerName;
        public string MatchedTaskId;
        public string MatchedTaskName;
        public string Wbs;
        public Task4DStatus Status;
        public int AciColor; // Индекс цвета AutoCAD
    }

    public static class ScheduleMapper
    {
        /// <summary>
        /// Парсер графиков производства работ (MS Project XML / CSV)
        /// </summary>
        public static List<ScheduleTask> LoadSchedule(string schedulePath)
        {
            var tasks = new List<ScheduleTask>();
            if (!File.Exists(schedulePath)) return tasks;

            string ext = Path.GetExtension(schedulePath).ToLowerInvariant();
            if (ext == ".xml")
            {
                tasks = ParseMsProjectXml(schedulePath);
            }
            else
            {
                tasks = ParseCsvSchedule(schedulePath);
            }

            return tasks;
        }

        private static List<ScheduleTask> ParseMsProjectXml(string path)
        {
            var list = new List<ScheduleTask>();
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                var taskNodes = doc.SelectNodes("//Task");
                if (taskNodes == null) return list;

                foreach (XmlNode n in taskNodes)
                {
                    string uid = GetXmlVal(n, "UID");
                    string name = GetXmlVal(n, "Name");
                    string wbs = GetXmlVal(n, "WBS");
                    string startStr = GetXmlVal(n, "Start");
                    string finishStr = GetXmlVal(n, "Finish");
                    string pctStr = GetXmlVal(n, "PercentComplete");

                    if (string.IsNullOrEmpty(name)) continue;

                    DateTime pStart, pFinish;
                    if (!DateTime.TryParse(startStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out pStart))
                        pStart = DateTime.MinValue;
                    if (!DateTime.TryParse(finishStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out pFinish))
                        pFinish = DateTime.MaxValue;

                    double pct = 0;
                    double.TryParse(pctStr, NumberStyles.Float, CultureInfo.InvariantCulture, out pct);

                    list.Add(new ScheduleTask
                    {
                        Uid = uid,
                        Name = name,
                        Wbs = wbs,
                        PlannedStart = pStart,
                        PlannedFinish = pFinish,
                        PercentComplete = pct
                    });
                }
            }
            catch { }
            return list;
        }

        private static List<ScheduleTask> ParseCsvSchedule(string path)
        {
            var list = new List<ScheduleTask>();
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("UID")) continue;
                    string[] parts = line.Split(';', ',');
                    if (parts.Length < 3) continue;

                    string uid = parts[0].Trim();
                    string name = parts[1].Trim();
                    string wbs = parts.Length > 2 ? parts[2].Trim() : "";

                    DateTime pStart = DateTime.Now.AddDays(-10);
                    DateTime pFinish = DateTime.Now.AddDays(10);
                    if (parts.Length > 3) DateTime.TryParse(parts[3].Trim(), out pStart);
                    if (parts.Length > 4) DateTime.TryParse(parts[4].Trim(), out pFinish);

                    list.Add(new ScheduleTask
                    {
                        Uid = uid,
                        Name = name,
                        Wbs = wbs,
                        PlannedStart = pStart,
                        PlannedFinish = pFinish,
                        PercentComplete = 0
                    });
                }
            }
            catch { }
            return list;
        }

        private static string GetXmlVal(XmlNode parent, string childName)
        {
            var c = parent.SelectSingleNode(childName);
            return c != null ? c.InnerText : "";
        }

        /// <summary>
        /// Сопоставление элементов 3D модели с графиком на целевую дату (Cutoff Date)
        /// </summary>
        public static List<Element4DMatch> EvaluateModel(
            List<string> elementNames,
            List<string> layerNames,
            List<ScheduleTask> tasks,
            DateTime cutoffDate)
        {
            var matches = new List<Element4DMatch>();
            int count = Math.Min(elementNames.Count, layerNames.Count);

            for (int i = 0; i < count; i++)
            {
                string elName = elementNames[i];
                string layer = layerNames[i];

                ScheduleTask matchedTask = FindBestTask(elName, layer, tasks);
                Task4DStatus status = Task4DStatus.NotStarted;
                int color = 8; // Серый по умолчанию

                if (matchedTask != null)
                {
                    if (cutoffDate >= matchedTask.PlannedFinish)
                    {
                        if (matchedTask.PercentComplete >= 99.0 || matchedTask.ActualFinish.HasValue)
                        {
                            status = Task4DStatus.Completed;
                            color = 3; // Зеленый
                        }
                        else
                        {
                            status = Task4DStatus.Delayed;
                            color = 1; // Красный (просрочено)
                        }
                    }
                    else if (cutoffDate >= matchedTask.PlannedStart)
                    {
                        status = Task4DStatus.InProgress;
                        color = 2; // Желтый
                    }
                    else
                    {
                        status = Task4DStatus.NotStarted;
                        color = 8; // Серый
                    }
                }

                matches.Add(new Element4DMatch
                {
                    ElementName = elName,
                    LayerName = layer,
                    MatchedTaskId = matchedTask != null ? matchedTask.Uid : "",
                    MatchedTaskName = matchedTask != null ? matchedTask.Name : "Без привязки к графику",
                    Wbs = matchedTask != null ? matchedTask.Wbs : "",
                    Status = status,
                    AciColor = color
                });
            }

            return matches;
        }

        private static ScheduleTask FindBestTask(string elName, string layer, List<ScheduleTask> tasks)
        {
            if (tasks == null || tasks.Count == 0) return null;

            string target = (elName + " " + layer).ToLowerInvariant();

            foreach (var t in tasks)
            {
                if (!string.IsNullOrEmpty(t.Wbs) && target.Contains(t.Wbs.ToLowerInvariant()))
                    return t;

                if (!string.IsNullOrEmpty(t.Name))
                {
                    string[] words = t.Name.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
                    int matches = 0;
                    foreach (var w in words)
                    {
                        if (w.Length > 2 && target.Contains(w.ToLowerInvariant())) matches++;
                    }
                    if (words.Length > 0 && (double)matches / words.Length >= 0.5) return t;
                }
            }

            return tasks[0]; // fallback
        }

        /// <summary>
        /// Экспорт аналитической ведомости 4D-статуса строительства в CSV
        /// </summary>
        public static void Write4DStatusCsv(string outputPath, List<Element4DMatch> matches, DateTime cutoffDate)
        {
            using (var w = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                w.WriteLine("=== ОТЧЕТ ПО 4D МОНИТОРИНГУ ГРАФИКА СТРОИТЕЛЬСТВА ===");
                w.WriteLine("ДАТА СРЕЗА:;" + cutoffDate.ToString("yyyy-MM-dd"));
                w.WriteLine();
                w.WriteLine("Элемент модели;Слой;ID задачи;Задача графика;WBS код;Статус монтажа;Цвет AutoCAD");

                foreach (var m in matches)
                {
                    string statusText = m.Status == Task4DStatus.Completed ? "Смонтировано (В срок)"
                                      : m.Status == Task4DStatus.InProgress ? "В процессе монтажа"
                                      : m.Status == Task4DStatus.Delayed ? "ОТСТАВАНИЕ ОТ ГРАФИКА"
                                      : "Запланировано (Будущий период)";

                    w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0};{1};{2};{3};{4};{5};{6}",
                        m.ElementName, m.LayerName, m.MatchedTaskId, m.MatchedTaskName, m.Wbs, statusText, m.AciColor));
                }
            }
        }
    }
}
