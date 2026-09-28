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
    public struct LTRoadWheelTracks
    {
        public bool enabled, clearStones;
        [Range(0,1)] public float strength;
        [Min(.01f)] public float width;
        [Min(.01f)] public float separation;
        [Range(.01f,1)] public float softness;
        public bool independentTiling;
        public Vector2 tileSizeMetres,textureOffset;
        public static LTRoadWheelTracks Default=>new LTRoadWheelTracks{strength=.85f,width=.65f,separation=1.8f,softness=.4f,clearStones=true,
            tileSizeMetres=new Vector2(6,4)};
    }

    [Serializable]
    public struct LTRoadVariation
    {
        public bool enabled, solidRuts;
        [Range(0,1)] public float strength;
        [Range(0,.35f)] public float widthAmount;
        [Min(.1f)] public float widthLength;
        [Range(0,1)] public float patchStrength;
        [Min(.1f)] public float patchSize;
        [Range(0,1)] public float rutVariation;
        [Min(.1f)] public float rutLength;
        public int seed;
        public static LTRoadVariation Default=>new LTRoadVariation{strength=1,widthAmount=.15f,widthLength=12,
            patchStrength=.5f,patchSize=8,rutVariation=.65f,rutLength=10,solidRuts=true,seed=12345};
    }

    [Serializable]
    public struct LTRoadPoint
    {
        public Vector3 position;
        public float bank;
        public bool overrideVariation;
        [Range(0,1)] public float variationStrength;
        public float VariationStrength=>overrideVariation?variationStrength:1;
        public LTRoadPoint(Vector3 position, float bank = 0)
        { this.position = position; this.bank = bank; overrideVariation=false; variationStrength=1; }
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
            public float variationStrength;
        }

        public struct Hit
        {
            public Vector3 position;
            public Vector3 right;
            public float lateral;
            public float distance;
            public float radialDistance;
            public float bank;
            public float variationStrength;
        }

        /// <summary>Value-only input. Asset identity belongs to paint; material assignment is handled by the caller.</summary>
        public struct Settings
        {
            public LTRoadMode mode;
            public LTRoadPattern pattern;
            public LTRoadProjection projection;
            // Spline UV: U = .5 + hit.lateral / acrossRepeat + offset.x;
            // V = hit.distance / textureRepeatMetres + offset.y. Offroad remains a terrain layer.
            public Vector2 textureOffset;
            // Zero preserves the legacy one-repeat-across-the-road mapping.
            public float textureAcrossMetres;
            public float width, shoulderWidth, blendWidth, flatten;
            public float rutWidth, rutSeparation, rutDepth, edgeNoise, noiseSize;
            public int seed;
            public float sampleSpacing, terrainCellSize, meshChunkLength, textureRepeatMetres, surfaceOffset;
            public bool clearVegetation, clearStones;
            public bool vegetationOnlyWheelTracks;
            public bool straightStart,straightEnd;
            public float junctionStartLength,junctionEndLength;
            // Coverage multiplier, not a widening of the track mask: the centre strip stays intact.
            public float vegetationFade;
            public int groundLayerId;
            // Optional full source-world transform fingerprint, even if relative transforms cancel out.
            public int sourceTransformHash;
            public LTRoadVariation variation;
            public LTRoadWheelTracks wheelTracks;
            public int wheelLayerId;

            public static Settings Default => new Settings
            {
                width = 6, shoulderWidth = 1, blendWidth = 3, flatten = 1,
                rutWidth = .55f, rutSeparation = 1.8f, rutDepth = .08f,
                edgeNoise = .2f, noiseSize = 3, seed = 12345,
                sampleSpacing = 1, terrainCellSize = .5f, meshChunkLength = 32,
                textureRepeatMetres = 4, surfaceOffset = .06f,
                clearVegetation = true, vegetationFade = 1, variation=LTRoadVariation.Default,wheelTracks=LTRoadWheelTracks.Default
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
                if(mode==LTRoadMode.Offroad&&projection==LTRoadProjection.Spline)
                    Range(textureAcrossMetres,0,100000,"textureAcrossMetres");
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
                if(mode==LTRoadMode.Offroad&&wheelTracks.enabled)
                {
                    Range(wheelTracks.width,.01f,10000,"wheelTracks.width");Range(wheelTracks.separation,.01f,10000,"wheelTracks.separation");
                    Range(wheelTracks.strength,0,1,"wheelTracks.strength");Range(wheelTracks.softness,.01f,1,"wheelTracks.softness");
                    if(wheelTracks.separation<=wheelTracks.width||wheelTracks.separation+wheelTracks.width>width)
                        throw Invalid("Следы колёс: расстояние между центрами должно быть больше ширины одного следа; сумма расстояния и ширины следа должна помещаться в ширину дороги.");
                    if(projection==LTRoadProjection.Spline&&wheelTracks.independentTiling)
                    {
                        Range(wheelTracks.tileSizeMetres.x,.01f,100000,"wheelTracks.tileSizeMetres.x");
                        Range(wheelTracks.tileSizeMetres.y,.01f,100000,"wheelTracks.tileSizeMetres.y");
                        Range(wheelTracks.textureOffset.x,-1e6f,1e6f,"wheelTracks.textureOffset.x");
                        Range(wheelTracks.textureOffset.y,-1e6f,1e6f,"wheelTracks.textureOffset.y");
                    }
                }
                if(mode==LTRoadMode.Offroad&&variation.enabled)
                {
                    Range(variation.strength,0,1,"variation.strength");Range(variation.widthAmount,0,.35f,"variation.widthAmount");
                    Range(variation.widthLength,.1f,100000,"variation.widthLength");Range(variation.patchSize,.1f,100000,"variation.patchSize");
                    Range(variation.patchStrength,0,1,"variation.patchStrength");Range(variation.rutVariation,0,1,"variation.rutVariation");
                    Range(variation.rutLength,.1f,100000,"variation.rutLength");
                }
                if (mode == LTRoadMode.Offroad && (pattern == LTRoadPattern.Tracks||variation.enabled&&variation.strength>0&&variation.solidRuts) &&
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
            public readonly int geometryHash, paintHash, detailHash, projectionHash, wheelPaintHash;
            readonly bool wheelLayerView;
            readonly int projectionFootprintHash;
            Snapshot wheelProjection;
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
            bool OwnWheelTiling=>wheelLayerView&&settings.wheelTracks.independentTiling&&mode==LTRoadMode.Offroad&&projection==LTRoadProjection.Spline;
            public float textureRepeatMetres => OwnWheelTiling?settings.wheelTracks.tileSizeMetres.y:settings.textureRepeatMetres;
            public float textureAcrossMetres => OwnWheelTiling?settings.wheelTracks.tileSizeMetres.x:
                mode==LTRoadMode.Offroad&&projection==LTRoadProjection.Spline&&settings.textureAcrossMetres>0?Math.Max(.01f,settings.textureAcrossMetres):width;
            public float surfaceOffset => settings.surfaceOffset;
            public LTRoadMode mode => settings.mode;
            public LTRoadPattern pattern => settings.pattern;
            public LTRoadProjection projection => settings.projection;
            public Vector2 textureOffset => OwnWheelTiling?settings.wheelTracks.textureOffset:settings.textureOffset;
            public bool clearVegetation => settings.clearVegetation;
            public bool clearStones => settings.clearStones;
            public float vegetationFade => settings.vegetationFade;
            public float length => data[data.Length - 1].distance;
            bool Varied=>mode==LTRoadMode.Offroad&&settings.variation.enabled&&settings.variation.strength>0;
            public float MaxHalfWidth=>width*.5f*(1+(Varied?settings.variation.strength*settings.variation.widthAmount:0));

            internal Snapshot(Sample[] source, Settings settings, int inputHash, int profileHash)
            {
                data = (Sample[])source.Clone();
                samples = Array.AsReadOnly(data);
                this.settings = settings;
                var tree = new List<Node>();
                BuildNode(tree, 0, data.Length - 1);
                nodes = tree.ToArray();
                float radius = MaxHalfWidth + shoulderWidth + blendWidth;
                var root = nodes[0];
                bounds = Rect.MinMaxRect(root.minX - radius, root.minZ - radius,
                    root.maxX + radius, root.maxZ + radius);
                int uvHash=Add(Add(Add(inputHash,settings.sampleSpacing),width),MaxHalfWidth);
                projectionFootprintHash=Add(Add(uvHash,shoulderWidth),blendWidth);
                projectionHash=TextureHash(projectionFootprintHash,textureAcrossMetres,textureRepeatMetres,textureOffset);
                Hashes(settings, inputHash, profileHash, out geometryHash, out paintHash, out detailHash);
                if(mode==LTRoadMode.Offroad&&clearVegetation&&settings.vegetationOnlyWheelTracks)
                {
                    detailHash=Mix(detailHash,0x57484545);
                    detailHash=Mix(detailHash,settings.wheelTracks.enabled?1:0);
                    if(settings.wheelTracks.enabled)detailHash=Add(Add(detailHash,settings.wheelTracks.width),settings.wheelTracks.separation);
                }
                wheelPaintHash=paintHash;
                if(mode==LTRoadMode.Offroad&&settings.wheelTracks.enabled)
                {
                    var w=settings.wheelTracks;
                    int maskKey=Add(Add(Add(Add(inputHash,sampleSpacing),w.width),w.separation),w.softness);
                    maskKey=Add(Add(Add(maskKey,width),shoulderWidth),edgeNoise);
                    if(edgeNoise>0)maskKey=Add(Mix(maskKey,seed),noiseSize);
                    if(Varied)
                    {
                        var v=settings.variation;maskKey=Mix(maskKey,profileHash);
                        maskKey=Add(Mix(maskKey,v.seed),v.strength);
                        maskKey=Add(Add(Add(Add(maskKey,v.widthAmount),v.widthLength),v.rutVariation),v.rutLength);
                    }
                    wheelPaintHash=Add(Mix(maskKey,settings.wheelLayerId),w.strength);
                    // Independent scaling/offset of the same continuous road frame.
                    int wheelUV=w.independentTiling&&projection==LTRoadProjection.Spline?
                        TextureHash(projectionFootprintHash,w.tileSizeMetres.x,w.tileSizeMetres.y,w.textureOffset):projectionHash;
                    wheelPaintHash=Mix(wheelPaintHash,wheelUV);
                    if(settings.wheelLayerId!=0)paintHash=Mix(paintHash,wheelPaintHash);
                    if(w.clearStones)detailHash=Mix(detailHash,maskKey);
                }
            }

            int TextureHash(int footprint,float across,float along,Vector2 offset)
            {
                // Unchanged defaults retain their old projection signature.
                if(across!=width)footprint=Add(footprint,across);
                return Mix(Add(Add(Add(footprint,along),offset.x),offset.y),(int)projection);
            }

            // Same immutable BVH/samples, with wheel coverage for ownership and an
            // optional independent UV scale/offset. No spline rebuild/native object.
            Snapshot(Snapshot source)
            {
                data=source.data;nodes=source.nodes;settings=source.settings;samples=source.samples;bounds=source.bounds;
                geometryHash=source.geometryHash;paintHash=source.wheelPaintHash;detailHash=source.detailHash;
                wheelPaintHash=source.wheelPaintHash;wheelLayerView=true;
                projectionFootprintHash=source.projectionFootprintHash;
                projectionHash=TextureHash(projectionFootprintHash,textureAcrossMetres,textureRepeatMetres,textureOffset);
            }
            public Snapshot WheelLayerProjection()=>wheelProjection??(wheelProjection=new Snapshot(this));

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
                    bank = a.bank + (b.bank - a.bank) * t,
                    variationStrength=a.variationStrength+(b.variationStrength-a.variationStrength)*t };
                return true;
            }

            bool TryInfluenceSample(float x, float z, out Hit hit)
            {
                float radius = MaxHalfWidth + shoulderWidth + blendWidth;
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
                uv = new Vector2(hit.lateral / textureAcrossMetres + .5f, along / textureRepeatMetres) + textureOffset;
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
                float weight = Fade(hit.radialDistance, HalfWidth(hit), shoulderWidth + blendWidth);
                weight *= mode == LTRoadMode.Asphalt ? 1 : Math.Max(flatten,1-rutFade);
                if (weight <= 0) return originalHeight;
                float target = SurfaceHeight(hit, hit.lateral);
                if (mode == LTRoadMode.Offroad && pattern == LTRoadPattern.Tracks)
                    target -= rutDepth * TrackWeight(hit, x, z)*rutFade;
                else if(Varied&&settings.variation.solidRuts)
                    target -= rutDepth * TrackWeight(hit,x,z)*rutFade*VariationStrength(hit);
                // Offset never subtracts from originalHeight: repeat evaluation cannot accumulate an asphalt offset.
                return originalHeight + (target - originalHeight) * weight;
            }

            public float PaintWeight(float x, float z)
            {
                if(wheelLayerView)return WheelPaintWeight(x,z);
                if (!InsideBounds(x, z) || !TryInfluenceSample(x, z, out var hit)) return 0;
                float coverage=Coverage(hit,x,z);
                if(coverage<=0||!Varied||settings.variation.patchStrength<=0)return coverage;
                var v=settings.variation;
                // Road-local metres, not UV repeats or chunk coordinates. Large patches
                // reveal the already painted substrate; no extra layer slots/textures.
                float noise=Noise(hit.distance/v.patchSize,hit.lateral/v.patchSize,unchecked(v.seed^0x36a51));
                float patches=Smooth(Clamp01((noise-.25f)/.5f));
                float tracks=pattern==LTRoadPattern.Tracks||v.solidRuts?TrackWeight(hit,x,z):0;
                return coverage*(1-v.patchStrength*VariationStrength(hit)*patches*(1-.85f*tracks));
            }

            public float ClearWeight(float x, float z, LTDetailCategory category)
            {
                bool vegetation = clearVegetation && (category & LTDetailCategory.Vegetation) != 0;
                bool wheelVegetation=vegetation&&mode==LTRoadMode.Offroad&&settings.vegetationOnlyWheelTracks;
                bool stones = clearStones && (category & LTDetailCategory.Stones) != 0;
                bool wheelStones=mode==LTRoadMode.Offroad&&settings.wheelTracks.enabled&&settings.wheelTracks.clearStones&&(category&LTDetailCategory.Stones)!=0;
                if (!vegetation && !stones&&!wheelStones) return 0;
                // Paint-only patches must not grow vegetation or leave stones in the
                // carriageway, nor change the expensive placement/geometry signatures.
                if(!Varied&&!wheelStones&&!wheelVegetation)return PaintWeight(x,z)*(stones?1:vegetationFade);
                if(!InsideBounds(x,z)||!TryInfluenceSample(x,z,out var hit))return 0;
                float coverage=Coverage(hit,x,z),original=stones?coverage:0;
                if(vegetation)original=Math.Max(original,wheelVegetation?WheelVegetationCoverage(hit):coverage*vegetationFade);
                return wheelStones?Math.Max(original,WheelCoverage(hit,x,z)):original;
            }

            float WheelVegetationCoverage(Hit hit)
            {
                if(!settings.wheelTracks.enabled)return 0;
                // Keep roots out of the entire authored strip, including its soft
                // paint edge. Colour opacity/noise/rut weakening must not regrow plants.
                var w=settings.wheelTracks;
                float across=Math.Abs(Math.Abs(hit.lateral)-w.separation*.5f);
                float beyond=Math.Max(0,hit.radialDistance*hit.radialDistance-hit.lateral*hit.lateral);
                return across*across+beyond<=w.width*w.width*.25f?1:0;
            }

            public float WheelPaintWeight(float x,float z)
            {
                if(mode!=LTRoadMode.Offroad||!settings.wheelTracks.enabled||settings.wheelLayerId==0||settings.wheelTracks.strength<=0||
                    !InsideBounds(x,z)||!TryInfluenceSample(x,z,out var hit))return 0;
                return WheelCoverage(hit,x,z)*settings.wheelTracks.strength;
            }
            float WheelCoverage(Hit hit,float x,float z)
            {
                var w=settings.wheelTracks;
                float half=w.width*.5f*(1-edgeNoise*Noise(x/noiseSize,z/noiseSize,seed)*.8f);
                float across=Math.Abs(Math.Abs(hit.lateral)-w.separation*.5f);
                float beyond=Math.Max(0,hit.radialDistance*hit.radialDistance-hit.lateral*hit.lateral);
                float mask=Fade((float)Math.Sqrt(across*across+beyond),half*(1-w.softness),half*w.softness);
                if(mask<=0)return 0;
                mask*=Fade(hit.radialDistance,HalfWidth(hit),shoulderWidth);
                if(settings.straightStart)mask*=Smooth(Clamp01(hit.distance/Math.Max(2,settings.junctionStartLength)));
                if(settings.straightEnd)mask*=Smooth(Clamp01((length-hit.distance)/Math.Max(2,settings.junctionEndLength)));
                if(Varied&&settings.variation.rutVariation>0)
                {
                    var v=settings.variation;
                    float noise=Noise(hit.distance/v.rutLength,.43f,unchecked(v.seed^0x47591));
                    mask*=1-v.rutVariation*VariationStrength(hit)*Smooth(Clamp01((noise-.2f)/.6f));
                }
                return mask;
            }

            float Coverage(Hit hit,float x,float z)
            {
                float footprint=Fade(hit.radialDistance,HalfWidth(hit),shoulderWidth);
                if(mode!=LTRoadMode.Offroad||pattern!=LTRoadPattern.Tracks)return footprint;
                float tracks=TrackWeight(hit,x,z);
                return Varied&&settings.variation.widthAmount>0?tracks*footprint:tracks;
            }

            float VariationStrength(Hit hit)
            {
                if(!Varied)return 0;
                float envelope=1;
                // Keep both round caps and the entire straight junction neck unchanged.
                float start=settings.straightStart?settings.junctionStartLength:0;
                float end=settings.straightEnd?settings.junctionEndLength:0;
                envelope=Math.Min(envelope,Smooth(Clamp01((hit.distance-start)/Math.Max(2,start))));
                envelope=Math.Min(envelope,Smooth(Clamp01((length-hit.distance-end)/Math.Max(2,end))));
                return settings.variation.strength*hit.variationStrength*envelope;
            }

            /// <summary>Actual side width, shared by height, paint, clearing and scene preview.</summary>
            public float HalfWidth(Hit hit)
            {
                float half=width*.5f;
                if(!Varied||settings.variation.widthAmount<=0)return half;
                var v=settings.variation;float amount=VariationStrength(hit)*v.widthAmount;
                if(amount<=0)return half;
                float left=Noise(hit.distance/v.widthLength,.37f,unchecked(v.seed^0x7153));
                float right=Noise(hit.distance/v.widthLength,9.71f,unchecked(v.seed^0x19b5));
                float side=Smooth(Clamp01(.5f+hit.lateral/half));
                return half*(1+amount*(2*(left+(right-left)*side)-1));
            }

            float TrackWeight(Hit hit, float x, float z)
            {
                // Noise erodes the outer strip edges only. The configured central gap can never be filled.
                float half = rutWidth * .5f;
                float extent = half * (1 - edgeNoise * Noise(x / noiseSize, z / noiseSize, seed) * .8f);
                float across = Math.Abs(Math.Abs(hit.lateral) - rutSeparation * .5f);
                float beyond = Math.Max(0, hit.radialDistance * hit.radialDistance - hit.lateral * hit.lateral);
                float radial = (float)Math.Sqrt(across * across + beyond);
                float result=Fade(radial, extent * .65f, extent * .35f);
                if(result>0&&Varied&&settings.variation.rutVariation>0)
                {
                    var v=settings.variation;
                    float noise=Noise(hit.distance/v.rutLength,.43f,unchecked(v.seed^0x47591));
                    result*=1-v.rutVariation*VariationStrength(hit)*Smooth(Clamp01((noise-.2f)/.6f));
                }
                return result;
            }

            bool InsideBounds(float x, float z) => Finite(x) && Finite(z) &&
                x >= bounds.xMin && x <= bounds.xMax && z >= bounds.yMin && z <= bounds.yMax;

            /// <summary>Conservative influence intersection (includes shoulder and height blend).</summary>
            public bool Intersects(Rect rect)
            {
                if (!Finite(rect.xMin) || !Finite(rect.xMax) || !Finite(rect.yMin) || !Finite(rect.yMax)) return false;
                float minX = Math.Min(rect.xMin, rect.xMax), maxX = Math.Max(rect.xMin, rect.xMax);
                float minZ = Math.Min(rect.yMin, rect.yMax), maxZ = Math.Max(rect.yMin, rect.yMax);
                float radius = MaxHalfWidth + shoulderWidth + blendWidth;
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
            int profileHash=17;
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
                profileHash=Add(profileHash,point.VariationStrength);
                point.position=terrainFromRoad.MultiplyPoint3x4(point.position);source[i]=point;
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
            var result = new List<Sample> { new Sample { position = source[0].position, bank = source[0].bank,variationStrength=source[0].VariationStrength } };
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
                    source[i].bank, source[i + 1].bank, source[i].VariationStrength,source[i+1].VariationStrength,settings.sampleSpacing, 0, p2 - p1);
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
            return new Snapshot(result.ToArray(), settings, hash, profileHash);
        }

        static void Subdivide(List<Sample> result, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            float bankA, float bankD, float variationA,float variationD,float spacing, int depth, Vector3 chord)
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
                result.Add(new Sample { position = d, bank = bankD,variationStrength=variationD });
                return;
            }
            if (depth >= 20) throw Invalid("Road cannot meet sampleSpacing safely. Simplify points or increase sampleSpacing.");
            var ab = (a + b) * .5f; var bc = (b + c) * .5f; var cd = (c + d) * .5f;
            var abc = (ab + bc) * .5f; var bcd = (bc + cd) * .5f; var mid = (abc + bcd) * .5f;
            float bankMid = (bankA + bankD) * .5f;
            float variationMid=(variationA+variationD)*.5f;
            Subdivide(result, a, ab, abc, mid, bankA, bankMid, variationA,variationMid,spacing, depth + 1, chord);
            Subdivide(result, mid, bcd, cd, d, bankMid, bankD, variationMid,variationD,spacing, depth + 1, chord);
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

        static void Hashes(Settings s, int input, int profileHash, out int geometry, out int paint, out int detail)
        {
            int common = Add(Mix(input, (int)s.mode), s.sampleSpacing);
            common = Add(common, s.width); common = Add(common, s.shoulderWidth);
            var v=s.variation;bool varied=s.mode==LTRoadMode.Offroad&&v.enabled&&v.strength>0;
            int variationKey=Add(Mix(profileHash,v.seed),v.strength);
            if(varied&&v.widthAmount>0)common=Mix(common,Add(Add(variationKey,v.widthAmount),v.widthLength));
            int mask = common,trackMask=common;
            bool tracks = s.mode == LTRoadMode.Offroad && s.pattern == LTRoadPattern.Tracks;
            if (s.mode == LTRoadMode.Offroad) mask = Mix(mask, (int)s.pattern);
            if (tracks||varied&&v.solidRuts)
            {
                trackMask = Add(mask, s.rutWidth); trackMask = Add(trackMask, s.rutSeparation); trackMask = Add(trackMask, s.edgeNoise);
                if (s.edgeNoise > 0) { trackMask = Add(trackMask, s.noiseSize); trackMask = Mix(trackMask, s.seed); }
                if(varied&&v.rutVariation>0)trackMask=Mix(trackMask,Add(Add(variationKey,v.rutVariation),v.rutLength));
                if(tracks)mask=trackMask;
            }
            geometry = common;
            geometry = Add(geometry, s.blendWidth); geometry = Add(geometry, s.terrainCellSize);
            if (s.mode == LTRoadMode.Offroad)
            {
                geometry = Add(geometry, s.flatten);
                if ((s.flatten > 0||s.straightStart||s.straightEnd) && (tracks||varied&&v.solidRuts) && s.rutDepth > 0)
                {
                    geometry = Mix(geometry, trackMask); geometry = Add(geometry, s.rutDepth);
                    if(!tracks)geometry=Mix(geometry,variationKey);
                }
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
            if(s.mode==LTRoadMode.Offroad&&s.projection==LTRoadProjection.Spline&&s.textureAcrossMetres>0)
                paint=Add(paint,s.textureAcrossMetres);
            if(varied&&v.patchStrength>0)
            {
                paint=Mix(paint,Add(Add(variationKey,v.patchStrength),v.patchSize));
                if(!tracks&&v.solidRuts)paint=Mix(paint,trackMask);
            }
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
            if(point.overrideVariation)Range(point.variationStrength,0,1,"Point "+index+" variation strength");
        }
        static ArgumentException Invalid(string message) => new ArgumentException("LTRoad: " + message);
    }
}
