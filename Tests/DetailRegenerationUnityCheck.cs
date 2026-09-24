#if UNITY_EDITOR
// Copy into an isolated Unity project's Assets (not Editor): tests internal runtime types.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    public static class DetailRegenerationUnityCheck
    {
        const BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        static readonly Type RendererType=typeof(LTDetailRenderer);
        static object Get(object obj,string name)=>obj.GetType().GetField(name,Fields).GetValue(obj);
        static void Set(object obj,string name,object value)=>obj.GetType().GetField(name,Fields).SetValue(obj,value);
        static object Nested(string name)=>Activator.CreateInstance(RendererType.GetNestedType(name,BindingFlags.NonPublic),true);
        static IList ListOf(string name)=>(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(RendererType.GetNestedType(name,BindingFlags.NonPublic)));
        static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        static int Mix(int a,int b)=>unchecked(a*397^b);
        static LTDetailEntry Clone(LTDetailEntry entry)=>JsonUtility.FromJson<LTDetailEntry>(JsonUtility.ToJson(entry));
        static object Source(LTDetailEntry entry,LTDetailPrefab recipe,LTSurfaceLayer layer)
        {
            var source=Nested("Source");var copy=Clone(entry);
            Set(source,"entry",copy);Set(source,"recipe",recipe);Set(source,"layer",layer);
            Set(source,"owner","layer");Set(source,"seed",12345);Set(source,"order",-1);Set(source,"density",copy.density);
            int key=Mix(12345,copy.density.GetHashCode());
            Set(source,"hash",Mix(JsonUtility.ToJson(copy).GetHashCode(),key));
            Set(source,"placementHash",Mix(JsonUtility.ToJson(copy.PlacementSettings()).GetHashCode(),key));return source;
        }
        static void MatrixEqual(Matrix4x4 a,Matrix4x4 b,string label)
        {for(int i=0;i<16;i++)Check(Mathf.Abs(a[i]-b[i])<.00002f,label);}
        static void Compare(object fast,object full)
        {
            Check((int)Get(fast,"count")== (int)Get(full,"count"),"population unchanged");
            Check((long)Get(fast,"cachedMatrixBytes")== (long)Get(full,"cachedMatrixBytes"),"cache accounting unchanged");
            var a=(IList)Get(fast,"groups");var b=(IList)Get(full,"groups");Check(a.Count==b.Count,"group count");
            for(int g=0;g<a.Count;g++)
            {
                var x=(IList)Get(a[g],"instances");var y=(IList)Get(b[g],"instances");Check(x.Count==y.Count,"group population");
                Check((float)Get(a[g],"shadowRange")== (float)Get(b[g],"shadowRange"),"shadow range");
                for(int i=0;i<x.Count;i++)
                {
                    Check((uint)Get(x[i],"id")== (uint)Get(y[i],"id"),"stable IDs/order");
                    Check((Vector3)Get(x[i],"position")== (Vector3)Get(y[i],"position"),"stable positions");
                    MatrixEqual((Matrix4x4)Get(x[i],"matrix"),(Matrix4x4)Get(y[i],"matrix"),"fast/full transform");
                    Check((Bounds)Get(x[i],"bounds")== (Bounds)Get(y[i],"bounds"),"fast/full culling bounds");
                    Check((Vector3)Get(x[i],"lodPosition")== (Vector3)Get(y[i],"lodPosition"),"fast/full LOD position");
                    Check(Mathf.Abs((float)Get(x[i],"lodSize")-(float)Get(y[i],"lodSize"))<.00002f,"fast/full LOD size");
                }
                var am=(IDictionary)Get(a[g],"partMatrices");var bm=(IDictionary)Get(b[g],"partMatrices");
                foreach(DictionaryEntry part in am)
                {
                    var ma=(Matrix4x4[])part.Value;var mb=(Matrix4x4[])bm[part.Key];
                    for(int i=0;i<ma.Length;i++)MatrixEqual(ma[i],mb[i],"part matrix cache");
                }
            }
            Check((Bounds)Get(fast,"bounds")== (Bounds)Get(full,"bounds"),"cell bounds");
            Check((Bounds)Get(fast,"originBounds")== (Bounds)Get(full,"originBounds"),"cell origins");
        }
        static object Changed(object value,Type type,GameObject obj,Texture2D texture)
        {
            if(type==typeof(bool))return !(bool)value;
            if(type==typeof(float))return (float)value+.123f;
            if(type==typeof(int))return (int)value+1;
            if(type.IsEnum)return Enum.ToObject(type,Convert.ToInt32(value)^1);
            if(type==typeof(Vector2))return (Vector2)value+new Vector2(.12f,.23f);
            if(type==typeof(Color))return new Color(.23f,.41f,.63f,.87f);
            if(type==typeof(GameObject))return obj;
            if(type==typeof(Texture2D))return texture;
            throw new Exception("Add mutation for new serialized field: "+type);
        }
        public static void Run()
        {
            var cleanup=new List<UnityEngine.Object>();int exit=0;
            try
            {
                var root=new GameObject("Isolated detail regeneration fixture");root.SetActive(false);cleanup.Add(root);
                var texture=new Texture2D(2,2);cleanup.Add(texture);
                var layer=ScriptableObject.CreateInstance<LTSurfaceLayer>();cleanup.Add(layer);
                int surfaceKey=layer.SurfaceHash();
                layer.details.Add(new LTDetailEntry{density=17,patchScaleEnabled=true});layer.detailDensityMask.patchCoverage=.73f;
                layer.name="renamed";Check(surfaceKey==layer.SurfaceHash(),"details/name must not dirty surface");
                foreach(var field in typeof(LTSurfaceLayer).GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly))
                {
                    if(field.Name=="details"||field.Name=="detailDensityMask")continue;
                    var before=field.GetValue(layer);field.SetValue(layer,Changed(before,field.FieldType,root,texture));
                    Check(surfaceKey!=layer.SurfaceHash(),"surface key includes "+field.Name);field.SetValue(layer,before);
                }
                var grass=new LTDetailEntry{density=3,densityMask=true,patchCoverage=.7f,patchSize=4,
                    patchScaleEnabled=true,scaleRange=new Vector2(.7f,1.3f),alignToNormal=.8f,heightOffsetRange=new Vector2(.2f,.5f)};
                var used=new HashSet<string>();grass.Validate(used);
                var excluded=new HashSet<string>{"scaleRange","patchScaleEnabled","patchScaleEdge","patchScaleInside","patchScaleSoftness"};
                string placement=JsonUtility.ToJson(grass.PlacementSettings()),original=JsonUtility.ToJson(grass);
                foreach(var field in typeof(LTDetailEntry).GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly))
                {
                    var copy=Clone(grass);field.SetValue(copy,Changed(field.GetValue(copy),field.FieldType,root,texture));
                    Check((placement==JsonUtility.ToJson(copy.PlacementSettings()))==excluded.Contains(field.Name),"placement key: "+field.Name);
                }
                Check(JsonUtility.ToJson(grass)==original,"placement key never mutates entry");
                var fixedEntry=Clone(grass);fixedEntry.Validate(used);fixedEntry.patchScaleEnabled=false;
                var emptyEntry=Clone(grass);emptyEntry.Validate(used);emptyEntry.probability=0;
                var world=root.AddComponent<LTWorld>();world.enabled=false;world.chunksX=world.chunksZ=1;
                root.transform.position=new Vector3(17,3,-23);
                var terrainSource=ScriptableObject.CreateInstance<LTSource>();cleanup.Add(terrainSource);
                terrainSource.size=new Vector3(16,10,16);world.source=terrainSource;
                var chunkObject=new GameObject("sloped mesh");chunkObject.transform.SetParent(root.transform,false);
                var chunk=chunkObject.AddComponent<LTChunk>();var mesh=new Mesh();cleanup.Add(mesh);
                mesh.vertices=new[]{new Vector3(0,0,0),new Vector3(0,1.6f,16),new Vector3(16,3.2f,0),new Vector3(16,4.8f,16)};
                mesh.triangles=new[]{0,1,2,2,1,3};mesh.RecalculateNormals();mesh.RecalculateBounds();chunk.mesh=mesh;
                var renderer=root.AddComponent<LTDetailRenderer>();renderer.enabled=false;Set(renderer,"world",world);renderer.cellSize=16;
                var keys=new HashSet<Vector2Int>{Vector2Int.zero};((List<Vector2Int>)Get(renderer,"planned")).Add(Vector2Int.zero);
                ((HashSet<Vector2Int>)Get(renderer,"wanted")).Add(Vector2Int.zero);
                var terrain=(LTPaintTerrain)Get(renderer,"terrain");var chunks=new[]{chunk};terrain.Update(world,chunks);
                var surface=new LTDetailSurface();surface.tiles.Add(new LTDetailSurface.Tile{rect=new Rect(0,0,16,16),coverage=3,surface=5,layers=new[]{layer}});
                var recipe=new LTDetailPrefab{rootScale=new Vector3(1,2,.5f),bounds=new Bounds(Vector3.up,new Vector3(1,2,1)),lodSize=2,lodReferencePoint=Vector3.up};
                var level=new LTDetailPrefab.Level();recipe.levels.Add(level);
                level.parts.Add(new LTDetailPrefab.Part{localMatrix=Matrix4x4.identity,castShadows=ShadowCastingMode.On});
                level.parts.Add(new LTDetailPrefab.Part{localMatrix=Matrix4x4.Translate(Vector3.right),castShadows=ShadowCastingMode.Off});
                var cells=(IDictionary)Get(renderer,"cells");var stamps=ListOf("Stamp");
                IEnumerator Work(int common)
                {
                    var sources=ListOf("Source");sources.Add(Source(grass,recipe,layer));sources.Add(Source(fixedEntry,recipe,layer));sources.Add(Source(emptyEntry,recipe,layer));
                    return (IEnumerator)RendererType.GetMethod("Generate",Fields).Invoke(renderer,new object[]{surface,chunks,sources,stamps,common,root.transform.localToWorldMatrix,keys});
                }
                void Generate(int common){var work=Work(common);int steps=0;while(work.MoveNext())Check(++steps<100000,"bounded generation");}
                Generate(42);var initial=cells[Vector2Int.zero];Check((int)Get(initial,"count")>500,"nonempty fixture");
                for(int pass=0;pass<6;pass++)
                {
                    var old=cells[Vector2Int.zero];var unchanged=((IList)Get(old,"groups"))[1];
                    grass.patchScaleEnabled=pass!=2;grass.patchScaleEdge=.1f+pass*.12f;grass.patchScaleInside=1.7f-pass*.1f;
                    grass.patchScaleSoftness=pass*.2f;grass.scaleRange=new Vector2(.5f+pass*.1f,1.4f+pass*.1f);
                    // No terrain samples are available: fast scale updates must still succeed.
                    terrain.Clear();Generate(42);var fast=cells[Vector2Int.zero];
                    Check((int)Get(fast,"count")== (int)Get(old,"count"),"no terrain reads during rescale");
                    Check(ReferenceEquals(unchanged,((IList)Get(fast,"groups"))[1]),"unchanged group/resources retained");
                    Check(renderer.CachedMatrixBytes==(long)Get(fast,"cachedMatrixBytes"),"resident accounting");
                    terrain.Update(world,chunks);Generate(43);Compare(fast,cells[Vector2Int.zero]);
                    Generate(42); // Restore the same common key for the next scale edit.
                }
                var resident=cells[Vector2Int.zero];grass.patchScaleInside+=.3f;var interrupted=Work(42);
                Check(interrupted.MoveNext(),"rescale yields within cell");((HashSet<Vector2Int>)Get(renderer,"wanted")).Clear();
                while(interrupted.MoveNext()){}Check(ReferenceEquals(resident,cells[Vector2Int.zero]),"cancel leaves old cell intact");
                ((HashSet<Vector2Int>)Get(renderer,"wanted")).Add(Vector2Int.zero);
                grass.patchCoverage=0;grass.patchMinimumDensity=0;terrain.Update(world,chunks);Generate(42);
                Check(((IList)Get(cells[Vector2Int.zero],"groups")).Count==1,"density-mask edit uses full generation");
                Debug.Log("PASS detail regeneration: all surface/placement fields; 6 fast/full comparisons on translated sloped terrain; stable IDs/positions, matrices, bounds, LOD, CPU caches, unchanged group reuse, empty groups, cancellation and density invalidation.");
            }
            catch(Exception e){Debug.LogException(e);exit=1;}
            finally{for(int i=cleanup.Count-1;i>=0;i--)if(cleanup[i])UnityEngine.Object.DestroyImmediate(cleanup[i]);}
            EditorApplication.Exit(exit);
        }
    }
}
#endif
