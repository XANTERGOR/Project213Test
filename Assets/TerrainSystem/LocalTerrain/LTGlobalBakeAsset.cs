using UnityEngine;

namespace LocalTerrainPrototype
{
    // One persistent snapshot with two linear texture sub-assets. Runtime never destroys these.
    public sealed class LTGlobalBakeAsset : ScriptableObject
    {
        public Texture2D albedo,normal;
        public Vector3 worldSize;
        public int chunksX,chunksZ,resolution;
        public string signature;
        public bool triplanar;
        public bool MatchesLayout(LTWorld world)=>world.source&&worldSize==world.source.size&&chunksX==world.chunksX&&chunksZ==world.chunksZ;

#if UNITY_EDITOR
        public static string Signature(LTWorld world)
        {
            var text=new System.Text.StringBuilder("LT global bake v3;");
            text.Append("layer-arrays-v1:").Append(LTPaintRuntime.LayerCapacity).Append(';');
            text.Append((int)world.arrayColorResolution).Append('/').Append((int)world.arrayNormalResolution).Append('/').Append((int)world.arrayMaskResolution).Append(';');
            text.Append(world.triplanarTexturing?"triplanar;":"planar;");
            void Number(float value)=>text.Append(value.ToString("R",System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            void Reference(Object value)
            {
                if(!value){text.Append("null;");return;}
                string path=UnityEditor.AssetDatabase.GetAssetPath(value);
                if(UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value,out string guid,out long localId))
                    text.Append(guid).Append(':').Append(localId);
                else text.Append("transient:").Append(value.GetInstanceID());
                if(value is Texture2D texture)text.Append(':').Append(texture.imageContentsHash);
                if(!string.IsNullOrEmpty(path))text.Append(':').Append(UnityEditor.AssetDatabase.GetAssetDependencyHash(path));
                text.Append(';');
            }
            void Layer(LTSurfaceLayer layer)
            {
                // Explicit properties also detect edits not yet saved to disk.
                if(!layer){text.Append("no-layer;");return;}
                // Identity matters: two identical assets occupy separate layer slots.
                if(UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(layer,out string guid,out long localId))
                    text.Append(guid).Append(':').Append(localId).Append(';');
                else text.Append("transient-layer:").Append(layer.GetInstanceID()).Append(';');
                Reference(layer.baseColorMap);Reference(layer.normalMap);Reference(layer.maskMap);
                Number(layer.tint.r);Number(layer.tint.g);Number(layer.tint.b);Number(layer.tint.a);
                Number(layer.normalStrength);Number(layer.aoStrength);Number(layer.heightStrength);Number(layer.heightOffset);
                Number(layer.metallic);Number(layer.smoothness);Number(layer.tileSizeMetres.x);Number(layer.tileSizeMetres.y);
                Number(layer.tileOffsetMetres.x);Number(layer.tileOffsetMetres.y);
            }
            if(world.source){Number(world.source.size.x);Number(world.source.size.y);Number(world.source.size.z);}
            Number(world.chunksX);Number(world.chunksZ);Number(world.globalLayerResolution);Number(world.layerHeightBlend);
            Layer(world.baseLayer);
            text.Append(world.lightweightBackground?"light-background;":"full-background;");
            foreach(var stamp in world.CollectPaintStamps())
            {
                if(!stamp.ActiveForPaint||!stamp.EffectiveLayer||(!stamp.Road&&stamp.strength<=0))continue;
                Layer(stamp.EffectiveLayer);
                var road=stamp.Road;
                if(road)
                {
                    // Serialized authoring values, never instance IDs or generated ownership fields.
                    text.Append("road;");
                    foreach(var point in road.points){Number(point.position.x);Number(point.position.y);Number(point.position.z);Number(point.bank);}
                    Number((int)road.mode);Number((int)road.pattern);Number((int)road.projection);
                    Number(road.width);Number(road.shoulderWidth);Number(road.blendWidth);Number(road.rutWidth);Number(road.rutSeparation);
                    Number(road.edgeNoise);Number(road.noiseSize);Number(road.seed);Number(road.sampleSpacing);
                    Number(road.textureRepeatMetres);Number(road.textureOffset.x);Number(road.textureOffset.y);
                    var roadMatrix=world.transform.worldToLocalMatrix*road.transform.localToWorldMatrix;
                    for(int i=0;i<16;i++)Number(roadMatrix[i]);
                    continue;
                }
                Reference(stamp.mask);Number((int)stamp.shape);Number(stamp.size.x);Number(stamp.size.y);
                Number(stamp.strength);Number(stamp.edgeFalloff);
                text.Append(JsonUtility.ToJson(stamp.heightFilter)).Append(';');
                text.Append(JsonUtility.ToJson(stamp.slopeFilter)).Append(';');
                text.Append(JsonUtility.ToJson(stamp.curveFilter)).Append(';');Number(stamp.curveRadius);
                if(stamp.noise.enabled)
                {
                    text.Append("noise;").Append(stamp.noise.seed).Append(';');
                    Number(stamp.noise.size);Number(stamp.noise.strength);Number(stamp.noise.threshold);Number(stamp.noise.softness);
                }
                var matrix=world.transform.worldToLocalMatrix*stamp.transform.localToWorldMatrix;
                for(int i=0;i<16;i++)Number(matrix[i]);
            }
            Reference(Resources.Load<Shader>("LTGlobalLayerBake"));
            if(world.triplanarTexturing&&world.generatedRoot)
                foreach(var chunk in world.generatedRoot.GetComponentsInChildren<LTChunk>())
                {
                    Reference(chunk.mesh);
                    var matrix=world.transform.worldToLocalMatrix*chunk.transform.localToWorldMatrix;
                    for(int i=0;i<16;i++)Number(matrix[i]);
                }
            text.Append(world.paintTerrainSignature);
            return Hash128.Compute(text.ToString()).ToString();
        }
#endif
    }
}
