using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // CPU snapshots of the same endpoint coverage maps and height-blend rules as the material.
    // Height reads deliberately use mip 0: placement must not change with camera derivatives.
    internal sealed class LTDetailSurface
    {
        internal sealed class Image
        {
            public Color32[] pixels;
            public int width,height;
            public Image(Texture2D texture){pixels=texture.GetPixels32(0);width=texture.width;height=texture.height;}
            public Color Sample(float u,float v,bool repeat)=>LTPaintMath.SampleGpuBilinear(pixels,width,height,u,v,repeat);
        }
        internal sealed class Tile
        {
            public Rect rect;
            public int coverage,surface;
            public LTSurfaceLayer[] layers;
            public Image weights0,weights1,weights2;
            public LTPaintRuntime.RoadProjectionSnapshot roadProjection;
        }
        internal sealed class Layer
        {
            public Image mask;
            public Vector2 size,offset;
            public float strength,heightOffset;
        }
        public readonly List<Tile> tiles=new List<Tile>();
        public readonly Dictionary<LTSurfaceLayer,Layer> layers=new Dictionary<LTSurfaceLayer,Layer>();
        readonly float[] raw=new float[LTPaintRuntime.LayerCapacity],working=new float[LTPaintRuntime.LayerCapacity],heights=new float[LTPaintRuntime.LayerCapacity];
        public bool triplanar,lightweight;
        public float blend;
        public void Weights(Tile tile,Vector3 p,Vector3 normal,float[] result)
        {
            System.Array.Clear(result,0,result.Length);
            if(tile.layers.Length==1){result[0]=1;return;}
            float u=((p.x-tile.rect.xMin)/tile.rect.width*256+.5f)/257;
            float v=((p.z-tile.rect.yMin)/tile.rect.height*256+.5f)/257;
            var a=tile.weights0.Sample(u,v,false);var b=tile.weights1.Sample(u,v,false);
            var c=tile.weights2!=null?tile.weights2.Sample(u,v,false):Color.clear;
            for(int i=0;i<tile.layers.Length;i++)raw[i]=i<4?a[i]:i<8?b[i-4]:c[i-8];
            var axisWeights=triplanar?new Vector3(Mathf.Pow(normal.x,4),Mathf.Pow(normal.y,4),Mathf.Pow(normal.z,4)):Vector3.up;
            axisWeights/=Mathf.Max(.00001f,axisWeights.x+axisWeights.y+axisWeights.z);
            for(int axis=0;axis<3;axis++)
            {
                if(axisWeights[axis]<=.00001f)continue;
                var uv=axis==0?new Vector2(p.z,p.y):axis==1?new Vector2(p.x,p.z):new Vector2(p.x,p.y);
                for(int i=0;i<tile.layers.Length;i++)
                {
                    var layer=layers[tile.layers[i]];working[i]=raw[i];float h=.5f;
                    bool basic=i==0&&lightweight;
                    if(!basic&&layer.mask!=null&&raw[i]>.00001f)
                    {
                        var textureUV=new Vector2((uv.x+layer.offset.x)/Mathf.Max(.001f,layer.size.x),
                            (uv.y+layer.offset.y)/Mathf.Max(.001f,layer.size.y));
                        if(tile.roadProjection!=null&&tile.roadProjection.TrySample(i,p.x,p.z,out var roadUV,out _))
                            textureUV=roadUV;
                        h=layer.mask.Sample(textureUV.x,textureUV.y,true).g;
                    }
                    heights[i]=basic?.5f:Mathf.Clamp01((h-.5f)*layer.strength+.5f+layer.heightOffset);
                }
                LTDetailMath.HeightBlend(working,heights,tile.layers.Length,blend);
                for(int i=0;i<tile.layers.Length;i++)result[i]+=working[i]*axisWeights[axis];
            }
        }
    }

    public sealed partial class LTPaintRuntime
    {
        internal void ReleaseDetailSnapshots()
        {
            foreach(var state in chunks.Values)state.detailSnapshot=null;
            foreach(var mask in masks.Values)mask.detailImage=null;
        }
        internal LTDetailSurface CaptureDetailSurface(LTWorld world,HashSet<Vector2Int> chunkKeys)
        {
            var result=new LTDetailSurface{triplanar=world.triplanarTexturing,lightweight=world.lightweightBackground,blend=world.layerHeightBlend};
            var images=new Dictionary<Texture2D,LTDetailSurface.Image>();
            foreach(var pair in chunks)
            {
                var state=pair.Value;
                if(!pair.Key||!chunkKeys.Contains(new Vector2Int(pair.Key.x,pair.Key.z)))
                {state.detailSnapshot=null;continue;}
                if(!pair.Key||!state.ready||state.detailLayers==null||!state.weights0||!state.weights1||!state.weights2)continue;
                var tile=state.detailSnapshot;
                var roadProjection=CaptureRoadProjection(state);
                int detailCoverage=roadProjection==null?state.coverageHash:Mix(state.coverageHash,roadProjection.hash);
                if(tile==null||tile.coverage!=detailCoverage||tile.surface!=state.surfaceHash||
                    !ReferenceEquals(tile.roadProjection,roadProjection))
                {
                    float w=world.source.size.x/world.chunksX,d=world.source.size.z/world.chunksZ;
                    tile=new LTDetailSurface.Tile{rect=new Rect(pair.Key.x*w,pair.Key.z*d,w,d),
                        coverage=detailCoverage,surface=state.surfaceHash,layers=state.detailLayers,roadProjection=roadProjection,
                        weights0=new LTDetailSurface.Image(state.weights0),weights1=new LTDetailSurface.Image(state.weights1),
                        weights2=new LTDetailSurface.Image(state.weights2)};
                    state.detailSnapshot=tile;
                }
                result.tiles.Add(tile);
                foreach(var layer in tile.layers)
                {
                    if(result.layers.ContainsKey(layer))continue;
                    LTDetailSurface.Image image=null;
                    if(layer.maskMap&&world.layerHeightBlend>.0001f)
                    {
                        if(!images.TryGetValue(layer.maskMap,out image))
                        {
                            var copy=ReadMask(layer.maskMap);
                            var cache=masks[layer.maskMap];
                            if(cache.detailImage==null)cache.detailImage=new LTDetailSurface.Image(copy);
                            image=cache.detailImage;images.Add(layer.maskMap,image);
                        }
                    }
                    result.layers.Add(layer,new LTDetailSurface.Layer{mask=image,size=layer.tileSizeMetres,offset=layer.tileOffsetMetres,
                        strength=layer.heightStrength,heightOffset=layer.heightOffset});
                }
            }
            result.tiles.Sort((a,b)=>a.rect.y!=b.rect.y?a.rect.y.CompareTo(b.rect.y):a.rect.x.CompareTo(b.rect.x));
            foreach(var pair in masks)if(!images.ContainsKey(pair.Key))pair.Value.detailImage=null;
            return result;
        }
    }
}
