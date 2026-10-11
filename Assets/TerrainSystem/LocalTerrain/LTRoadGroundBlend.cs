using UnityEngine;
using System.Collections.Generic;

namespace LocalTerrainPrototype
{
    // Only generated renderers own this binding. No material assets are edited at
    // runtime; the same distance texture/materials remain bound for every mesh LOD.
    [ExecuteAlways, DisallowMultipleComponent, AddComponentMenu("")]
    public sealed class LTRoadGroundBlend : MonoBehaviour
    {
        public LTRoad owner;
        public LTRoadJunction junction;
        LTWorld World=>owner?owner.World:junction?junction.World:null;
        Transform Source=>owner?owner.transform:junction?junction.transform:null;
        bool SourceActive=>owner?owner.isActiveAndEnabled:junction&&junction.isActiveAndEnabled;
        bool Automatic=>owner?owner.AutomaticGroundBlend:junction&&junction.AutomaticGroundBlend;
        public Matrix4x4 roadToMask=Matrix4x4.identity;
        MaterialPropertyBlock block;
        MeshRenderer target;
        Matrix4x4 lastRoad,lastGround;
        bool valid;
        LTRoadTerrainBlendCache cache;
        static readonly HashSet<LTRoadGroundBlend> active=new HashSet<LTRoadGroundBlend>();
        void OnEnable(){active.Add(this);valid=false;Refresh();}
        void OnDisable()
        {
            active.Remove(this);if(cache!=null)cache.Changed-=Invalidate;cache=null;
            // Keep the road drawable even when only this component is disabled.
            // Replace borrowed world SRVs before their resources can be released.
            BindFallback();valid=false;
        }
        void OnDestroy(){if(target)target.SetPropertyBlock(null);} // Global safe defaults remain alive.
        void BindFallback()
        {
            if(!target)target=GetComponent<MeshRenderer>();if(!target)return;
            if(block==null)block=new MaterialPropertyBlock();block.Clear();
            LTRoadTerrainFallback.Bind(block);block.SetVector("_LTRoadBlendParams",Vector4.zero);target.SetPropertyBlock(block);
        }
        void Invalidate()
        {
            valid=false;
            if(!Source||!World){BindFallback();return;}
            Refresh();
        }
        void LateUpdate()=>Refresh();
        internal static List<Rect> Regions(LTWorld world)
        {
            var result=new List<Rect>();
            foreach(var b in active)
            {
                if(!b||!b.isActiveAndEnabled||!b.SourceActive||!b.Automatic||b.World!=world)continue;
                var lod=b.GetComponent<LTRoadLOD>();if(!lod||lod.meshes==null)continue;
                var matrix=world.transform.worldToLocalMatrix*b.transform.localToWorldMatrix;
                var min=new Vector2(float.PositiveInfinity,float.PositiveInfinity);var max=-min;
                // Union every authored LOD's bounds; switching LOD never evicts maps.
                foreach(var mesh in lod.meshes)if(mesh)
                {
                    var bounds=mesh.bounds;
                    for(int k=0;k<8;k++)
                    {
                        var p=matrix.MultiplyPoint3x4(bounds.center+Vector3.Scale(bounds.extents,new Vector3((k&1)==0?-1:1,(k&2)==0?-1:1,(k&4)==0?-1:1)));
                        min=Vector2.Min(min,new Vector2(p.x,p.z));max=Vector2.Max(max,new Vector2(p.x,p.z));
                    }
                }
                if(float.IsFinite(min.x))result.Add(Rect.MinMaxRect(min.x-.001f,min.y-.001f,max.x+.001f,max.y+.001f));
            }
            return result;
        }
        public void Refresh()
        {
            if(!Source||!World)
            {
                if(cache!=null)cache.Changed-=Invalidate;cache=null;
                if(valid||block==null){BindFallback();valid=false;}return;
            }
            if(!target)target=GetComponent<MeshRenderer>();if(!target)return;
            var next=Automatic&&World.isActiveAndEnabled?World.DetailPaintRuntime?.RoadTerrain:null;
            if(next!=cache)
            {if(cache!=null)cache.Changed-=Invalidate;cache=next;if(cache!=null)cache.Changed+=Invalidate;valid=false;}
            var roadMatrix=roadToMask*Source.worldToLocalMatrix;
            var groundMatrix=World.transform.worldToLocalMatrix;
            if(valid&&roadMatrix==lastRoad&&groundMatrix==lastGround)return;
            if(block==null)block=new MaterialPropertyBlock();block.Clear();
            if(cache!=null)cache.Bind(block);else LTRoadTerrainFallback.Bind(block);
            block.SetMatrix("_LTRoadBlendWorldToMask",roadMatrix);
            block.SetMatrix("_LTSurfaceWorldToLocal",groundMatrix);
            target.SetPropertyBlock(block);lastRoad=roadMatrix;lastGround=groundMatrix;valid=true;
        }
    }
}
