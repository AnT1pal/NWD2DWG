using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NWD2DWG.Plugin
{
    public class IfcMeshData
    {
        public string Name;
        public string Layer;
        public List<double> Verts;
        public List<int> Indices;
        public int Rgb; // -1 for default
        public Dictionary<string, string> Properties;
    }

    public static class IfcGuid
    {
        private static readonly char[] base64Chars = 
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$".ToCharArray();

        public static string New()
        {
            var random = new Random(Guid.NewGuid().GetHashCode());
            char[] res = new char[22];
            for (int i = 0; i < 22; i++)
            {
                res[i] = base64Chars[random.Next(64)];
            }
            return new string(res);
        }
    }

    public class IfcWriter
    {
        private string _outputPath;
        private int _entityCounter = 1;
        private StringBuilder _sb;
        private int _ownerHistoryId;
        private int _worldCoordinateSystemId;
        private int _projectPlacementId;
        private int _buildingPlacementId;
        private int _storeyPlacementId;
        private int _storeyId;
        
        private List<string> _elements = new List<string>();

        public IfcWriter(string outputPath)
        {
            _outputPath = outputPath;
            _sb = new StringBuilder();
            InitBaseEntities();
        }

        private int GetNextId()
        {
            return _entityCounter++;
        }

        private void AppendEntity(int id, string type, string args)
        {
            _sb.AppendFormat("#{0}={1}({2});\r\n", id, type, args);
        }

        private void InitBaseEntities()
        {
            // Организация и история (Organization and History)
            int personId = GetNextId();
            AppendEntity(personId, "IFCPERSON", "$,'Unknown',$,$,$,$,$,$");
            
            int orgId = GetNextId();
            AppendEntity(orgId, "IFCORGANIZATION", "$,'BaidurovLabs',$,$,$");
            
            int personAndOrgId = GetNextId();
            AppendEntity(personAndOrgId, "IFCPERSONANDORGANIZATION", $"#{personId},#{orgId},$");
            
            int appOrgId = GetNextId();
            AppendEntity(appOrgId, "IFCORGANIZATION", "$,'NWD2DWG',$,$,$");
            
            int appId = GetNextId();
            AppendEntity(appId, "IFCAPPLICATION", $"#{appOrgId},'2.0','NWD2DWG','NWD2DWG'");
            
            _ownerHistoryId = GetNextId();
            AppendEntity(_ownerHistoryId, "IFCOWNERHISTORY", $"#{personAndOrgId},#{appId},$,.ADDED.,$,#{personAndOrgId},#{appId},1234567890");

            // Координаты и размещение (Coordinates and Placement)
            int originId = GetNextId();
            AppendEntity(originId, "IFCCARTESIANPOINT", "(0.0,0.0,0.0)");
            
            int axisZId = GetNextId();
            AppendEntity(axisZId, "IFCDIRECTION", "(0.0,0.0,1.0)");
            
            int axisXId = GetNextId();
            AppendEntity(axisXId, "IFCDIRECTION", "(1.0,0.0,0.0)");
            
            _worldCoordinateSystemId = GetNextId();
            AppendEntity(_worldCoordinateSystemId, "IFCAXIS2PLACEMENT3D", $"#{originId},#{axisZId},#{axisXId}");

            _projectPlacementId = GetNextId();
            AppendEntity(_projectPlacementId, "IFCLOCALPLACEMENT", $"$,#{_worldCoordinateSystemId}");

            // Единицы измерения (Units - MM)
            int lengthUnitId = GetNextId();
            AppendEntity(lengthUnitId, "IFCSIUNIT", "*,.LENGTHUNIT.,.MILLI.,.METRE.");
            
            int angleUnitId = GetNextId();
            AppendEntity(angleUnitId, "IFCSIUNIT", "*,.PLANEANGLEUNIT.,$,.RADIAN.");
            
            int unitAssigId = GetNextId();
            AppendEntity(unitAssigId, "IFCUNITASSIGNMENT", $"(#{lengthUnitId},#{angleUnitId})");

            // Иерархия: Проект -> Участок -> Здание -> Этаж
            // (Hierarchy: Project -> Site -> Building -> Storey)
            int projectId = GetNextId();
            AppendEntity(projectId, "IFCPROJECT", $"'{IfcGuid.New()}',#{_ownerHistoryId},'NWD2DWG Export',$,$,$,$,(#{_projectPlacementId}),#{unitAssigId}");

            _buildingPlacementId = GetNextId();
            AppendEntity(_buildingPlacementId, "IFCLOCALPLACEMENT", $"#{_projectPlacementId},#{_worldCoordinateSystemId}");
            
            int siteId = GetNextId();
            AppendEntity(siteId, "IFCSITE", $"'{IfcGuid.New()}',#{_ownerHistoryId},'Default Site',$,$,#{_buildingPlacementId},$,$,.ELEMENT.,$,$,$,$,$");

            _storeyPlacementId = GetNextId();
            AppendEntity(_storeyPlacementId, "IFCLOCALPLACEMENT", $"#{_buildingPlacementId},#{_worldCoordinateSystemId}");

            int buildingId = GetNextId();
            AppendEntity(buildingId, "IFCBUILDING", $"'{IfcGuid.New()}',#{_ownerHistoryId},'Default Building',$,$,#{_storeyPlacementId},$,$,.ELEMENT.,$,$,$");

            _storeyId = GetNextId();
            AppendEntity(_storeyId, "IFCBUILDINGSTOREY", $"'{IfcGuid.New()}',#{_ownerHistoryId},'Default Storey',$,$,#{_storeyPlacementId},$,$,.ELEMENT.,0.0");

            // Связи иерархии (Hierarchy Relationships)
            int relProjSiteId = GetNextId();
            AppendEntity(relProjSiteId, "IFCRELAGGREGATES", $"'{IfcGuid.New()}',#{_ownerHistoryId},'ProjectContainer',$,#{projectId},(#{siteId})");

            int relSiteBuildingId = GetNextId();
            AppendEntity(relSiteBuildingId, "IFCRELAGGREGATES", $"'{IfcGuid.New()}',#{_ownerHistoryId},'SiteContainer',$,#{siteId},(#{buildingId})");

            int relBuildingStoreyId = GetNextId();
            AppendEntity(relBuildingStoreyId, "IFCRELAGGREGATES", $"'{IfcGuid.New()}',#{_ownerHistoryId},'BuildingContainer',$,#{buildingId},(#{_storeyId})");
        }

        private string FormatReal(double value)
        {
            string s = value.ToString("0.0####", CultureInfo.InvariantCulture);
            if (!s.Contains("."))
                s += ".0";
            return s;
        }

        public void AddElement(IfcMeshData element)
        {
            if (element.Verts == null || element.Verts.Count < 9 || element.Indices == null || element.Indices.Count < 3)
                return; // Пропуск пустых сеток (Skip empty meshes)

            // Конвертация вершин (Convert vertices)
            int[] pointIds = new int[element.Verts.Count / 3];
            for (int i = 0; i < element.Verts.Count; i += 3)
            {
                int pointId = GetNextId();
                AppendEntity(pointId, "IFCCARTESIANPOINT", 
                    $"({FormatReal(element.Verts[i])},{FormatReal(element.Verts[i + 1])},{FormatReal(element.Verts[i + 2])})");
                pointIds[i / 3] = pointId;
            }

            // Создание граней (Create faces)
            List<int> faceIds = new List<int>();
            
            // Если индексы идут по 4 (quads), шаг 4. Иначе 3.
            int step = 4;
            if (element.Indices.Count % 4 != 0 && element.Indices.Count % 3 == 0) 
            {
                step = 3;
            }
            
            for (int i = 0; i < element.Indices.Count; i += step)
            {
                if (i + 2 >= element.Indices.Count) break;

                int i1 = element.Indices[i];
                int i2 = element.Indices[i + 1];
                int i3 = element.Indices[i + 2];

                if (i1 >= pointIds.Length || i2 >= pointIds.Length || i3 >= pointIds.Length)
                    continue;

                if (i1 != i2 && i2 != i3 && i1 != i3)
                {
                    int polyLoopId1 = GetNextId();
                    AppendEntity(polyLoopId1, "IFCPOLYLOOP", $"(#{pointIds[i1]},#{pointIds[i2]},#{pointIds[i3]})");
                    int boundId1 = GetNextId();
                    AppendEntity(boundId1, "IFCFACEOUTERBOUND", $"#{polyLoopId1},.T.");
                    int faceId1 = GetNextId();
                    AppendEntity(faceId1, "IFCFACE", $"(#{boundId1})");
                    faceIds.Add(faceId1);
                }

                // Если это квадрат (4 индекса на грань) и 4-й не равен 3-му
                if (step == 4 && i + 3 < element.Indices.Count)
                {
                    int i4 = element.Indices[i + 3];
                    if (i4 != i3 && i4 < pointIds.Length) // 4-я вершина (4th index is not a duplicate)
                    {
                        if (i1 != i3 && i3 != i4 && i1 != i4)
                        {
                            int polyLoopId2 = GetNextId();
                            AppendEntity(polyLoopId2, "IFCPOLYLOOP", $"(#{pointIds[i1]},#{pointIds[i3]},#{pointIds[i4]})");
                            int boundId2 = GetNextId();
                            AppendEntity(boundId2, "IFCFACEOUTERBOUND", $"#{polyLoopId2},.T.");
                            int faceId2 = GetNextId();
                            AppendEntity(faceId2, "IFCFACE", $"(#{boundId2})");
                            faceIds.Add(faceId2);
                        }
                    }
                }
            }

            if (faceIds.Count == 0)
                return;

            // Оболочка и геометрия (Shell and Geometry)
            int shellId = GetNextId();
            string faceRefs = string.Join(",", faceIds.Select(id => "#" + id));
            AppendEntity(shellId, "IFCCLOSEDSHELL", $"({faceRefs})");

            int brepId = GetNextId();
            AppendEntity(brepId, "IFCFACETEDBREP", $"#{shellId}");

            int shapeRepId = GetNextId();
            AppendEntity(shapeRepId, "IFCSHAPEREPRESENTATION", $"#{_worldCoordinateSystemId},'Body','Brep',(#{brepId})");

            int prodDefShapeId = GetNextId();
            AppendEntity(prodDefShapeId, "IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{shapeRepId})");

            // Элемент Proxy (Proxy Element)
            int elementPlacementId = GetNextId();
            AppendEntity(elementPlacementId, "IFCLOCALPLACEMENT", $"#{_storeyPlacementId},#{_worldCoordinateSystemId}");

            string elName = string.IsNullOrEmpty(element.Name) ? "Geometry" : EscapeIfcString(element.Name);

            int proxyId = GetNextId();
            AppendEntity(proxyId, "IFCBUILDINGELEMENTPROXY", 
                $"'{IfcGuid.New()}',#{_ownerHistoryId},'{elName}',$,$,#{elementPlacementId},#{prodDefShapeId},$,$");
            
            _elements.Add($"#{proxyId}");

            // Цвет (Color material)
            if (element.Rgb != -1)
            {
                double r = ((element.Rgb >> 16) & 0xFF) / 255.0;
                double g = ((element.Rgb >> 8) & 0xFF) / 255.0;
                double b = (element.Rgb & 0xFF) / 255.0;

                int colorId = GetNextId();
                AppendEntity(colorId, "IFCCOLOURRGB", $"$,{FormatReal(r)},{FormatReal(g)},{FormatReal(b)}");

                int surfaceStyleId = GetNextId();
                AppendEntity(surfaceStyleId, "IFCSURFACESTYLERENDERING", $"#{colorId},0.,$,$,$,$,$,$,.NOTDEFINED.");

                int styleId = GetNextId();
                AppendEntity(styleId, "IFCSURFACESTYLE", $"$,.BOTH.,(#{surfaceStyleId})");

                int presentationStyleId = GetNextId();
                AppendEntity(presentationStyleId, "IFCPRESENTATIONSTYLEASSIGNMENT", $"(#{styleId})");

                int styledItemId = GetNextId();
                AppendEntity(styledItemId, "IFCSTYLEDITEM", $"#{brepId},(#{presentationStyleId}),$");
            }

            // Свойства (Properties)
            if (element.Properties != null && element.Properties.Count > 0)
            {
                List<int> propIds = new List<int>();
                foreach (var kvp in element.Properties)
                {
                    int pId = GetNextId();
                    AppendEntity(pId, "IFCPROPERTYSINGLEVALUE", $"'{EscapeIfcString(kvp.Key)}',$,IFCTEXT('{EscapeIfcString(kvp.Value)}'),$");
                    propIds.Add(pId);
                }

                int pSetId = GetNextId();
                AppendEntity(pSetId, "IFCPROPERTYSET", $"'{IfcGuid.New()}',#{_ownerHistoryId},'CustomProperties',$,({string.Join(",", propIds.Select(id => "#" + id))})");

                int relDefByPropId = GetNextId();
                AppendEntity(relDefByPropId, "IFCRELDEFINESBYPROPERTIES", $"'{IfcGuid.New()}',#{_ownerHistoryId},$,$,(#{proxyId}),#{pSetId}");
            }
        }

        private string EscapeIfcString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("'", "''");
        }

        public void Write()
        {
            if (_elements.Count > 0)
            {
                // Связываем элементы с этажом (Relate elements to storey)
                int relStoreyElementsId = GetNextId();
                string elementsList = string.Join(",", _elements);
                AppendEntity(relStoreyElementsId, "IFCRELCONTAINEDINSPATIALSTRUCTURE", 
                    $"'{IfcGuid.New()}',#{_ownerHistoryId},'StoreyElements',$,({elementsList}),#{_storeyId}");
            }

            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");

            using (StreamWriter writer = new StreamWriter(_outputPath, false, new UTF8Encoding(false)))
            {
                writer.Write("ISO-10303-21;\r\n");
                writer.Write("HEADER;\r\n");
                writer.Write("FILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');\r\n");
                writer.Write($"FILE_NAME('{Path.GetFileName(_outputPath)}','{timestamp}',('NWD2DWG'),('BaidurovLabs'),'NWD2DWG v2.0','NWD2DWG','');\r\n");
                writer.Write("FILE_SCHEMA(('IFC2X3'));\r\n");
                writer.Write("ENDSEC;\r\n");
                writer.Write("DATA;\r\n");
                
                writer.Write(_sb.ToString());
                
                writer.Write("ENDSEC;\r\n");
                writer.Write("END-ISO-10303-21;\r\n");
            }
        }
    }
}
