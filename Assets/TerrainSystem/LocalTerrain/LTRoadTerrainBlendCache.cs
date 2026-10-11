using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    // A world owns one sparse addressable view of the EXISTING terrain paint maps.
    // Layer texture arrays are borrowed, never copied/modified. Only 257² masks for
    // road-intersected chunks are GPU-copied. No color bake, readback or mesh edits.
    public sealed class LTRoadTerrainBlendCache : IDisposable
    {
        public readonly struct Tile
        {
            public readonly Vector2Int cell;
            public readonly Material material;
            public readonly long revision;
            public Tile(Vector2Int cell,Material material,long revision){this.cell=cell;this.material=material;this.revision=revision;}
        }
        [StructLayout(LayoutKind.Sequential)] struct ChunkData {public Vector4 rect,options,fastLayerParams;}
        [StructLayout(LayoutKind.Sequential)] struct LayerData {public Vector4 tiling,tint,settings,flags,indices,asphaltStyle;}
        sealed class Resources : IDisposable
        {
            public Texture2D lookup;
            public Texture2DArray weights,projection,damage;
            public Texture color,normal,mask,cracks;
            public ComputeBuffer chunks,layers;
            public Vector4 grid,cell;
            public long bytes;
            public void Dispose(){chunks?.Dispose();layers?.Dispose();Destroy(lookup);Destroy(weights);Destroy(projection);Destroy(damage);}
        }
        Resources current;
        string signature;
        public event Action Changed;
        public string Status {get;private set;}="Автоматический бленд не используется.";
        public bool Ready=>current!=null;
        public long Bytes=>current?.bytes??0;
        public const long MaxBytes=128L*1024*1024;
        const int Size=257;
        static int Id(UnityEngine.Object o)=>o?o.GetInstanceID():0;
        static void Destroy(UnityEngine.Object o){if(o){if(Application.isPlaying)UnityEngine.Object.Destroy(o);else UnityEngine.Object.DestroyImmediate(o);}}
        public void Dispose()
        {
            var old=current;current=null;signature=null;Status="Нет доступных масок terrain; отображается исходный асфальт.";
            // Unbind buffers BEFORE disposing them, also on world disable/reload.
            Changed?.Invoke();old?.Dispose();
        }
        public void Bind(MaterialPropertyBlock block)
        {
            if(!Ready){LTRoadTerrainFallback.Bind(block);return;}
            block.SetFloat("_LTRoadTerrainReady",1);
            block.SetVector("_LTRoadTerrainGrid",current.grid);block.SetVector("_LTRoadTerrainCell",current.cell);
            block.SetTexture("_LTRoadTerrainLookup",current.lookup);block.SetTexture("_LTRoadTerrainWeights",current.weights);
            block.SetTexture("_LTRoadTerrainProjection",current.projection);block.SetTexture("_LTRoadTerrainDamage",current.damage);
            block.SetTexture("_LTRoadTerrainColor",current.color);block.SetTexture("_LTRoadTerrainNormal",current.normal);block.SetTexture("_LTRoadTerrainMask",current.mask);
            block.SetTexture("_LTRoadTerrainCracks",current.cracks?current.cracks:LTRoadTerrainFallback.EmptyArray);
            block.SetBuffer("_LTRoadTerrainChunks",current.chunks);block.SetBuffer("_LTRoadTerrainLayers",current.layers);
        }
        public void Synchronize(Vector2 cellSize,List<Tile> tiles)
        {
            if(tiles.Count==0){if(current!=null||signature!=null)Dispose();return;}
            tiles.Sort((a,b)=>a.cell.y!=b.cell.y?a.cell.y.CompareTo(b.cell.y):a.cell.x.CompareTo(b.cell.x));
            var key=new System.Text.StringBuilder().Append(cellSize.x.ToString("R")).Append('/').Append(cellSize.y.ToString("R"));
            foreach(var t in tiles)key.Append('|').Append(t.cell).Append(':').Append(t.revision).Append(':').Append(Id(t.material)).Append(':').Append(t.material?t.material.ComputeCRC():0);
            string next=key.ToString();if(signature==next)return;
            Resources built=null;
            try
            {
                built=Build(cellSize,tiles);var old=current;current=built;built=null;signature=next;
                Status=$"Авто terrain: {tiles.Count} чанков; маски {Bytes/(1024f*1024f):F1} МиБ. Текстуры слоёв общие с terrain.";
                Changed?.Invoke();old?.Dispose();
            }
            catch(Exception e)
            {
                built?.Dispose();Dispose();signature=next;
                Status="Авто terrain недоступен: "+e.Message+" Исходный асфальт сохранён.";
            }
        }
        static Texture2DArray Array(string name,int count,TextureFormat format)
        {
            int size=count>0?Size:1;
            return new Texture2DArray(size,size,Math.Max(1,count),format,false,true)
                {name=name,hideFlags=HideFlags.HideAndDontSave,wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear};
        }
        static Texture Texture(Material m,string name)=>m.HasProperty(name)?m.GetTexture(name):null;
        static bool SameSampling(Texture a,Texture b)=>a.filterMode==b.filterMode&&a.anisoLevel==b.anisoLevel&&
            a.wrapModeU==b.wrapModeU&&a.wrapModeV==b.wrapModeV&&a.wrapModeW==b.wrapModeW&&a.mipMapBias==b.mipMapBias;
        static Resources Build(Vector2 cellSize,List<Tile> tiles)
        {
            if(!(cellSize.x>0&&cellSize.y>0)||!float.IsFinite(cellSize.x)||!float.IsFinite(cellSize.y))throw new InvalidOperationException("Некорректный размер чанка.");
            if(!SystemInfo.supports2DArrayTextures||(SystemInfo.copyTextureSupport&CopyTextureSupport.DifferentTypes)==0)
                throw new InvalidOperationException("GPU не поддерживает копирование карт в Texture2DArray.");
            int minX=int.MaxValue,minZ=int.MaxValue,maxX=int.MinValue,maxZ=int.MinValue,projections=0,damages=0;
            var cells=new HashSet<Vector2Int>();
            foreach(var t in tiles)
            {
                if(!t.material||!cells.Add(t.cell))throw new InvalidOperationException("Отсутствующий материал или повтор координат чанка.");
                minX=Math.Min(minX,t.cell.x);minZ=Math.Min(minZ,t.cell.y);maxX=Math.Max(maxX,t.cell.x);maxZ=Math.Max(maxZ,t.cell.y);
                if(Texture(t.material,"_LTRoadProjectionMap") is Texture2DArray p)projections+=p.depth;
                if(Texture(t.material,"_LTAsphaltDamage") is Texture2DArray d)damages+=d.depth;
            }
            long width=(long)maxX-minX+1,height=(long)maxZ-minZ+1;
            long bytes=(long)Size*Size*(tiles.Count*12L+projections*16L+damages*4L)+width*height*4+tiles.Count*(48+12*96);
            int maxSlices=SystemInfo.maxTextureArraySlices;
            if(bytes>MaxBytes||width*height>1048576||width>SystemInfo.maxTextureSize||height>SystemInfo.maxTextureSize||
                tiles.Count*3>maxSlices||projections>maxSlices||damages>maxSlices)
                throw new InvalidOperationException("Превышен лимит адресуемых масок (128 МиБ). Уменьшите число дорог с автоматическим блендом.");
            var r=new Resources{bytes=bytes,grid=new Vector4(minX,minZ,width,height),cell=new Vector4(cellSize.x,cellSize.y,0,0)};
            try
            {
                var first=tiles[0].material;r.color=Texture(first,"_LTColorArray");r.normal=Texture(first,"_LTNormalArray");r.mask=Texture(first,"_LTMaskArray");r.cracks=Texture(first,"_LTAsphaltCracks");
                if(!r.color||!r.normal||!r.mask)throw new InvalidOperationException("Массивы слоёв terrain ещё не готовы.");
                if(!SameSampling(r.color,r.normal)||!SameSampling(r.color,r.mask))
                    throw new InvalidOperationException("Массивы цвета, нормалей и масок terrain должны иметь одинаковую фильтрацию для общего sampler.");
                r.weights=Array("Road terrain weights",tiles.Count*3,TextureFormat.RGBA32);
                r.projection=Array("Road terrain spline coordinates",projections,TextureFormat.RGBAFloat);
                r.damage=Array("Road terrain asphalt influence",damages,TextureFormat.RGBA32);
                var chunks=new ChunkData[tiles.Count];var layers=new LayerData[tiles.Count*12];var lookup=new float[width*height];
                int projectionOffset=0,damageOffset=0;
                void Copy(Texture source,int from,Texture2DArray target,int to)
                {
                    if(!source||source.width!=Size||source.height!=Size||source.graphicsFormat!=target.graphicsFormat)
                        throw new InvalidOperationException("Несовместимый размер/формат карты terrain.");
                    Graphics.CopyTexture(source,from,0,target,to,0);
                }
                for(int i=0;i<tiles.Count;i++)
                {
                    var t=tiles[i];var m=t.material;
                    if(Texture(m,"_LTColorArray")!=r.color||Texture(m,"_LTNormalArray")!=r.normal||Texture(m,"_LTMaskArray")!=r.mask)
                        throw new InvalidOperationException("Разные палитры terrain; ожидается завершение обновления.");
                    var cracks=Texture(m,"_LTAsphaltCracks");if(cracks){if(r.cracks&&r.cracks!=cracks)throw new InvalidOperationException("Разные массивы трещин.");r.cracks=cracks;}
                    lookup[(t.cell.y-minZ)*width+t.cell.x-minX]=i+1;
                    // A negative budget selects the full sampler; zero is a valid fast budget.
                    chunks[i]=new ChunkData{rect=m.GetVector("_LTRect"),options=new Vector4(m.GetFloat("_LTHeightBlend"),m.GetFloat("_LTTriplanar"),m.GetFloat("_LTBaseOnly"),m.GetFloat("_LTRoadProjectionEnabled")),
                        fastLayerParams=m.IsKeywordEnabled("_LT_FAST_LAYERS")?m.GetVector("_LTFastLayerParams"):new Vector4(-1,0,0,0)};
                    for(int k=0;k<3;k++)Copy(Texture(m,"_LTWeights"+k),0,r.weights,i*3+k);
                    for(int s=0;s<12;s++)
                    {
                        float slice=m.GetVector("_LTLayerSlices"+(s/4))[s%4];float road=m.GetVector("_LTRoadProjectionSlots"+(s/4))[s%4];
                        if(road!=0)road=Mathf.Sign(road)*(Mathf.Abs(road)+projectionOffset);
                        var style=m.GetVector("_LTAsphaltStyle"+s);if(style.x>.5f)style.x+=damageOffset;
                        layers[i*12+s]=new LayerData{tiling=m.GetVector("_LTTiling"+s),tint=m.GetVector("_LTTint"+s),settings=m.GetVector("_LTSettings"+s),flags=m.GetVector("_LTFlags"+s),indices=new Vector4(slice,road,0,0),asphaltStyle=style};
                    }
                    if(Texture(m,"_LTRoadProjectionMap") is Texture2DArray p){for(int s=0;s<p.depth;s++)Copy(p,s,r.projection,projectionOffset+s);projectionOffset+=p.depth;}
                    if(Texture(m,"_LTAsphaltDamage") is Texture2DArray d){for(int s=0;s<d.depth;s++)Copy(d,s,r.damage,damageOffset+s);damageOffset+=d.depth;}
                }
                r.lookup=new Texture2D((int)width,(int)height,TextureFormat.RFloat,false,true){name="Road terrain chunk addresses",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
                r.lookup.SetPixelData(lookup,0);r.lookup.Apply(false,true);
                r.chunks=new ComputeBuffer(chunks.Length,48);r.chunks.SetData(chunks);
                r.layers=new ComputeBuffer(layers.Length,96);r.layers.SetData(layers);
                return r;
            }
            catch{r.Dispose();throw;}
        }
    }

    public sealed partial class LTPaintRuntime
    {
        internal readonly LTRoadTerrainBlendCache RoadTerrain=new LTRoadTerrainBlendCache();
        void TickRoadTerrain(LTWorld world)
        {
            var regions=LTRoadGroundBlend.Regions(world);
            if(regions.Count==0){if(RoadTerrain.Ready)RoadTerrain.Dispose();return;}
            var tiles=new List<LTRoadTerrainBlendCache.Tile>();var size=world.ChunkSize;
            foreach(var pair in chunks)
            {
                var chunk=pair.Key;var state=pair.Value;
                if(!chunk||!chunk.isActiveAndEnabled||!state.ready||!state.material)continue;
                var rect=new Rect(chunk.x*size.x,chunk.z*size.y,size.x,size.y);
                foreach(var area in regions)if(Touches(area,rect))
                {tiles.Add(new LTRoadTerrainBlendCache.Tile(new Vector2Int(chunk.x,chunk.z),state.material,state.grassColorRevision));break;}
            }
            RoadTerrain.Synchronize(size,tiles);
        }
    }
}
