using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NWD2DWG.Plugin
{
    public class GltfMeshData
    {
        public string Name;
        public List<double> Verts;
        public List<int> Indices;
        public int Rgb = -1;
        public double Transparency;
    }

    public class GltfWriter
    {
        private string _outputPath;
        private List<GltfMeshData> _meshes;
        private bool _isGlb;

        public GltfWriter(string outputPath)
        {
            _outputPath = outputPath;
            _meshes = new List<GltfMeshData>();
            _isGlb = outputPath.ToLowerInvariant().EndsWith(".glb");
        }

        public void AddMesh(GltfMeshData mesh)
        {
            _meshes.Add(mesh);
        }

        public void Write()
        {
            using (var memoryStream = new MemoryStream())
            using (var binWriter = new BinaryWriter(memoryStream))
            {
                // Список для хранения информации о буферах
                var bufferViews = new List<string>();
                var accessors = new List<string>();
                var meshesJson = new List<string>();
                var nodesJson = new List<string>();
                var materialsJson = new List<string>();

                int bufferOffset = 0;
                int accessorIndex = 0;
                int materialIndex = 0;
                int nodeIndex = 1; // 0 is root node

                nodesJson.Add("{\"children\":[" + string.Join(",", GetChildIndices()) + "],\"name\":\"RootNode\"}");

                foreach (var mesh in _meshes)
                {
                    // Обработка вершин (POSITION)
                    float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                    float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

                    int vertexCount = mesh.Verts.Count / 3;
                    byte[] vertexBytes = new byte[vertexCount * 12];
                    for (int i = 0; i < vertexCount; i++)
                    {
                        float x = (float)mesh.Verts[i * 3];
                        float y = (float)mesh.Verts[i * 3 + 1];
                        float z = (float)mesh.Verts[i * 3 + 2];

                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (z < minZ) minZ = z;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                        if (z > maxZ) maxZ = z;

                        Buffer.BlockCopy(BitConverter.GetBytes(x), 0, vertexBytes, i * 12, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(y), 0, vertexBytes, i * 12 + 4, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(z), 0, vertexBytes, i * 12 + 8, 4);
                    }

                    int vertexOffset = bufferOffset;
                    int vertexLength = vertexBytes.Length;
                    binWriter.Write(vertexBytes);
                    bufferOffset += vertexLength;

                    // Обработка индексов
                    int indexCount = mesh.Indices.Count;
                    byte[] indexBytes = new byte[indexCount * 4];
                    uint minIndex = uint.MaxValue;
                    uint maxIndex = uint.MinValue;

                    for (int i = 0; i < indexCount; i++)
                    {
                        uint idx = (uint)mesh.Indices[i];
                        if (idx < minIndex) minIndex = idx;
                        if (idx > maxIndex) maxIndex = idx;
                        Buffer.BlockCopy(BitConverter.GetBytes(idx), 0, indexBytes, i * 4, 4);
                    }

                    int indexOffset = bufferOffset;
                    int indexLength = indexBytes.Length;
                    binWriter.Write(indexBytes);
                    bufferOffset += indexLength;

                    // Создание JSON для BufferViews
                    bufferViews.Add($"{{\"buffer\":0,\"byteOffset\":{vertexOffset},\"byteLength\":{vertexLength},\"target\":34922}}"); // ARRAY_BUFFER
                    bufferViews.Add($"{{\"buffer\":0,\"byteOffset\":{indexOffset},\"byteLength\":{indexLength},\"target\":34963}}"); // ELEMENT_ARRAY_BUFFER

                    int vertexBufferViewIndex = accessorIndex * 2;
                    int indexBufferViewIndex = accessorIndex * 2 + 1;

                    // Создание JSON для Accessors
                    string f(float val) => val.ToString(CultureInfo.InvariantCulture);
                    accessors.Add($"{{\"bufferView\":{vertexBufferViewIndex},\"componentType\":5126,\"count\":{vertexCount},\"type\":\"VEC3\",\"max\":[{f(maxX)},{f(maxY)},{f(maxZ)}],\"min\":[{f(minX)},{f(minY)},{f(minZ)}]}}");
                    accessors.Add($"{{\"bufferView\":{indexBufferViewIndex},\"componentType\":5125,\"count\":{indexCount},\"type\":\"SCALAR\",\"max\":[{maxIndex}],\"min\":[{minIndex}]}}");

                    int positionAccessorIndex = accessorIndex * 2;
                    int indexAccessorIndex = accessorIndex * 2 + 1;

                    // Материал
                    float r = 0.8f, g = 0.8f, b = 0.8f;
                    if (mesh.Rgb != -1)
                    {
                        r = ((mesh.Rgb >> 16) & 0xFF) / 255.0f;
                        g = ((mesh.Rgb >> 8) & 0xFF) / 255.0f;
                        b = (mesh.Rgb & 0xFF) / 255.0f;
                    }
                    float a = 1.0f - (float)mesh.Transparency;
                    string alphaMode = mesh.Transparency > 0.0 ? "\"BLEND\"" : "\"OPAQUE\"";

                    materialsJson.Add($"{{\"pbrMetallicRoughness\":{{\"baseColorFactor\":[{f(r)},{f(g)},{f(b)},{f(a)}],\"metallicFactor\":0.0,\"roughnessFactor\":0.5}},\"alphaMode\":{alphaMode},\"doubleSided\":true}}");

                    // Меш
                    string meshName = mesh.Name != null ? EscapeJsonString(mesh.Name) : "Mesh_" + materialIndex;
                    meshesJson.Add($"{{\"name\":\"{meshName}\",\"primitives\":[{{\"attributes\":{{\"POSITION\":{positionAccessorIndex}}},\"indices\":{indexAccessorIndex},\"material\":{materialIndex}}}]}}");

                    // Узел
                    nodesJson.Add($"{{\"mesh\":{materialIndex},\"name\":\"{meshName}\"}}");

                    accessorIndex++;
                    materialIndex++;
                    nodeIndex++;
                }

                byte[] binBuffer = memoryStream.ToArray();
                int binLength = binBuffer.Length;

                // Выравнивание бинарного буфера до 4 байт для GLB
                if (_isGlb)
                {
                    int padding = (4 - (binLength % 4)) % 4;
                    if (padding > 0)
                    {
                        Array.Resize(ref binBuffer, binLength + padding);
                        binLength += padding;
                    }
                }

                string binUri = "";
                if (!_isGlb)
                {
                    string binFileName = Path.GetFileNameWithoutExtension(_outputPath) + ".bin";
                    binUri = $",\"uri\":\"{binFileName}\"";
                    string binFilePath = Path.Combine(Path.GetDirectoryName(_outputPath), binFileName);
                    File.WriteAllBytes(binFilePath, binBuffer);
                }

                // Сборка JSON
                var sb = new StringBuilder();
                sb.Append("{");
                sb.Append("\"asset\":{\"version\":\"2.0\",\"generator\":\"NWD2DWG\"},");
                sb.Append("\"scene\":0,");
                // Корень сцены — единственная нода 0 (RootNode), её children
                // перечисляют все меш-ноды. По спецификации glTF ноды образуют
                // непересекающиеся деревья, поэтому дочерние ноды НЕ должны
                // одновременно перечисляться как корни сцены.
                sb.Append("\"scenes\":[{\"nodes\":[0]}],");
                sb.Append("\"nodes\":[" + string.Join(",", nodesJson) + "],");
                sb.Append("\"materials\":[" + string.Join(",", materialsJson) + "],");
                sb.Append("\"meshes\":[" + string.Join(",", meshesJson) + "],");
                sb.Append("\"accessors\":[" + string.Join(",", accessors) + "],");
                sb.Append("\"bufferViews\":[" + string.Join(",", bufferViews) + "],");
                sb.Append($"\"buffers\":[{{\"byteLength\":{bufferOffset}{binUri}}}]");
                sb.Append("}");

                string jsonString = sb.ToString();

                if (_isGlb)
                {
                    // Формирование файла GLB
                    byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonString);
                    int jsonPadding = (4 - (jsonBytes.Length % 4)) % 4;
                    byte[] paddedJsonBytes = new byte[jsonBytes.Length + jsonPadding];
                    Buffer.BlockCopy(jsonBytes, 0, paddedJsonBytes, 0, jsonBytes.Length);
                    for (int i = 0; i < jsonPadding; i++)
                    {
                        paddedJsonBytes[jsonBytes.Length + i] = 0x20; // Пробел
                    }

                    int totalLength = 12 + 8 + paddedJsonBytes.Length + 8 + binBuffer.Length;

                    using (var fs = new FileStream(_outputPath, FileMode.Create, FileAccess.Write))
                    using (var writer = new BinaryWriter(fs))
                    {
                        // Заголовок
                        writer.Write((uint)0x46546C67); // magic
                        writer.Write((uint)2);          // version
                        writer.Write((uint)totalLength);

                        // Чанк 0 (JSON)
                        writer.Write((uint)paddedJsonBytes.Length);
                        writer.Write((uint)0x4E4F534A); // JSON
                        writer.Write(paddedJsonBytes);

                        // Чанк 1 (BIN)
                        writer.Write((uint)binBuffer.Length);
                        writer.Write((uint)0x004E4942); // BIN
                        writer.Write(binBuffer);
                    }
                }
                else
                {
                    // Encoding.UTF8 добавляет BOM, а JSON с BOM отвергают
                    // glTF-Validator, three.js GLTFLoader и Blender-импортёр
                    File.WriteAllText(_outputPath, jsonString, new UTF8Encoding(false));
                }
            }
        }

        private IEnumerable<string> GetChildIndices()
        {
            for (int i = 1; i <= _meshes.Count; i++)
            {
                yield return i.ToString(CultureInfo.InvariantCulture);
            }
        }

        private string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                        {
                            sb.Append($"\\u{(int)c:x4}");
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
