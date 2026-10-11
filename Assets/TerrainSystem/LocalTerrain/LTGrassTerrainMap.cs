using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    // Automatic unlit albedo cache, independent of the user-requested far bake.
    // No readback, scene objects, material assets or per-instance buffer changes.
    public sealed class LTGrassTerrainMap : IDisposable
    {
        public const string Strength="_LTGrassTerrainStrength",Height="_LTGrassTerrainHeight";
        public const string Map="_LTGrassTerrainMap",Ready="_LTGrassTerrainReady",U="_LTGrassTerrainU",V="_LTGrassTerrainV";
        public struct Tile : IEquatable<Tile>
        {
            public int id;
            public long revision;
            public Rect rect;
            public Material material;
            public Mesh mesh;
            public double geometryRevision;
            public Matrix4x4 localToWorld,localToTerrain;
            public bool Equals(Tile b)=>id==b.id&&revision==b.revision&&rect.Equals(b.rect)&&material==b.material&&mesh==b.mesh&&
                geometryRevision==b.geometryRevision&&localToWorld.Equals(b.localToWorld)&&localToTerrain.Equals(b.localToTerrain);
        }
        readonly Dictionary<int,Tile> applied=new Dictionary<int,Tile>();
        readonly HashSet<int> membership=new HashSet<int>();
        readonly List<Tile> pending=new List<Tile>();
        readonly int limit;
        RenderTexture map;
        Material baker;
        Rect extent;
        Matrix4x4 worldToLocal;
        bool triplanar;
        int next;
        public Texture Texture=>map;
        public int Pending=>pending.Count-next;
        public int TilesRendered{get;private set;}
        public string Status{get;private set;}="Оттенок от земли выключен.";
        public LTGrassTerrainMap(int maximumResolution=4096){limit=Mathf.Clamp(maximumResolution,32,4096);}
        public static bool Requested(Material material)=>material&&material.HasProperty(Strength)&&material.GetFloat(Strength)>0;

        public void Synchronize(Rect area,Matrix4x4 inverse,List<Tile> tiles,bool useTriplanar)
        {
            try
            {
                if(tiles.Count==0||area.width<=0||area.height<=0){Dispose();return;}
                if(!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB32))throw new InvalidOperationException("ARGB32 недоступен.");
                var shader=Resources.Load<Shader>("LTGlobalLayerBake");
                if(!shader||!shader.isSupported)throw new InvalidOperationException("Шейдер цвета земли ещё не готов.");
                if(!baker)baker=new Material(shader){hideFlags=HideFlags.HideAndDontSave,name="Grass terrain color baker"};
                int max=Mathf.Min(limit,SystemInfo.maxTextureSize);
                int width=Mathf.Clamp(Mathf.CeilToInt(area.width*2),32,max),height=Mathf.Clamp(Mathf.CeilToInt(area.height*2),32,max);
                bool recreate=!map||!map.IsCreated()||map.width!=width||map.height!=height;
                bool reset=recreate||!extent.Equals(area)||triplanar!=useTriplanar||!worldToLocal.Equals(inverse)||membership.Count!=tiles.Count;
                foreach(var tile in tiles)if(!membership.Contains(tile.id))reset=true;
                if(recreate)
                {
                    Destroy(map);map=null;
                    map=new RenderTexture(width,height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear)
                    {name="Grass terrain albedo (automatic)",hideFlags=HideFlags.HideAndDontSave,useMipMap=false,
                        filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
                    if(!map.Create())throw new InvalidOperationException("Не удалось создать карту цвета земли.");
                }
                extent=area;worldToLocal=inverse;triplanar=useTriplanar;
                if(reset)
                {
                    applied.Clear();membership.Clear();foreach(var tile in tiles)membership.Add(tile.id);
                    // Alpha zero = unavailable, not black ground. Deleted/sparse cells
                    // and not-yet-built tiles leave the original grass unchanged.
                    using(var commands=new CommandBuffer{name="Clear grass terrain albedo"})
                    {commands.SetRenderTarget(map);commands.ClearRenderTarget(false,true,Color.clear);Graphics.ExecuteCommandBuffer(commands);}
                }
                pending.Clear();next=0;
                foreach(var tile in tiles)if(!applied.TryGetValue(tile.id,out var previous)||!previous.Equals(tile))pending.Add(tile);
                UpdateStatus();
            }
            catch(Exception e){Fail(e);}
        }
        public void Advance(double budgetMs=2,int maxTiles=4)
        {
            if(!map||!baker||Pending==0)return;
            long start=System.Diagnostics.Stopwatch.GetTimestamp();int count=0;
            try
            {
                while(Pending>0&&count++<maxTiles)
                {
                    var tile=pending[next++];if(!tile.material)continue;
                    if(triplanar&&(!tile.mesh||!tile.mesh.HasVertexAttribute(VertexAttribute.Normal)||!tile.mesh.HasVertexAttribute(VertexAttribute.Tangent)))continue;
                    Bake(tile);applied[tile.id]=tile;TilesRendered++;
                    if((System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency>=budgetMs)break;
                }
                UpdateStatus();
            }
            catch(Exception e){Fail(e);}
        }
        void Bake(Tile tile)
        {
            baker.CopyPropertiesFromMaterial(tile.material);baker.shaderKeywords=Array.Empty<string>();
            baker.SetVector("_LTGlobalRect",new Vector4(extent.xMin,extent.yMin,extent.width,extent.height));
            int x0=Mathf.Clamp(Mathf.RoundToInt((tile.rect.xMin-extent.xMin)/extent.width*map.width),0,map.width);
            int x1=Mathf.Clamp(Mathf.RoundToInt((tile.rect.xMax-extent.xMin)/extent.width*map.width),0,map.width);
            int y0=Mathf.Clamp(Mathf.RoundToInt((tile.rect.yMin-extent.yMin)/extent.height*map.height),0,map.height);
            int y1=Mathf.Clamp(Mathf.RoundToInt((tile.rect.yMax-extent.yMin)/extent.height*map.height),0,map.height);
            if(x1<=x0||y1<=y0)return;
            baker.SetVector("_LTBakeRect",new Vector4((float)x0/map.width,(float)y0/map.height,(float)(x1-x0)/map.width,(float)(y1-y0)/map.height));
            baker.SetMatrix("_LTBakeLocalToTerrain",tile.localToTerrain);
            baker.SetMatrix("_LTBakeLocalToWorld",tile.localToWorld);
            baker.SetMatrix("_LTBakeNormalToWorld",tile.localToWorld.inverse.transpose);
            baker.SetMatrix("_LTSurfaceWorldToLocal",worldToLocal);
            using(var commands=new CommandBuffer{name="Update grass terrain albedo tile"})
            {
                commands.SetRenderTarget(map);commands.SetViewport(new Rect(0,0,map.width,map.height));
                commands.EnableScissorRect(new Rect(x0,y0,x1-x0,y1-y0));
                // Shared near-layer shader: textures, tints, height blend and spline UVs.
                commands.DrawProcedural(Matrix4x4.identity,baker,0,MeshTopology.Triangles,6);
                if(triplanar)for(int sub=0;sub<tile.mesh.subMeshCount;sub++)commands.DrawMesh(tile.mesh,Matrix4x4.identity,baker,sub,3);
                commands.DisableScissorRect();Graphics.ExecuteCommandBuffer(commands);
            }
        }
        void UpdateStatus()
        {
            Status=map?$"Цвет земли: {applied.Count}/{membership.Count} чанков; {map.width}×{map.height}, шаг ≈ {Mathf.Max(extent.width/map.width,extent.height/map.height):0.##} м. "+
                (Pending>0?"Обновляется автоматически…":"Карта актуальна."):"Ожидание цвета земли.";
            if(Pending==0&&applied.Count<membership.Count)Status+=" Не все чанки готовы (нужны LOD0, нормали и тангенты для трипланара).";
        }
        void Fail(Exception e){Dispose();Status="Оттенок от земли: "+e.Message;Debug.LogWarning(Status);}
        public static void Bind(MaterialPropertyBlock block,LTGrassTerrainMap colors)
            =>CaptureBinding(colors).Apply(block);
        public static LTGrassTerrainBinding CaptureBinding(LTGrassTerrainMap colors)
        {
            bool ready=colors!=null&&colors.map&&colors.map.IsCreated();
            Vector4 u=Vector4.zero,v=Vector4.zero;
            if(ready)
            {
                u=colors.worldToLocal.GetRow(0)/colors.extent.width;u.w-=colors.extent.xMin/colors.extent.width;
                v=colors.worldToLocal.GetRow(2)/colors.extent.height;v.w-=colors.extent.yMin/colors.extent.height;
            }
            return new LTGrassTerrainBinding(ready?colors.map:Texture2D.blackTexture,ready,u,v);
        }
        public void Dispose()
        {
            Destroy(map);Destroy(baker);map=null;baker=null;pending.Clear();applied.Clear();membership.Clear();next=0;
            Status="Оттенок от земли выключен.";
        }
        static void Destroy(UnityEngine.Object obj)
        {if(obj){if(Application.isPlaying)UnityEngine.Object.Destroy(obj);else UnityEngine.Object.DestroyImmediate(obj);}}
    }
}
