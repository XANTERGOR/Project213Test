using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public enum LTRoadMode { Offroad, Asphalt }
    public enum LTRoadPattern { Solid, Tracks }
    public enum LTRoadProjection { World, Spline }

    [Serializable]
    public struct LTRoadPoint
    {
        public Vector3 position;
        public float bank;
        public LTRoadPoint(Vector3 position, float bank = 0) { this.position = position; this.bank = bank; }
    }

    /// <summary>Managed spline construction and allocation-free terrain-local queries. No Unity object access.</summary>
    public static class LTRoadMath
    {
        public const int MaxPoints = 1024;
        public const int MaxSamples = 32768;
        public const int TextureBakeBlockSize = 8;
        const float MinSpan = .001f;

        public struct Sample
        {
            public Vector3 position;
            public Vector3 right;
            public float distance;
            public float bank;
        }

        public struct Hit
        {
            public Vector3 position;
            public Vector3 right;
            public float lateral;
            public float distance;
            public float radialDistance;
            public float bank;
        }

        /// <summary>Value-only input. Asset identity belongs to paint; material assignment is handled by the caller.</summary>
        public struct Settings
        {
            public LTRoadMode mode;
            public LTRoadPattern pattern;
            public LTRoadProjection projection;
            // Spline UV: U = .5 + hit.lateral / width + offset.x;
            // V = hit.distance / textureRepeatMetres + offset.y. Offroad remains a terrain layer.
            public Vector2 textureOffset;
            public float width, shoulderWidth, blendWidth, flatten;
            public float rutWidth, rutSeparation, rutDepth, edgeNoise, noiseSize;
            public int seed;
            public float sampleSpacing, terrainCellSize, meshChunkLength, textureRepeatMetres, surfaceOffset;
            public bool clearVegetation, clearStones;
            public bool straightStart,straightEnd;
            public float junctionStartLength,junctionEndLength;
            // Coverage multiplier, not a widening of the track mask: the centre strip stays intact.
            public float vegetationFade;
            public int groundLayerId;
            // Optional full source-world transform fingerprint, even if relative transforms cancel out.
            public int sourceTransformHash;

            public static Settings Default => new Settings
            {
                width = 6, shoulderWidth = 1, blendWidth = 3, flatten = 1,
                rutWidth = .55f, rutSeparation = 1.8f, rutDepth = .08f,
                edgeNoise = .2f, noiseSize = 3, seed = 12345,
                sampleSpacing = 1, terrainCellSize = .5f, meshChunkLength = 32,
                textureRepeatMetres = 4, surfaceOffset = .06f,
                clearVegetation = true, vegetationFade = 1
            };

            public void Validate()
            {
                if (mode != LTRoadMode.Offroad && mode != LTRoadMode.Asphalt)
                    throw Invalid("Choose Offroad or Asphalt mode.");
                if (pattern != LTRoadPattern.Solid && pattern != LTRoadPattern.Tracks)
                    throw Invalid("Choose Solid or Tracks pattern.");
                if (projection != LTRoadProjection.World && projection != LTRoadProjection.Spline)
                    throw Invalid("Choose World or Spline projection.");
                Range(textureOffset.x, -1e6f, 1e6f, "textureOffset.x");
                Range(textureOffset.y, -1e6f, 1e6f, "textureOffset.y");
                Range(width, .01f, 10000, "width");
                Range(shoulderWidth, 0, 10000, "shoulderWidth");
                Range(blendWidth, 0, 10000, "blendWidth");
                Range(flatten, 0, 1, "flatten");
                Range(rutWidth, .01f, 10000, "rutWidth");
                Range(rutSeparation, 0, 10000, "rutSeparation");
                Range(rutDepth, 0, 1000, "rutDepth");
                Range(edgeNoise, 0, 1, "edgeNoise");
                Range(noiseSize, .01f, 10000, "noiseSize");
                Range(sampleSpacing, .01f, 10000, "sampleSpacing");
                Range(terrainCellSize, .01f, 10000, "terrainCellSize");
                Range(meshChunkLength, .01f, 100000, "meshChunkLength");
                Range(textureRepeatMetres, .01f, 100000, "textureRepeatMetres");
                Range(surfaceOffset, 0, 1000, "surfaceOffset");
                Range(vegetationFade, 0, 1, "vegetationFade");
                Range(junctionStartLength,0,100000,"junctionStartLength");Range(junctionEndLength,0,100000,"junctionEndLength");
                if (mode == LTRoadMode.Offroad && pattern == LTRoadPattern.Tracks &&
                    (rutSeparation <= rutWidth || rutSeparation + rutWidth > width))
                    throw Invalid("Tracks need rutSeparation > rutWidth and rutSeparation + rutWidth <= width. Increase road width or reduce the ruts.");
            }
        }

        public sealed class Snapshot
        {
            readonly Sample[] data;
            readonly Node[] nodes;
            readonly Settings settings;
            public readonly ReadOnlyCollection<Sample> samples;
            public readonly Rect bounds;
            public readonly int geometryHash, paintHash, detailHash;
            public float width => settings.width;
            public float shoulderWidth => settings.shoulderWidth;
            public float blendWidth => settings.blendWidth;
            public float flatten => settings.flatten;
            public float rutWidth => settings.rutWidth;
            public float rutSeparation => settings.rutSeparation;
            public float rutDepth => settings.rutDepth;
            public float edgeNoise => settings.edgeNoise;
            public float noiseSize => settings.noiseSize;
            public int seed => settings.seed;
            public float sampleSpacing => settings.sampleSpacing;
            public float terrainCellSize => settings.terrainCellSize;
            public float meshChunkLength => settings.meshChunkLength;
            public float textureRepeatMetres => settings.textureRepeatMetres;
            public float surfaceOffset => settings.surfaceOffset;
            public LTRoadMode mode => settings.mode;
            public LTRoadPattern pattern => settings.pattern;
            public LTRoadProjection projection => settings.projection;
            public Vector2 textureOffset => settings.textureOffset;
            public bool clearVegetation => settings.clearVegetation;
            public bool clearStones => settings.clearStones;
            public float vegetationFade => settings.vegetationFade;
            public float length => data[data.Length - 1].distance;

            internal Snapshot(Sample[] source, Settings settings, int inputHash)
            {
                data = (Sample[])source.Clone();
                samples = Array.AsReadOnly(data);
                this.settings = settings;
                var tree = new List<Node>();
                BuildNode(tree, 0, data.Length - 1);
                nodes = tree.ToArray();
                float radius = width * .5f + shoulderWidth + blendWidth;
                var root = nodes[0];
                bounds = Rect.MinMaxRect(root.minX - radius, root.minZ - radius,
                    root.maxX + radius, root.maxZ + radius);
                Hashes(settings, inputHash, out geometryHash, out paintHash, out detailHash);
            }

            int BuildNode(List<Node> tree, int start, int end)
            {
                int index = tree.Count;
                tree.Add(default);
                var node = new Node { start = start, end = end, left = -1, right = -1,
                    minX = float.PositiveInfinity, minZ = float.PositiveInfinity,
                    maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity };
                if (end - start > 8)
                {
                    int mid = (start + end) / 2;
                    node.left = BuildNode(tree, start, mid);
                    node.right = BuildNode(tree, mid, end);
                    var a = tree[node.left]; var b = tree[node.right];
                    node.minX = Math.Min(a.minX, b.minX); node.maxX = Math.Max(a.maxX, b.maxX);
                    node.minZ = Math.Min(a.minZ, b.minZ); node.maxZ = Math.Max(a.maxZ, b.maxZ);
                }
                else for (int i = start; i <= end; i++)
                {
                    var p = data[i].position;
                    node.minX = Math.Min(node.minX, p.x); node.maxX = Math.Max(node.maxX, p.x);
                    node.minZ = Math.Min(node.minZ, p.z); node.maxZ = Math.Max(node.maxZ, p.z);
                }
                tree[index] = node;
                return index;
            }

            /// <summary>Nearest clamped XZ segment, including outside the influence bounds. False for non-finite input.</summary>
            public bool TrySample(float x, float z, out Hit hit)
                => TrySampleWithin(x, z, float.PositiveInfinity, out hit);

            // Limit height/mask searches, but never the endpoint-extending UV query.
            bool TrySampleWithin(float x, float z, float maxDistanceSquared, out Hit hit)
            {
                hit = default;
                if (!Finite(x) || !Finite(z) || Math.Abs(x) > 1e7f || Math.Abs(z) > 1e7f) return false;
                float best = maxDistanceSquared, t = 0;
                int segment = -1;
                Nearest(0, x, z, ref best, ref segment, ref t);
                if (segment < 0) return false;
                var a = data[segment]; var b = data[segment + 1];
                var p = a.position + (b.position - a.position) * t;
                var right = HorizontalUnit(a.right + (b.right - a.right) * t);
                hit = new Hit { position = p, right = right,
                    lateral = (x - p.x) * right.x + (z - p.z) * right.z,
                    radialDistance = (float)Math.Sqrt(best),
                    distance = a.distance + (b.distance - a.distance) * t,
                    bank = a.bank + (b.bank - a.bank) * t };
                return true;
            }

            bool TryInfluenceSample(float x, float z, out Hit hit)
            {
                float radius = width * .5f + shoulderWidth + blendWidth;
                // Conservative guard for squared-distance rounding at the outer edge.
                radius += Math.Max(.001f, radius * .00001f);
                return TrySampleWithin(x, z, radius * radius, out hit);
            }

            // Same suppression formula as the map baker, but with a bounded nearest
            // search. Padding is the baker's bilinear cell-diagonal guard, not blendWidth.
            public float DisplacementSuppression(float x,float z,float padding)
            {
                if(!Finite(padding)||padding<0)throw Invalid("Suppression padding must be finite and non-negative.");
                float feather=Math.Max(.0001f,shoulderWidth);
                float radius=width*.5f+padding+feather;
                radius+=Math.Max(.001f,radius*.00001f);
                if(!TrySampleWithin(x,z,radius*radius,out var hit))return 0;
                float distance=hit.radialDistance-width*.5f-padding;
                return 1-Mathf.SmoothStep(0,1,distance/feather);
            }

            /// <summary>Unwrapped texture frame. Unlike geometry queries, UVs extend
            /// beyond the endpoint tangent so rounded painted caps do not smear one texel row.</summary>
            public bool TryTextureCoordinates(float x, float z, out Vector2 uv, out Vector2 right)
            {
                uv = default; right = Vector2.right;
                if (!TrySample(x, z, out var hit)) return false;
                float along = hit.distance;
                // XZ projection uses a horizontal unit tangent, while stored arc length
                // includes height. Continue V at the adjacent segment's metres-per-XZ-metre.
                int end = along <= 0 ? 0 : along >= length ? data.Length - 2 : -1;
                if (end >= 0)
                {
                    var a = data[end]; var b = data[end + 1];
                    float dx = b.position.x - a.position.x, dz = b.position.z - a.position.z;
                    float square = dx * dx + dz * dz;
                    if (square > 1e-12f)
                        along += ((x - hit.position.x) * dx + (z - hit.position.z) * dz)
                            / square * (b.distance - a.distance);
                }
                uv = new Vector2(hit.lateral / width + .5f, along / textureRepeatMetres) + textureOffset;
                right = new Vector2(hit.right.x, hit.right.z).normalized;
                return true;
            }

            /// <summary>Endpoint-grid texels needed by this road, including the
            /// bilinear footprint and one additional guard texel. Half-open bounds.</summary>
            public RectInt TextureBakeRegion(Rect chunk, int resolution)
            {
                if (resolution < 2 || !Finite(chunk.xMin) || !Finite(chunk.yMin) ||
                    !Finite(chunk.width) || !Finite(chunk.height) || chunk.width <= 0 || chunk.height <= 0)
                    throw Invalid("TextureBakeRegion requires a finite chunk and resolution >= 2.");
                if (bounds.xMax < chunk.xMin || bounds.xMin > chunk.xMax || bounds.yMax < chunk.yMin || bounds.yMin > chunk.yMax)
                    return new RectInt(0, 0, 0, 0);
                float scaleX = (resolution - 1) / chunk.width, scaleZ = (resolution - 1) / chunk.height;
                int x0 = Mathf.Clamp(Mathf.FloorToInt((bounds.xMin - chunk.xMin) * scaleX) - 2, 0, resolution);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((bounds.yMin - chunk.yMin) * scaleZ) - 2, 0, resolution);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((bounds.xMax - chunk.xMin) * scaleX) + 3, 0, resolution);
                int z1 = Mathf.Clamp(Mathf.CeilToInt((bounds.yMax - chunk.yMin) * scaleZ) + 3, 0, resolution);
                return new RectInt(x0, z0, x1 - x0, z1 - z0);
            }

            /// <summary>Conservative sparse UV block selection. Two texels retain
            /// weight interpolation support and the UV/Jacobian's four-corner footprint.</summary>
            public bool TextureBakeBlockIntersects(Rect chunk,int resolution,RectInt block)
            {
                if(resolution<2||chunk.width<=0||chunk.height<=0)throw Invalid("Invalid UV block grid.");
                if(block.width<=0||block.height<=0)return false;
                float dx=chunk.width/(resolution-1),dz=chunk.height/(resolution-1);
                var area=Rect.MinMaxRect(chunk.xMin+(block.xMin-2)*dx,chunk.yMin+(block.yMin-2)*dz,
                    chunk.xMin+(block.xMax+1)*dx,chunk.yMin+(block.yMax+1)*dz);
                return Intersects(area);
            }

            void Nearest(int index, float x, float z, ref float best, ref int segment, ref float fraction)
            {
                var node = nodes[index];
                if (node.DistanceSquared(x, z) > best) return;
                if (node.left >= 0)
                {
                    int first = node.left, second = node.right;
                    if (nodes[second].DistanceSquared(x, z) < nodes[first].DistanceSquared(x, z))
                    { first = node.right; second = node.left; }
                    Nearest(first, x, z, ref best, ref segment, ref fraction);
                    Nearest(second, x, z, ref best, ref segment, ref fraction);
                    return;
                }
                for (int i = node.start; i < node.end; i++)
                {
                    var a = data[i].position; var b = data[i + 1].position;
                    float dx = b.x - a.x, dz = b.z - a.z;
                    float t = Clamp01(((x - a.x) * dx + (z - a.z) * dz) / (dx * dx + dz * dz));
                    float rx = x - (a.x + dx * t), rz = z - (a.z + dz * t);
                    float d = rx * rx + rz * rz;
                    if (d < best || (d == best && (segment < 0 || i < segment)))
                    { best = d; segment = i; fraction = t; }
                }
            }

            /// <summary>Terrain reference height including crossfall. Asphalt mesh vertices add surfaceOffset once.</summary>
            public float SurfaceHeight(Hit hit, float lateral)
            {
                if (!Finite(lateral) || !Finite(hit.position.y) || !Finite(hit.bank))
                    throw Invalid("SurfaceHeight requires a finite hit and lateral coordinate.");
                return hit.position.y + lateral * (float)Math.Tan(Math.Max(-80, Math.Min(80, hit.bank)) * Math.PI / 180);
            }

            public float ApplyHeight(float x, float z, float originalHeight)
            {
                if (!Finite(originalHeight)) throw Invalid("originalHeight must be finite.");
                if (mode == LTRoadMode.Offroad && flatten <= 0 && !settings.straightStart&&!settings.straightEnd) return originalHeight;
                if (!InsideBounds(x, z) || !TryInfluenceSample(x, z, out var hit)) return originalHeight;
                // Connected endpoints have a cut plane, not a rounded height cap.
                // In particular an offroad ramp must not lift terrain through the asphalt centre.
                if(settings.straightStart&&hit.distance<=0&&Vector3.Dot(new Vector3(x-hit.position.x,0,z-hit.position.z),new Vector3(-data[0].right.z,0,data[0].right.x))<-.00001f)return originalHeight;
                if(settings.straightEnd&&hit.distance>=length&&Vector3.Dot(new Vector3(x-hit.position.x,0,z-hit.position.z),new Vector3(-data[data.Length-1].right.z,0,data[data.Length-1].right.x))>.00001f)return originalHeight;
                float rutFade=1;
                if(settings.straightStart)rutFade=Math.Min(rutFade,Mathf.SmoothStep(0,1,hit.distance/Math.Max(.001f,settings.junctionStartLength)));
                if(settings.straightEnd)rutFade=Math.Min(rutFade,Mathf.SmoothStep(0,1,(length-hit.distance)/Math.Max(.001f,settings.junctionEndLength)));
                float weight = Fade(hit.radialDistance, width * .5f, shoulderWidth + blendWidth);
                weight *= mode == LTRoadMode.Asphalt ? 1 : Math.Max(flatten,1-rutFade);
                if (weight <= 0) return originalHeight;
                float target = SurfaceHeight(hit, hit.lateral);
                if (mode == LTRoadMode.Offroad && pattern == LTRoadPattern.Tracks)
                    target -= rutDepth * TrackWeight(hit, x, z)*rutFade;
                // Offset never subtracts from originalHeight: repeat evaluation cannot accumulate an asphalt offset.
                return originalHeight + (target - originalHeight) * weight;
            }

            public float PaintWeight(float x, float z)
            {
                if (!InsideBounds(x, z) || !TryInfluenceSample(x, z, out var hit)) return 0;
                return mode == LTRoadMode.Offroad && pattern == LTRoadPattern.Tracks
                    ? TrackWeight(hit, x, z) : Fade(hit.radialDistance, width * .5f, shoulderWidth);
            }

            public float ClearWeight(float x, float z, LTDetailCategory category)
            {
                bool vegetation = clearVegetation && (category & LTDetailCategory.Vegetation) != 0;
                bool stones = clearStones && (category & LTDetailCategory.Stones) != 0;
                if (!vegetation && !stones) return 0;
                return PaintWeight(x, z) * (stones ? 1 : vegetationFade);
            }

            float TrackWeight(Hit hit, float x, float z)
            {
                // Noise erodes the outer strip edges only. The configured central gap can never be filled.
                float half = rutWidth * .5f;
                float extent = half * (1 - edgeNoise * Noise(x / noiseSize, z / noiseSize, seed) * .8f);
                float across = Math.Abs(Math.Abs(hit.lateral) - rutSeparation * .5f);
                float beyond = Math.Max(0, hit.radialDistance * hit.radialDistance - hit.lateral * hit.lateral);
                float radial = (float)Math.Sqrt(across * across + beyond);
                return Fade(radial, extent * .65f, extent * .35f);
            }

            bool InsideBounds(float x, float z) => Finite(x) && Finite(z) &&
                x >= bounds.xMin && x <= bounds.xMax && z >= bounds.yMin && z <= bounds.yMax;

            /// <summary>Conservative influence intersection (includes shoulder and height blend).</summary>
            public bool Intersects(Rect rect)
            {
                if (!Finite(rect.xMin) || !Finite(rect.xMax) || !Finite(rect.yMin) || !Finite(rect.yMax)) return false;
                float minX = Math.Min(rect.xMin, rect.xMax), maxX = Math.Max(rect.xMin, rect.xMax);
                float minZ = Math.Min(rect.yMin, rect.yMax), maxZ = Math.Max(rect.yMin, rect.yMax);
                float radius = width * .5f + shoulderWidth + blendWidth;
                return IntersectsNode(0, minX - radius, minZ - radius, maxX + radius, maxZ + radius);
            }

            bool IntersectsNode(int index, float minX, float minZ, float maxX, float maxZ)
            {
                var n = nodes[index];
                if (n.maxX < minX || n.minX > maxX || n.maxZ < minZ || n.minZ > maxZ) return false;
                return n.left < 0 || IntersectsNode(n.left, minX, minZ, maxX, maxZ) ||
                    IntersectsNode(n.right, minX, minZ, maxX, maxZ);
            }
        }

        struct Node
        {
            public int start, end, left, right;
            public float minX, minZ, maxX, maxZ;
            public float DistanceSquared(float x, float z)
            {
                float dx = Math.Max(0, Math.Max(minX - x, x - maxX));
                float dz = Math.Max(0, Math.Max(minZ - z, z - maxZ));
                return dx * dx + dz * dz;
            }
        }

        /// <summary>Build from authored points and a unit-scale, yaw/translation-only road-to-terrain matrix.</summary>
        public static Snapshot Build(IReadOnlyList<LTRoadPoint> points, Matrix4x4 terrainFromRoad, Settings settings)
        {
            settings.Validate();
            ValidateTransform(terrainFromRoad, false, "Road-to-terrain transform");
            if (points == null || points.Count < 2 || points.Count > MaxPoints)
                throw Invalid("Use 2 to " + MaxPoints + " road points; split longer roads into separate components.");
            var source = new LTRoadPoint[points.Count];
            int hash = Mix(17, settings.sourceTransformHash);
            if(settings.straightStart||settings.straightEnd)hash=Mix(hash,(settings.straightStart?1:0)|(settings.straightEnd?2:0));
            if(settings.straightStart)hash=Add(hash,settings.junctionStartLength);
            if(settings.straightEnd)hash=Add(hash,settings.junctionEndLength);
            hash = Mix(hash, TransformHash(terrainFromRoad));
            hash = Mix(hash, points.Count);
            for (int i = 0; i < source.Length; i++)
            {
                var point = points[i];
                ValidatePoint(point, i);
                hash = AddPoint(hash, point);
                source[i] = new LTRoadPoint(terrainFromRoad.MultiplyPoint3x4(point.position), point.bank);
                ValidatePoint(source[i], i);
                if (i > 0 && HorizontalLength(source[i].position - source[i - 1].position) < MinSpan)
                    throw Invalid("Points " + (i - 1) + " and " + i + " have no horizontal span. Move or remove one of them.");
                if (i > 1)
                {
                    var a = HorizontalUnit(source[i - 1].position - source[i - 2].position);
                    var b = HorizontalUnit(source[i].position - source[i - 1].position);
                    if (a.x * b.x + a.z * b.z < -.95f)
                        throw Invalid("Road backtracks at point " + (i - 1) + ". Spread the turn over more points.");
                }
            }
            var result = new List<Sample> { new Sample { position = source[0].position, bank = source[0].bank } };
            for (int i = 0; i < source.Length - 1; i++)
            {
                Vector3 p1 = source[i].position, p2 = source[i + 1].position;
                Vector3 p0 = i > 0 ? source[i - 1].position : p1 * 2 - p2;
                Vector3 p3 = i + 2 < source.Length ? source[i + 2].position : p2 * 2 - p1;
                // Centripetal Catmull-Rom expressed as cubic Bezier. XZ knots protect the horizontal footprint.
                float d0 = (float)Math.Sqrt(HorizontalLength(p1 - p0));
                float d1 = (float)Math.Sqrt(HorizontalLength(p2 - p1));
                float d2 = (float)Math.Sqrt(HorizontalLength(p3 - p2));
                var m1 = d1 * ((p1 - p0) / d0 - (p2 - p0) / (d0 + d1) + (p2 - p1) / d1);
                var m2 = d1 * ((p2 - p1) / d1 - (p3 - p1) / (d1 + d2) + (p3 - p2) / d2);
                if(settings.straightStart&&i==0||settings.straightEnd&&i==source.Length-2)m1=m2=p2-p1;
                else
                {
                    if(settings.straightStart&&i==1)m1.y=0;
                    if(settings.straightEnd&&i==source.Length-3)m2.y=0;
                }
                Subdivide(result, p1, p1 + m1 / 3, p2 - m2 / 3, p2,
                    source[i].bank, source[i + 1].bank, settings.sampleSpacing, 0, p2 - p1);
            }
            for (int i = 0; i < result.Count; i++)
            {
                var sample = result[i];
                var tangent = i == 0 ? result[1].position - sample.position :
                    i == result.Count - 1 ? sample.position - result[i - 1].position :
                    HorizontalUnit(sample.position - result[i - 1].position) + HorizontalUnit(result[i + 1].position - sample.position);
                tangent = HorizontalUnit(tangent);
                sample.right = new Vector3(tangent.z, 0, -tangent.x);
                if (i > 0) sample.distance = result[i - 1].distance + Length(sample.position - result[i - 1].position);
                result[i] = sample;
            }
            return new Snapshot(result.ToArray(), settings, hash);
        }

        static void Subdivide(List<Sample> result, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            float bankA, float bankD, float spacing, int depth, Vector3 chord)
        {
            float hull = Length(b - a) + Length(c - b) + Length(d - c);
            float error = Math.Max(Length(b - (a * (2f / 3) + d / 3)), Length(c - (a / 3 + d * (2f / 3))));
            if (hull <= spacing && error <= Math.Min(.02f, spacing * .02f))
            {
                var delta = d - a;
                if (HorizontalLength(delta) < .00001f || delta.x * chord.x + delta.z * chord.z <= 0)
                    throw Invalid("Spline folds back or loses horizontal span. Space out points, reduce height jumps, or simplify the turn.");
                if (result.Count >= MaxSamples)
                    throw Invalid("Road exceeds " + MaxSamples + " samples. Increase sampleSpacing or split the road.");
                result.Add(new Sample { position = d, bank = bankD });
                return;
            }
            if (depth >= 20) throw Invalid("Road cannot meet sampleSpacing safely. Simplify points or increase sampleSpacing.");
            var ab = (a + b) * .5f; var bc = (b + c) * .5f; var cd = (c + d) * .5f;
            var abc = (ab + bc) * .5f; var bcd = (bc + cd) * .5f; var mid = (abc + bcd) * .5f;
            float bankMid = (bankA + bankD) * .5f;
            Subdivide(result, a, ab, abc, mid, bankA, bankMid, spacing, depth + 1, chord);
            Subdivide(result, mid, bcd, cd, d, bankMid, bankD, spacing, depth + 1, chord);
        }

        /// <summary>Validate the full effective matrix, including inherited scale, reflection and shear.</summary>
        public static void ValidateTransform(Matrix4x4 matrix, bool world, string label)
        {
            const float tolerance = .0001f;
            for (int i = 0; i < 16; i++) if (!Finite(matrix[i])) throw Invalid(label + " contains NaN or infinity.");
            bool valid = Math.Abs(matrix.m30) <= tolerance && Math.Abs(matrix.m31) <= tolerance &&
                Math.Abs(matrix.m32) <= tolerance && Math.Abs(matrix.m33 - 1) <= tolerance;
            if (world)
            {
                for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++)
                    valid &= Math.Abs(matrix[row, col] - (row == col ? 1 : 0)) <= tolerance;
            }
            else
            {
                valid &= Math.Abs(matrix.m01) <= tolerance && Math.Abs(matrix.m21) <= tolerance &&
                    Math.Abs(matrix.m10) <= tolerance && Math.Abs(matrix.m12) <= tolerance && Math.Abs(matrix.m11 - 1) <= tolerance;
                valid &= Math.Abs(matrix.m00 * matrix.m00 + matrix.m20 * matrix.m20 - 1) <= tolerance &&
                    Math.Abs(matrix.m02 * matrix.m02 + matrix.m22 * matrix.m22 - 1) <= tolerance &&
                    Math.Abs(matrix.m00 * matrix.m02 + matrix.m20 * matrix.m22) <= tolerance &&
                    Math.Abs(matrix.m00 * matrix.m22 - matrix.m02 * matrix.m20 - 1) <= tolerance;
            }
            if (!valid) throw Invalid(label + (world ? " must have unit scale and no rotation (including parents)." :
                " must have unit scale and only translation/yaw; remove pitch, roll, shear or reflected scale (including parents)."));
        }

        public static int TransformHash(Matrix4x4 matrix)
        {
            int hash = 17;
            for (int i = 0; i < 16; i++) hash = Add(hash, matrix[i]);
            return hash;
        }

        static void Hashes(Settings s, int input, out int geometry, out int paint, out int detail)
        {
            int common = Add(Mix(input, (int)s.mode), s.sampleSpacing);
            common = Add(common, s.width); common = Add(common, s.shoulderWidth);
            int mask = common;
            bool tracks = s.mode == LTRoadMode.Offroad && s.pattern == LTRoadPattern.Tracks;
            if (s.mode == LTRoadMode.Offroad) mask = Mix(mask, (int)s.pattern);
            if (tracks)
            {
                mask = Add(mask, s.rutWidth); mask = Add(mask, s.rutSeparation); mask = Add(mask, s.edgeNoise);
                if (s.edgeNoise > 0) { mask = Add(mask, s.noiseSize); mask = Mix(mask, s.seed); }
            }
            geometry = common;
            geometry = Add(geometry, s.blendWidth); geometry = Add(geometry, s.terrainCellSize);
            if (s.mode == LTRoadMode.Offroad)
            {
                geometry = Add(geometry, s.flatten);
                if (s.flatten > 0 && tracks && s.rutDepth > 0) { geometry = Mix(geometry, mask); geometry = Add(geometry, s.rutDepth); }
            }
            else
            {
                geometry = Add(geometry, s.meshChunkLength); geometry = Add(geometry, s.textureRepeatMetres);
                geometry = Add(geometry, s.surfaceOffset);
            }
            paint = Mix(mask, s.groundLayerId);
            paint = Mix(paint, (int)s.projection);
            paint = Add(paint, s.textureOffset.x); paint = Add(paint, s.textureOffset.y);
            if (s.projection == LTRoadProjection.Spline) paint = Add(paint, s.textureRepeatMetres);
            detail = Mix(Mix(mask, s.clearVegetation ? 1 : 0), s.clearStones ? 1 : 0);
            if (s.clearVegetation) detail = Add(detail, s.vegetationFade);
        }

        static int AddPoint(int hash, LTRoadPoint point)
        {
            hash = Add(hash, point.position.x); hash = Add(hash, point.position.y);
            hash = Add(hash, point.position.z); return Add(hash, point.bank);
        }
        static int Add(int hash, float value) => Mix(hash, value.GetHashCode());
        static int Mix(int hash, int value) { unchecked { return hash * 397 ^ value; } }
        static float Noise(float x, float z, int seed)
        {
            int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
            float u = Smooth(x - ix), v = Smooth(z - iz);
            float a = Corner(ix, iz, seed), b = Corner(ix + 1, iz, seed);
            float c = Corner(ix, iz + 1, seed), d = Corner(ix + 1, iz + 1, seed);
            return (a + (b - a) * u) * (1 - v) + (c + (d - c) * u) * v;
        }
        static float Corner(int x, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)Mix(Mix(seed, x), z);
                h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
                return (h >> 8) * (1f / 16777216);
            }
        }
        static float Fade(float distance, float inner, float feather)
            => distance <= inner ? 1 : feather <= 0 ? 0 : 1 - Smooth(Clamp01((distance - inner) / feather));
        static float Smooth(float value) => value * value * (3 - 2 * value);
        static float Clamp01(float value) => Math.Max(0, Math.Min(1, value));
        static float Length(Vector3 value) => (float)Math.Sqrt((double)value.x * value.x + (double)value.y * value.y + (double)value.z * value.z);
        static float HorizontalLength(Vector3 value) => (float)Math.Sqrt((double)value.x * value.x + (double)value.z * value.z);
        static Vector3 HorizontalUnit(Vector3 value)
        {
            float length = HorizontalLength(value);
            if (length < .000001f) throw Invalid("Road tangent has no stable horizontal direction. Simplify the turn.");
            return new Vector3(value.x / length, 0, value.z / length);
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static void Range(float value, float min, float max, string name)
        {
            if (!Finite(value) || value < min || value > max)
                throw Invalid(name + " must be finite and between " + min + " and " + max + ".");
        }
        static void ValidatePoint(LTRoadPoint point, int index)
        {
            Range(point.position.x, -1e6f, 1e6f, "Point " + index + " X");
            Range(point.position.y, -1e6f, 1e6f, "Point " + index + " Y");
            Range(point.position.z, -1e6f, 1e6f, "Point " + index + " Z");
            Range(point.bank, -80, 80, "Point " + index + " bank (degrees)");
        }
        static ArgumentException Invalid(string message) => new ArgumentException("LTRoad: " + message);
    }
}
