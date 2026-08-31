// ============================================================================
//  NWD2DWG — BcfExporter.cs
//  Модуль экспорта коллизий и точек обзора в стандартный BCF 2.1 (BIM Collaboration Format).
//
//  Разработчик: Baidurov Pavel / BaidurovLabs
//  Лицензия: GNU General Public License v3.0 (GPLv3)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace NWD2DWG.Plugin
{
    public class BcfTopic
    {
        public string Guid = System.Guid.NewGuid().ToString();
        public string Title;
        public string Description;
        public string AssignedTo;
        public string Status = "Active"; // Active, Resolved, Closed
        public DateTime CreationDate = DateTime.Now;
        public double CameraPosX, CameraPosY, CameraPosZ;
        public double CameraDirX, CameraDirY, CameraDirZ;
        public double CameraUpX, CameraUpY, CameraUpZ;
        public List<string> ComponentGuids = new List<string>();
    }

    public static class BcfExporter
    {
        private static string Author = "NWD2DWG";

        /// <summary>
        /// Создание валидного .bcfzip (BCF 2.1) архива из списка топиков/коллизий
        /// </summary>
        public static void ExportBcfZip(string bcfPath, IList<BcfTopic> topics)
        {
            ExportBcfZip(bcfPath, topics, "NWD2DWG");
        }

        public static void ExportBcfZip(string bcfPath, IList<BcfTopic> topics, string author)
        {
            Author = string.IsNullOrEmpty(author) ? "NWD2DWG" : author;
            if (topics == null || topics.Count == 0) return;
            string dir = Path.GetDirectoryName(bcfPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(bcfPath)) try { File.Delete(bcfPath); } catch { }

            using (var zipStream = new FileStream(bcfPath, FileMode.Create))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                // 1. bcf.version
                var verEntry = archive.CreateEntry("bcf.version");
                using (var sw = new StreamWriter(verEntry.Open(), Encoding.UTF8))
                {
                    sw.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                    sw.WriteLine("<Version VersionId=\"2.1\" />");
                }

                // 2. Папки для каждого топика
                foreach (var topic in topics)
                {
                    string topicFolder = topic.Guid + "/";

                    // markup.bcf
                    var markupEntry = archive.CreateEntry(topicFolder + "markup.bcf");
                    using (var sw = new StreamWriter(markupEntry.Open(), Encoding.UTF8))
                    {
                        sw.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                        sw.WriteLine("<Markup xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
                        sw.WriteLine(string.Format("  <Topic Guid=\"{0}\" TopicType=\"Clash\" TopicStatus=\"{1}\">", topic.Guid, topic.Status));
                        sw.WriteLine(string.Format("    <Title>{0}</Title>", EscapeXml(topic.Title)));
                        sw.WriteLine(string.Format("    <CreationDate>{0:O}</CreationDate>", topic.CreationDate));
                        sw.WriteLine(string.Format("    <CreationAuthor>{0}</CreationAuthor>", EscapeXml(Author)));
                        sw.WriteLine(string.Format("    <Description>{0}</Description>", EscapeXml(topic.Description)));
                        sw.WriteLine("  </Topic>");
                        sw.WriteLine(string.Format("  <Viewpoints Guid=\"{0}\">", topic.Guid));
                        sw.WriteLine(string.Format("    <Viewpoint>{0}.bcfv</Viewpoint>", topic.Guid));
                        sw.WriteLine("  </Viewpoints>");
                        sw.WriteLine("</Markup>");
                    }

                    // viewpoint.bcfv
                    var vpEntry = archive.CreateEntry(topicFolder + topic.Guid + ".bcfv");
                    using (var sw = new StreamWriter(vpEntry.Open(), Encoding.UTF8))
                    {
                        var ci = CultureInfo.InvariantCulture;
                        sw.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                        sw.WriteLine("<VisualizationInfo Guid=\"{0}\">", topic.Guid);
                        sw.WriteLine("  <PerspectiveCamera>");
                        sw.WriteLine(string.Format(ci, "    <CameraViewPoint X=\"{0:F4}\" Y=\"{1:F4}\" Z=\"{2:F4}\" />", topic.CameraPosX, topic.CameraPosY, topic.CameraPosZ));
                        sw.WriteLine(string.Format(ci, "    <CameraDirection X=\"{0:F4}\" Y=\"{1:F4}\" Z=\"{2:F4}\" />", topic.CameraDirX, topic.CameraDirY, topic.CameraDirZ));
                        sw.WriteLine(string.Format(ci, "    <CameraUpVector X=\"{0:F4}\" Y=\"{1:F4}\" Z=\"{2:F4}\" />", topic.CameraUpX, topic.CameraUpY, topic.CameraUpZ));
                        sw.WriteLine("    <FieldOfView>60</FieldOfView>");
                        sw.WriteLine("  </PerspectiveCamera>");
                        if (topic.ComponentGuids.Count > 0)
                        {
                            sw.WriteLine("  <Components>");
                            foreach (var cg in topic.ComponentGuids)
                            {
                                sw.WriteLine(string.Format("    <Component IfcGuid=\"{0}\" Selected=\"true\" />", cg));
                            }
                            sw.WriteLine("  </Components>");
                        }
                        sw.WriteLine("</VisualizationInfo>");
                    }
                }
            }
        }

        private static string EscapeXml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
        }
    }
}
