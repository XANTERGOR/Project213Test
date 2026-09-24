using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Pure editor mesh preparation. Shared rows are shaded before partitioning.
    public static class LTRoadMesh
    {
        public const int MaxPoints = LTRoadMath.MaxPoints, MaxSamples = LTRoadMath.MaxSamples, MaxChunks = 1024;

        public sealed class Chunk
        {
            public Vector3[] vertices, normals;
            public Vector4[] tangents;
            public Vector2[] uv;
            public int[] triangles;
            public string hash;
        }

        public static List<Chunk> Build(LTRoadMath.Snapshot snapshot, float chunkLength,
            float repeat, float offset, Matrix4x4 terrainToRoad)
        {
            if (snapshot == null || snapshot.samples == null || snapshot.samples.Count < 2)
                throw new InvalidOperationException("Для меша нужны хотя бы два образца дороги.");
            int rows = snapshot.samples.Count;
            if (rows > MaxSamples) throw new InvalidOperationException($"Лимит образцов: {MaxSamples}. Увеличьте шаг вдоль дороги.");
            if (!Finite(chunkLength) || chunkLength <= 0 || !Finite(repeat) || repeat <= 0 || !Finite(offset)
                || !Finite(snapshot.width) || snapshot.width <= 0)
                throw new InvalidOperationException("Ширина, длина чанка и повтор текстуры должны быть положительными конечными числами.");
            if (!Finite(terrainToRoad.determinant) || terrainToRoad.determinant <= 0.00000001f)
                throw new InvalidOperationException("Нулевой или отражённый масштаб дороги не поддерживается.");
            var vertices = new Vector3[rows * 2];
            var normals = new Vector3[vertices.Length];
            var tangents = new Vector4[vertices.Length];
            var uv = new Vector2[vertices.Length];
            float previous = -1;
            for (int i = 0; i < rows; i++)
            {
                var sample = snapshot.samples[i];
                if (!Finite(sample.position) || !Finite(sample.right) || sample.right.sqrMagnitude < .5f
                    || !Finite(sample.distance) || sample.distance < 0 || (i > 0 && sample.distance <= previous) || !Finite(sample.bank))
                    throw new InvalidOperationException("Некорректные образцы сплайна: проверьте совпадающие точки и длину пути.");
                previous = sample.distance;
                var hit = new LTRoadMath.Hit { position = sample.position, right = sample.right,
                    distance = sample.distance, bank = sample.bank };
                for (int side = 0; side < 2; side++)
                {
                    float lateral = (side == 0 ? -.5f : .5f) * snapshot.width;
                    hit.lateral = lateral; hit.radialDistance = Mathf.Abs(lateral);
                    var p = sample.position + sample.right * lateral;
                    p.y = snapshot.SurfaceHeight(hit, lateral) + offset;
                    if (!Finite(p)) throw new InvalidOperationException("Высота поверхности дороги не является конечным числом.");
                    vertices[i * 2 + side] = p;
                    uv[i * 2 + side] = new Vector2(side, sample.distance / repeat);
                }
            }
            // Area-weighted normals include triangles on BOTH sides of every chunk seam.
            for (int row = 0; row < rows - 1; row++)
            {
                int a = row * 2;
                Accumulate(a, a + 2, a + 1, vertices, normals);
                Accumulate(a + 1, a + 2, a + 3, vertices, normals);
            }
            var normalMatrix = terrainToRoad.inverse.transpose;
            for (int row = 0; row < rows; row++)
            {
                Vector3 across = vertices[row * 2 + 1] - vertices[row * 2];
                for (int side = 0; side < 2; side++)
                {
                    int index = row * 2 + side;
                    Vector3 n = normals[index].normalized;
                    if (n.sqrMagnitude < .5f) throw new InvalidOperationException("Вырожденный участок ленты. Разнесите точки или уменьшите ширину дороги.");
                    Vector3 along = vertices[Math.Min(rows - 1, row + 1) * 2 + side]
                        - vertices[Math.Max(0, row - 1) * 2 + side];
                    n = normalMatrix.MultiplyVector(n).normalized;
                    Vector3 t = terrainToRoad.MultiplyVector(across);
                    t = (t - n * Vector3.Dot(n, t)).normalized;
                    float sign = Vector3.Dot(Vector3.Cross(n, t), terrainToRoad.MultiplyVector(along)) < 0 ? -1 : 1;
                    normals[index] = n; tangents[index] = new Vector4(t.x, t.y, t.z, sign);
                }
            }
            for (int i = 0; i < vertices.Length; i++) vertices[i] = terrainToRoad.MultiplyPoint3x4(vertices[i]);
            var result = new List<Chunk>();
            int first = 0;
            while (first < rows - 1)
            {
                int last = first + 1;
                float end = snapshot.samples[first].distance + chunkLength;
                while (last < rows - 1 && snapshot.samples[last + 1].distance <= end) last++;
                if (result.Count >= MaxChunks)
                    throw new InvalidOperationException($"Лимит чанков: {MaxChunks}. Увеличьте длину чанка.");
                int count = (last - first + 1) * 2;
                var chunk = new Chunk { vertices = Slice(vertices, first * 2, count), normals = Slice(normals, first * 2, count),
                    tangents = Slice(tangents, first * 2, count), uv = Slice(uv, first * 2, count), triangles = new int[(last - first) * 6] };
                for (int i = 0; i < last - first; i++)
                {
                    int a = i * 2, j = i * 6;
                    chunk.triangles[j] = a; chunk.triangles[j + 1] = a + 2; chunk.triangles[j + 2] = a + 1;
                    chunk.triangles[j + 3] = a + 1; chunk.triangles[j + 4] = a + 2; chunk.triangles[j + 5] = a + 3;
                }
                chunk.hash = ContentHash(chunk); result.Add(chunk); first = last;
            }
            return result;
        }

        static void Accumulate(int a, int b, int c, Vector3[] vertices, Vector3[] normals)
        {
            var n = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            if (n.y <= 0) throw new InvalidOperationException("Лента складывается на повороте. Уменьшите ширину или сделайте поворот плавнее.");
            normals[a] += n; normals[b] += n; normals[c] += n;
        }
        static T[] Slice<T>(T[] data, int start, int length)
        { var result = new T[length]; Array.Copy(data, start, result, 0, length); return result; }
        static string ContentHash(Chunk chunk)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1); writer.Write(chunk.vertices.Length);
                for (int i = 0; i < chunk.vertices.Length; i++)
                {
                    var p = chunk.vertices[i]; writer.Write(p.x); writer.Write(p.y); writer.Write(p.z);
                    var n = chunk.normals[i]; writer.Write(n.x); writer.Write(n.y); writer.Write(n.z);
                    var t = chunk.tangents[i]; writer.Write(t.x); writer.Write(t.y); writer.Write(t.z); writer.Write(t.w);
                    writer.Write(chunk.uv[i].x); writer.Write(chunk.uv[i].y);
                }
                foreach (int index in chunk.triangles) writer.Write(index);
                writer.Flush();
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
