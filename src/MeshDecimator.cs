using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NWD2DWG.Plugin
{
    public static class MeshDecimator
    {
        private class SymmetricMatrix
        {
            public double m11, m12, m13, m14;
            public double m22, m23, m24;
            public double m33, m34;
            public double m44;

            public SymmetricMatrix() { }

            public SymmetricMatrix(double a, double b, double c, double d)
            {
                m11 = a * a; m12 = a * b; m13 = a * c; m14 = a * d;
                m22 = b * b; m23 = b * c; m24 = b * d;
                m33 = c * c; m34 = c * d;
                m44 = d * d;
            }

            public SymmetricMatrix Scaled(double k)
            {
                var r = new SymmetricMatrix();
                r.m11 = m11 * k; r.m12 = m12 * k; r.m13 = m13 * k; r.m14 = m14 * k;
                r.m22 = m22 * k; r.m23 = m23 * k; r.m24 = m24 * k;
                r.m33 = m33 * k; r.m34 = m34 * k;
                r.m44 = m44 * k;
                return r;
            }

            public void Add(SymmetricMatrix n)
            {
                m11 += n.m11; m12 += n.m12; m13 += n.m13; m14 += n.m14;
                m22 += n.m22; m23 += n.m23; m24 += n.m24;
                m33 += n.m33; m34 += n.m34;
                m44 += n.m44;
            }

            public static SymmetricMatrix operator +(SymmetricMatrix n1, SymmetricMatrix n2)
            {
                SymmetricMatrix res = new SymmetricMatrix();
                res.m11 = n1.m11 + n2.m11; res.m12 = n1.m12 + n2.m12; res.m13 = n1.m13 + n2.m13; res.m14 = n1.m14 + n2.m14;
                res.m22 = n1.m22 + n2.m22; res.m23 = n1.m23 + n2.m23; res.m24 = n1.m24 + n2.m24;
                res.m33 = n1.m33 + n2.m33; res.m34 = n1.m34 + n2.m34;
                res.m44 = n1.m44 + n2.m44;
                return res;
            }
        }

        private class Vertex
        {
            public int id;
            public double x, y, z;
            public SymmetricMatrix q;
            public bool removed;
            public HashSet<int> adjacentVertices;
            public HashSet<int> faces;

            public Vertex(int id, double x, double y, double z)
            {
                this.id = id;
                this.x = x; this.y = y; this.z = z;
                this.q = new SymmetricMatrix();
                this.removed = false;
                this.adjacentVertices = new HashSet<int>();
                this.faces = new HashSet<int>();
            }
        }

        private class Face
        {
            public int id;
            public int v0, v1, v2;
            public double[] normal;
            public bool removed;

            public Face(int id, int v0, int v1, int v2)
            {
                this.id = id;
                this.v0 = v0; this.v1 = v1; this.v2 = v2;
                this.normal = new double[3];
                this.removed = false;
            }

            public bool HasVertex(int vId)
            {
                return v0 == vId || v1 == vId || v2 == vId;
            }
            
            public void ReplaceVertex(int oldV, int newV)
            {
                if (v0 == oldV) v0 = newV;
                else if (v1 == oldV) v1 = newV;
                else if (v2 == oldV) v2 = newV;
            }
        }

        private class Edge : IComparable<Edge>
        {
            public int v1, v2;
            public double error;
            public double tx, ty, tz;
            // версии вершин на момент расчёта ошибки: если вершина с тех пор
            // сдвинулась, запись в куче устарела и ошибку надо пересчитать
            public int ver1, ver2;

            public Edge(int v1, int v2)
            {
                this.v1 = Math.Min(v1, v2);
                this.v2 = Math.Max(v1, v2);
            }

            public int CompareTo(Edge other)
            {
                return this.error.CompareTo(other.error);
            }

            public override bool Equals(object obj)
            {
                if (obj is Edge other)
                    return v1 == other.v1 && v2 == other.v2;
                return false;
            }

            public override int GetHashCode()
            {
                return (v1 * 397) ^ v2;
            }
        }

        // Двоичная мин-куча по ошибке. Раньше рёбра лежали в отсортированном
        // List и новые вставлялись через List.Insert — O(N) memmove на каждое
        // схлопывание, то есть O(N^2) на прогон. Плюс вставка перед курсором
        // сдвигала список под ним и ломала порядок обхода.
        private class EdgeHeap
        {
            private readonly List<Edge> _h;
            public EdgeHeap(int capacity) { _h = new List<Edge>(capacity); }
            public int Count { get { return _h.Count; } }

            public void Push(Edge e)
            {
                _h.Add(e);
                int i = _h.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_h[p].error <= _h[i].error) break;
                    Edge tmp = _h[p]; _h[p] = _h[i]; _h[i] = tmp;
                    i = p;
                }
            }

            public Edge Pop()
            {
                if (_h.Count == 0) return null;
                Edge top = _h[0];
                Edge last = _h[_h.Count - 1];
                _h.RemoveAt(_h.Count - 1);
                if (_h.Count > 0)
                {
                    _h[0] = last;
                    int i = 0;
                    while (true)
                    {
                        int l = 2 * i + 1, r = l + 1, s = i;
                        if (l < _h.Count && _h[l].error < _h[s].error) s = l;
                        if (r < _h.Count && _h[r].error < _h[s].error) s = r;
                        if (s == i) break;
                        Edge tmp = _h[s]; _h[s] = _h[i]; _h[i] = tmp;
                        i = s;
                    }
                }
                return top;
            }
        }

        private static void CountEdge(Dictionary<long, int> map, int a, int b)
        {
            long k = EdgeKey(a, b);
            int c;
            map[k] = map.TryGetValue(k, out c) ? c + 1 : 1;
        }

        private static long EdgeKey(int a, int b)
        {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }

        // Для ребра, принадлежащего ровно одной грани, добавляем квадрику
        // плоскости, перпендикулярной грани и содержащей это ребро.
        private static void AddBoundaryQuadric(List<Vertex> vertices,
            Dictionary<long, int> edgeFaces, Face f, int a, int b, double weight)
        {
            int cnt;
            if (!edgeFaces.TryGetValue(EdgeKey(a, b), out cnt) || cnt != 1) return;

            Vertex va = vertices[a], vb = vertices[b];
            double ex = vb.x - va.x, ey = vb.y - va.y, ez = vb.z - va.z;
            double el = Math.Sqrt(ex * ex + ey * ey + ez * ez);
            if (el < 1e-12) return;
            ex /= el; ey /= el; ez /= el;

            // n = edge x faceNormal
            double nx = ey * f.normal[2] - ez * f.normal[1];
            double ny = ez * f.normal[0] - ex * f.normal[2];
            double nz = ex * f.normal[1] - ey * f.normal[0];
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-12) return;
            nx /= nl; ny /= nl; nz /= nl;

            double d = -(nx * va.x + ny * va.y + nz * va.z);
            SymmetricMatrix sm = new SymmetricMatrix(nx, ny, nz, d).Scaled(weight);
            va.q.Add(sm);
            vb.q.Add(sm);
        }

        // true, если после переноса v2 в точку (tx,ty,tz) хоть одна уцелевшая
        // грань поменяет направление нормали (складка / вывернутый треугольник)
        private static bool WouldFlip(List<Vertex> vertices, List<Face> faces,
            Vertex v1, Vertex v2, double tx, double ty, double tz)
        {
            foreach (var set in new[] { v1.faces, v2.faces })
            {
                foreach (int fId in set)
                {
                    Face f = faces[fId];
                    if (f.removed) continue;
                    // грани, содержащие оба конца, всё равно схлопнутся
                    if (f.HasVertex(v1.id) && f.HasVertex(v2.id)) continue;

                    double[] p0 = MovedPos(vertices[f.v0], v1, v2, tx, ty, tz);
                    double[] p1 = MovedPos(vertices[f.v1], v1, v2, tx, ty, tz);
                    double[] p2 = MovedPos(vertices[f.v2], v1, v2, tx, ty, tz);

                    double ux = p1[0] - p0[0], uy = p1[1] - p0[1], uz = p1[2] - p0[2];
                    double vx = p2[0] - p0[0], vy = p2[1] - p0[1], vz = p2[2] - p0[2];
                    double nx = uy * vz - uz * vy;
                    double ny = uz * vx - ux * vz;
                    double nz = ux * vy - uy * vx;
                    double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (nl < 1e-12) return true; // грань выродилась в отрезок

                    double dot = (nx * f.normal[0] + ny * f.normal[1] + nz * f.normal[2]) / nl;
                    if (dot < 0.1) return true;
                }
            }
            return false;
        }

        private static double[] MovedPos(Vertex v, Vertex v1, Vertex v2,
            double tx, double ty, double tz)
        {
            if (v.id == v1.id || v.id == v2.id) return new[] { tx, ty, tz };
            return new[] { v.x, v.y, v.z };
        }

        private static double VertexError(SymmetricMatrix q, double x, double y, double z)
        {
            return q.m11 * x * x + 2 * q.m12 * x * y + 2 * q.m13 * x * z + 2 * q.m14 * x +
                   q.m22 * y * y + 2 * q.m23 * y * z + 2 * q.m24 * y +
                   q.m33 * z * z + 2 * q.m34 * z +
                   q.m44;
        }

        private static double CalculateError(Vertex v1, Vertex v2, out double tx, out double ty, out double tz)
        {
            SymmetricMatrix q = v1.q + v2.q;

            // Вычисляем ошибку для v1, v2 и центральной точки, выбираем минимальную
            double p1x = v1.x, p1y = v1.y, p1z = v1.z;
            double p2x = v2.x, p2y = v2.y, p2z = v2.z;
            double p3x = (v1.x + v2.x) / 2.0;
            double p3y = (v1.y + v2.y) / 2.0;
            double p3z = (v1.z + v2.z) / 2.0;

            double err1 = VertexError(q, p1x, p1y, p1z);
            double err2 = VertexError(q, p2x, p2y, p2z);
            double err3 = VertexError(q, p3x, p3y, p3z);

            double minErr = Math.Min(err1, Math.Min(err2, err3));
            if (minErr == err1) { tx = p1x; ty = p1y; tz = p1z; }
            else if (minErr == err2) { tx = p2x; ty = p2y; tz = p2z; }
            else { tx = p3x; ty = p3y; tz = p3z; }

            return minErr;
        }

        public static void Decimate(ref List<double> verts, ref List<int> quads, double targetRatio)
        {
            Decimate(ref verts, ref quads, targetRatio, 1000.0, true);
        }

        public static void Decimate(ref List<double> verts, ref List<int> quads,
                                    double targetRatio, double boundaryWeight, bool preventFlips)
        {
            if (targetRatio <= 0.0 || quads.Count == 0 || verts.Count == 0) return;
            // Раньше ratio >= 1 молча стирал всю геометрию. Ограничиваем
            // максимальное сжатие вместо уничтожения меша.
            if (targetRatio > 0.95) targetRatio = 0.95;

            int initialFaceCount = quads.Count / 4;
            int targetFaceCount = (int)(initialFaceCount * (1.0 - targetRatio));
            if (targetFaceCount < 4) targetFaceCount = 4;

            // 1. Построение структур данных
            List<Vertex> vertices = new List<Vertex>(verts.Count / 3);
            for (int i = 0; i < verts.Count; i += 3)
            {
                vertices.Add(new Vertex(vertices.Count, verts[i], verts[i + 1], verts[i + 2]));
            }

            List<Face> faces = new List<Face>(initialFaceCount);
            for (int i = 0; i < quads.Count; i += 4)
            {
                int v0 = quads[i];
                int v1 = quads[i + 1];
                int v2 = quads[i + 2];
                // Игнорируем вырожденные треугольники
                if (v0 == v1 || v1 == v2 || v2 == v0) continue;

                Face f = new Face(faces.Count, v0, v1, v2);
                faces.Add(f);

                vertices[v0].faces.Add(f.id);
                vertices[v1].faces.Add(f.id);
                vertices[v2].faces.Add(f.id);

                vertices[v0].adjacentVertices.Add(v1); vertices[v0].adjacentVertices.Add(v2);
                vertices[v1].adjacentVertices.Add(v0); vertices[v1].adjacentVertices.Add(v2);
                vertices[v2].adjacentVertices.Add(v0); vertices[v2].adjacentVertices.Add(v1);
            }

            // 2. Расчет нормалей и матриц квадрик (Q)
            foreach (Face f in faces)
            {
                Vertex v0 = vertices[f.v0];
                Vertex v1 = vertices[f.v1];
                Vertex v2 = vertices[f.v2];

                double ux = v1.x - v0.x, uy = v1.y - v0.y, uz = v1.z - v0.z;
                double vx = v2.x - v0.x, vy = v2.y - v0.y, vz = v2.z - v0.z;
                
                double nx = uy * vz - uz * vy;
                double ny = uz * vx - ux * vz;
                double nz = ux * vy - uy * vx;
                
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-10)
                {
                    nx /= len; ny /= len; nz /= len;
                }

                f.normal[0] = nx; f.normal[1] = ny; f.normal[2] = nz;
                double d = -(nx * v0.x + ny * v0.y + nz * v0.z);

                SymmetricMatrix sm = new SymmetricMatrix(nx, ny, nz, d);
                v0.q.Add(sm);
                v1.q.Add(sm);
                v2.q.Add(sm);
            }

            // 2б. Квадрики граничных рёбер.
            // Navisworks отдаёт открытые оболочки, а не замкнутые тела. Без
            // закрепления границы QEM утягивает края внутрь и объект усыхает.
            var edgeFaces = new Dictionary<long, int>(faces.Count * 2);
            foreach (Face bf in faces)
            {
                CountEdge(edgeFaces, bf.v0, bf.v1);
                CountEdge(edgeFaces, bf.v1, bf.v2);
                CountEdge(edgeFaces, bf.v2, bf.v0);
            }
            if (boundaryWeight > 0)
            {
                foreach (Face bf in faces)
                {
                    AddBoundaryQuadric(vertices, edgeFaces, bf, bf.v0, bf.v1, boundaryWeight);
                    AddBoundaryQuadric(vertices, edgeFaces, bf, bf.v1, bf.v2, boundaryWeight);
                    AddBoundaryQuadric(vertices, edgeFaces, bf, bf.v2, bf.v0, boundaryWeight);
                }
            }

            // 3. Формирование кучи рёбер по возрастанию ошибки
            int[] vver = new int[vertices.Count];
            var heap = new EdgeHeap(vertices.Count * 3);
            for (int i = 0; i < vertices.Count; i++)
            {
                Vertex vi = vertices[i];
                foreach (int adjId in vi.adjacentVertices)
                {
                    if (adjId > i) // каждое ребро добавляем один раз
                    {
                        Edge e0 = new Edge(i, adjId);
                        e0.error = CalculateError(vi, vertices[adjId], out e0.tx, out e0.ty, out e0.tz);
                        e0.ver1 = vver[e0.v1]; e0.ver2 = vver[e0.v2];
                        heap.Push(e0);
                    }
                }
            }

            // 4. Схлопывание ребер (Decimation)
            int currentFaceCount = faces.Count;

            while (currentFaceCount > targetFaceCount && heap.Count > 0)
            {
                Edge e = heap.Pop();
                Vertex v1 = vertices[e.v1];
                Vertex v2 = vertices[e.v2];

                if (v1.removed || v2.removed) continue;

                // Запись устарела (одна из вершин уже двигалась) — пересчитываем
                // и возвращаем в кучу, чтобы не нарушать порядок по ошибке
                if (e.ver1 != vver[e.v1] || e.ver2 != vver[e.v2])
                {
                    e.error = CalculateError(v1, v2, out e.tx, out e.ty, out e.tz);
                    e.ver1 = vver[e.v1]; e.ver2 = vver[e.v2];
                    heap.Push(e);
                    continue;
                }

                // Защита от выворачивания: если уцелевшая грань меняет
                // направление нормали, схлопывание даёт складку
                if (preventFlips && WouldFlip(vertices, faces, v1, v2, e.tx, e.ty, e.tz)) continue;

                // Схлопываем v2 в v1
                v1.x = e.tx; v1.y = e.ty; v1.z = e.tz;
                v1.q.Add(v2.q);
                v2.removed = true;
                vver[v1.id]++;

                // Обновляем лица
                List<int> facesToRemove = new List<int>();
                foreach (int fId in v2.faces)
                {
                    Face f = faces[fId];
                    if (f.removed) continue;

                    if (f.HasVertex(v1.id))
                    {
                        // Треугольник стал вырожденным, удаляем
                        f.removed = true;
                        facesToRemove.Add(f.id);
                        currentFaceCount--;
                    }
                    else
                    {
                        // Заменяем v2 на v1
                        f.ReplaceVertex(v2.id, v1.id);
                        v1.faces.Add(f.id);
                    }
                }

                foreach (int fId in facesToRemove) v1.faces.Remove(fId);
                v2.faces.Clear();

                // Обновляем связи графа
                v1.adjacentVertices.Remove(v2.id);
                foreach (int adjId in v2.adjacentVertices)
                {
                    if (adjId != v1.id && !vertices[adjId].removed)
                    {
                        vertices[adjId].adjacentVertices.Remove(v2.id);
                        vertices[adjId].adjacentVertices.Add(v1.id);
                        v1.adjacentVertices.Add(adjId);
                        vver[adjId]++;

                        Edge newEdge = new Edge(v1.id, adjId);
                        newEdge.error = CalculateError(v1, vertices[adjId],
                            out newEdge.tx, out newEdge.ty, out newEdge.tz);
                        newEdge.ver1 = vver[newEdge.v1]; newEdge.ver2 = vver[newEdge.v2];
                        heap.Push(newEdge);
                    }
                }
                v2.adjacentVertices.Clear();
            }

            // 5. Упаковка новых массивов
            List<double> newVerts = new List<double>();
            int[] oldToNewVertex = new int[vertices.Count];
            int newIdx = 0;

            for (int i = 0; i < vertices.Count; i++)
            {
                if (!vertices[i].removed)
                {
                    newVerts.Add(vertices[i].x);
                    newVerts.Add(vertices[i].y);
                    newVerts.Add(vertices[i].z);
                    oldToNewVertex[i] = newIdx++;
                }
                else
                {
                    oldToNewVertex[i] = -1;
                }
            }

            List<int> newQuads = new List<int>();
            foreach (Face f in faces)
            {
                if (!f.removed)
                {
                    int nv0 = oldToNewVertex[f.v0];
                    int nv1 = oldToNewVertex[f.v1];
                    int nv2 = oldToNewVertex[f.v2];

                    if (nv0 >= 0 && nv1 >= 0 && nv2 >= 0 && nv0 != nv1 && nv1 != nv2 && nv2 != nv0)
                    {
                        newQuads.Add(nv0);
                        newQuads.Add(nv1);
                        newQuads.Add(nv2);
                        newQuads.Add(nv2); // дублируем для quads
                    }
                }
            }

            verts = newVerts;
            quads = newQuads;
        }
    }
}
