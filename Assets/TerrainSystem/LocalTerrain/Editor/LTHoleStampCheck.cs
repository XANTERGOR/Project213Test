using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    public static class LTHoleStampCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Hole Stamp (no scene changes)
        public static void Run()
        {
            // Only temporary hidden objects in an owned preview scene. Never
            // call LTEditorEngine.Refresh, SaveAssets or save a user scene.
            var scenes=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++)
            {var s=SceneManager.GetSceneAt(i);scenes.Add((s,s.isDirty,s.rootCount));}
            var active=SceneManager.GetActiveScene();
            Scene preview=default;GameObject owner=null;Mesh mesh=null;Texture2D texture=null;
            try
            {
                preview=EditorSceneManager.NewPreviewScene();
                owner=EditorUtility.CreateGameObjectWithHideFlags("Hole Stamp collider check",HideFlags.HideAndDontSave,typeof(MeshCollider));
                SceneManager.MoveGameObjectToScene(owner,preview);
                var collider=owner.GetComponent<MeshCollider>();
                mesh=new Mesh{name="Hole check mesh",hideFlags=HideFlags.HideAndDontSave};
                var rect=new Rect(0,0,8,8);
                LTHoleStampSnapshot Hole(float x,float z,LTStampShape shape=LTStampShape.Ellipse,LTStampDensityMask mask=null,float size=2)
                {
                    var matrix=Matrix4x4.Translate(new Vector3(x,0,z));
                    return new LTHoleStampSnapshot(matrix,matrix.inverse,new Vector2(size,size),shape,.125f,mask);
                }
                void Build(LTHoleStampSnapshot hole,Func<float,float,float> height=null)
                {
                    height=height??((x,z)=>0);
                    var zones=new List<LTStampMesh.Zone>();if(hole!=null)zones.Add(hole.CreateZone());
                    var plan=LTStampMesh.Plan(rect,4,true,.1f,100000,zones,height,new LTLODMesh.BuildProgress(false));
                    var forest=new LTBalancedForest(1,1,100000,new Dictionary<int,List<Vector3Int>>{{0,plan}});forest.Balance();
                    LTStampMesh.Emit(rect,new Vector2(8,8),100000,forest,0,forest.Plan(0),height,hole==null?(Func<float,float,bool>)null:(x,z)=>hole.Contains(x,height(x,z),z),
                        out var positions,out var normals,out var uv,out var indices,null,new LTLODMesh.BuildProgress(false));
                    collider.sharedMesh=null;mesh.Clear();mesh.vertices=positions;mesh.normals=normals;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateBounds();
                    if(indices.Length>0)collider.sharedMesh=mesh;
                }
                bool Hit(float x,float z)=>collider.Raycast(new Ray(new Vector3(x,10,z),Vector3.down),out _,20);
                Build(null);Require(Hit(4,4)&&Hit(1,1),"Baseline collider raycast failed.");
                Build(Hole(4,4));Require(!Hit(4,4)&&Hit(1,1),"Through-hole did not cut the collider.");
                Build(Hole(6,4));Require(Hit(4,4)&&!Hit(6,4),"Moving hole did not restore the old location.");
                Build(null);Require(Hit(4,4)&&Hit(6,4),"Removing hole did not restore collider.");
                texture=new Texture2D(4,4,TextureFormat.RGBAFloat,false,true){hideFlags=HideFlags.HideAndDontSave};
                var pixels=new Color[16];for(int y=0;y<4;y++)for(int x=0;x<2;x++)pixels[y*4+x]=Color.white;
                texture.SetPixels(pixels);texture.Apply(false,false);
                var mask=LTStampDensityMask.Capture(texture,false,new Vector2(0,1),1);
                Build(Hole(4,4,LTStampShape.Rectangle,mask));Require(!Hit(3.5f,4)&&Hit(4.5f,4),"Mask and collider do not agree.");
                Build(Hole(4,4,LTStampShape.Rectangle,size:20));Require(!collider.sharedMesh&&!Hit(1,1),"Fully cut chunk must have no collider mesh.");
                var cubeVertices=new[]{new Vector3(-1,-1,-1),new Vector3(1,-1,-1),new Vector3(1,-1,1),new Vector3(-1,-1,1),
                    new Vector3(-1,1,-1),new Vector3(1,1,-1),new Vector3(1,1,1),new Vector3(-1,1,1)};
                var cubeIndices=new[]{0,1,2,0,2,3,4,6,5,4,7,6,0,4,5,0,5,1,1,5,6,1,6,2,2,6,7,2,7,3,3,7,4,3,4,0};
                var cube=new LTHoleMeshData(cubeVertices,cubeIndices);
                LTHoleStampSnapshot Volume(LTHoleMeshData data,float y,Quaternion rotation)=>
                    new LTHoleStampSnapshot(new LTHoleMeshVolume(data,Matrix4x4.TRS(new Vector3(4,y,4),rotation,Vector3.one)),.125f);
                Build(Volume(cube,0,Quaternion.identity));Require(!Hit(4,4)&&Hit(1,1),"Mesh volume did not cut native collider.");
                Build(Volume(cube,5,Quaternion.identity));Require(Hit(4,4),"Raised mesh still cuts its projection.");
                Build(Volume(cube,-5,Quaternion.identity));Require(Hit(4,4),"Buried mesh still cuts its projection.");
                Build(Volume(cube,0,Quaternion.Euler(29,17,33)));Require(!Hit(4,4)&&Hit(1,1),"XYZ rotated volume cut failed.");
                Build(Volume(cube,3,Quaternion.identity));Require(Hit(4,4),"High cutter must miss low terrain.");
                Build(Volume(cube,3,Quaternion.identity),(x,z)=>3);Require(!Hit(4,4)&&Hit(1,1),"Cut must follow actual terrain height, not Y=0.");
                var shells=new Vector3[16];var shellIndices=new int[cubeIndices.Length*2];
                for(int i=0;i<8;i++){shells[i]=cubeVertices[i]+Vector3.up*3;shells[i+8]=cubeVertices[i]-Vector3.up*3;}
                for(int i=0;i<cubeIndices.Length;i++){shellIndices[i]=cubeIndices[i];shellIndices[i+cubeIndices.Length]=cubeIndices[i]+8;}
                Build(Volume(new LTHoleMeshData(shells,shellIndices),0,Quaternion.identity));
                Require(Hit(4,4),"Gap between disconnected volumes must retain ground.");
                Build(null);Require(Hit(4,4),"Removing mesh volume did not restore collider.");
                Debug.Log("Hole Mesh Volume native PASS: collider cuts, above/below restoration, XYZ rotation, terrain height and vertical air gap. Owned preview scene only.");
                string lod=LTHoleStampLODCheck.Run((positions,indices)=>
                {
                    collider.sharedMesh=null;mesh.Clear();mesh.vertices=positions;mesh.triangles=indices;mesh.RecalculateBounds();collider.sharedMesh=mesh;
                    Require(!Hit(8,8)&&Hit(1,1),"LOD mesh changed the through-hole collider.");
                });
                Debug.Log("Hole Stamp LOD v2 PASS: "+lod);
                string edgeLOD=LTHoleEdgeLODCheck.Run((positions,indices,centre)=>
                {
                    collider.sharedMesh=null;mesh.Clear();mesh.indexFormat=positions.Length>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16;
                    mesh.vertices=positions;mesh.triangles=indices;mesh.RecalculateBounds();collider.sharedMesh=mesh;
                    Require(!Hit(centre.x,centre.y)&&Hit(1,1),"Hole closed or outside ground vanished at a visual LOD.");
                });
                Debug.Log("Hole edge LOD v5 PASS: "+edgeLOD);
                string volumeLOD=LTHoleVolumeLODCheck.Run((positions,indices,centreHasGround)=>
                {
                    collider.sharedMesh=null;mesh.Clear();mesh.indexFormat=positions.Length>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16;
                    mesh.vertices=positions;mesh.triangles=indices;mesh.RecalculateBounds();collider.sharedMesh=mesh;
                    Require(Hit(8,8)==centreHasGround&&Hit(1,1),"Volume LOD collider lost its opening, bridge/cavity or outer ground.");
                });
                Debug.Log("Hole Mesh Volume LOD v7 PASS: "+volumeLOD);
                string contourLOD=LTHoleContourLODCheck.Run((positions,indices,point,hasGround)=>
                {
                    collider.sharedMesh=null;mesh.Clear();mesh.indexFormat=positions.Length>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16;
                    mesh.vertices=positions;mesh.triangles=indices;mesh.RecalculateBounds();collider.sharedMesh=mesh;
                    Require(Hit(point.x,point.y)==hasGround&&Hit(1,1),"Simplified mesh contour lost its opening, bridge/island or outer ground.");
                });
                Debug.Log("Hole Mesh Contour LOD v8 PASS: "+contourLOD);
                CheckPreviewGraphics();
            }
            finally
            {
                if(owner)UnityEngine.Object.DestroyImmediate(owner);
                if(mesh)UnityEngine.Object.DestroyImmediate(mesh);
                if(texture)UnityEngine.Object.DestroyImmediate(texture);
                if(preview.IsValid())EditorSceneManager.ClosePreviewScene(preview);
                Require(SceneManager.GetActiveScene()==active&&SceneManager.sceneCount==scenes.Count,"Loaded scene selection/count changed during validation.");
                foreach(var s in scenes)Require(s.scene.IsValid()&&s.scene.isDirty==s.dirty&&s.scene.rootCount==s.roots,"A user scene changed during validation.");
            }
            Debug.Log("Hole Stamp v8 PASS: native MeshCollider raycasts, shape/mask/mesh-volume cuts, movement/restoration, fully removed chunk, bounded corner/contour error, shape AND mesh-volume contour simplification, preserved openings/bridges/islands at mixed LODs and preview graphics readback. No user scene/assets/settings changed.");
        }
        static void CheckPreviewGraphics()
        {
            if(SystemInfo.graphicsDeviceType==UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new InvalidOperationException("Hole Preview graphics NOT TESTED: requires a graphics device, not -nographics.");
            var shader=AssetDatabase.LoadAssetAtPath<Shader>(LTHoleStampPreview.ShaderPath);
            Require(shader&&shader.isSupported&&!ShaderUtil.ShaderHasError(shader),"Hole preview shader import/support failed.");
            Mesh patch=null;Material material=null;RenderTexture rt=null;Texture2D readback=null;
            var previous=RenderTexture.active;bool wire=GL.wireframe,srgb=GL.sRGBWrite,asyncCompile=ShaderUtil.allowAsyncCompilation;
            try
            {
                GL.wireframe=false;GL.sRGBWrite=false;ShaderUtil.allowAsyncCompilation=false;
                patch=new Mesh{hideFlags=HideFlags.HideAndDontSave};
                patch.vertices=new[]{new Vector3(-1,0,-1),new Vector3(-1,0,1),new Vector3(1,0,1),new Vector3(1,0,-1)};
                patch.colors=new[]{Color.black,Color.black,Color.red,Color.red};patch.triangles=new[]{0,1,2,0,2,3};
                material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};material.SetFloat("_Opacity",.8f);
                rt=new RenderTexture(64,64,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear){hideFlags=HideFlags.HideAndDontSave};
                rt.Create();Require(rt.IsCreated(),"Could not create hole preview graphics target.");
                readback=new Texture2D(64,64,TextureFormat.RGBAFloat,false,true){hideFlags=HideFlags.HideAndDontSave};
                var camera=Matrix4x4.TRS(new Vector3(0,2,0),Quaternion.LookRotation(Vector3.down,Vector3.forward),Vector3.one);
                var view=Matrix4x4.Scale(new Vector3(1,1,-1))*camera.inverse;
                void Render()
                {
                    RenderTexture.active=rt;GL.PushMatrix();
                    try
                    {
                        GL.LoadProjectionMatrix(Matrix4x4.Ortho(-1,1,-1,1,.1f,10));GL.modelview=view;GL.Clear(true,true,Color.clear);
                        Require(material.SetPass(0),"Hole preview SetPass failed.");Graphics.DrawMeshNow(patch,Matrix4x4.identity,0);
                    }
                    finally{GL.PopMatrix();}
                    readback.ReadPixels(new Rect(0,0,64,64),0,0);readback.Apply(false,false);
                }
                Render();
                var kept=readback.GetPixel(8,32);var cut=readback.GetPixel(56,32);var rim=readback.GetPixel(32,32);
                Require(kept.a<.001f&&cut.a>.1f&&cut.r>cut.g+.2f,"Hole preview cut/keep field did not render.");
                Require(rim.g>cut.g+.1f,"Hole preview contour highlight did not render.");
                material.SetFloat("_Opacity",0);Render();Require(readback.GetPixel(56,32).a<.001f,"Hole preview opacity failed.");
                Require(!ShaderUtil.ShaderHasError(shader),"Hole preview shader reported a graphics error.");
                Debug.Log("Hole Preview graphics PASS: owned DrawMeshNow/readback, cut/keep clipping, contour and opacity. Live Scene View still needs a visual check.");
            }
            finally
            {
                RenderTexture.active=previous;GL.wireframe=wire;GL.sRGBWrite=srgb;ShaderUtil.allowAsyncCompilation=asyncCompile;
                if(rt){rt.Release();UnityEngine.Object.DestroyImmediate(rt);}
                if(readback)UnityEngine.Object.DestroyImmediate(readback);
                if(material)UnityEngine.Object.DestroyImmediate(material);
                if(patch)UnityEngine.Object.DestroyImmediate(patch);
            }
        }
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Hole Stamp FAIL: "+message);}
    }
}
