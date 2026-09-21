using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace LocalTerrainPrototype
{
    public static class LTSmokeTests
    {
        static void Check(bool ok,string message){if(!ok)throw new Exception("Smoke test: "+message);}
        static LTChunk Chunk(LTWorld w,int x,int z)=>w.generatedRoot.GetComponentsInChildren<LTChunk>().First(c=>c.x==x&&c.z==z);
        static LTHeightStamp Stamp(LTWorld w,Vector3 position)
        {
            var go=new GameObject("Test Stamp");go.transform.SetParent(w.transform,false);go.transform.localPosition=position;
            var s=go.AddComponent<LTHeightStamp>();s.size=new Vector2(8,8);s.shape=LTStampShape.Rectangle;return s;
        }
        [MenuItem("Tools/Local Terrain/Run Smoke Tests")]
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var previous=SceneManager.GetActiveScene();var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);string folder=null;
            try
            {
                SceneManager.SetActiveScene(scene);LTEditorEngine.Create(null,new Vector3(192,100,192),3,3,32,null);
                var w=Selection.activeGameObject.GetComponent<LTWorld>();folder=w.outputFolder;w.autoUpdate=false;
                var stamp=Stamp(w,new Vector3(32,8,32));stamp.operation=LTStampOperation.Override;
                LTEditorEngine.Refresh(w,false);Check(w.lastUpdatedChunks==1,"height stamp stays local");
                Check(Mathf.Abs(Chunk(w,0,0).mesh.vertices.Max(v=>v.y)-8)<.001f,"Position Y height");
                stamp.transform.localScale=new Vector3(1,2,1);LTEditorEngine.Refresh(w,false);
                Check(Mathf.Abs(Chunk(w,0,0).mesh.vertices.Max(v=>v.y)-16)<.001f,"Scale Y amplitude");
                stamp.affectHeight=false;stamp.densityZone=true;stamp.densityCellSizeMin=.25f;stamp.densityCellSizeMax=.5f;LTEditorEngine.Refresh(w,false);
                Check(Chunk(w,0,0).mesh.vertices.All(v=>Mathf.Abs(v.y)<.001f),"density-only does not sculpt");
                int count=Chunk(w,0,0).mesh.vertexCount;
                stamp.densityZone=false;LTEditorEngine.Refresh(w,false);Check(Chunk(w,0,0).mesh.vertexCount<count,"toggle restores base mesh");
                stamp.densityZone=true;LTEditorEngine.Refresh(w,false);int unaffected=Chunk(w,2,0).mesh.vertexCount;
                stamp.transform.localPosition=new Vector3(160,0,160);LTEditorEngine.Refresh(w,false);
                Check(Chunk(w,0,0).mesh.vertexCount<count,"old position restored");
                Check(Chunk(w,2,0).mesh.vertexCount==unaffected,"unrelated chunk unchanged");
                Check(!w.lastUpdatedIds.Contains("(2,0)"),"unrelated chunk not rebuilt");
                stamp.transform.localPosition=new Vector3(64,0,32);LTEditorEngine.Refresh(w,false);
                var left=Chunk(w,0,0).mesh;var right=Chunk(w,1,0).mesh;
                var lv=left.vertices.Where(v=>v.x==64).OrderBy(v=>v.z).ToArray();var rv=right.vertices.Where(v=>v.x==0).OrderBy(v=>v.z).ToArray();
                Check(lv.Length==rv.Length,"border counts");for(int i=0;i<lv.Length;i++)Check(Vector3.Distance(lv[i],rv[i]+Vector3.right*64)<.0001f,"border positions");
                var before=Chunk(w,0,0).mesh.vertices;w.maxVerticesPerChunk=10000;stamp.size=new Vector2(16,16);stamp.densityCellSizeMin=.001f;stamp.densityCellSizeMax=.001f;
                bool refused=false;try{LTEditorEngine.Refresh(w,false);}catch(InvalidOperationException){refused=true;}
                Check(refused,"resource error surfaced");Check(Chunk(w,0,0).mesh.vertices.SequenceEqual(before),"failed batch preserves original mesh");
                LTRefinementTests.Run();Debug.Log("Local Terrain v0.5: smoke tests PASSED.");
            }
            finally{EditorSceneManager.CloseScene(scene,true);if(previous.IsValid())SceneManager.SetActiveScene(previous);if(!string.IsNullOrEmpty(folder))AssetDatabase.DeleteAsset(folder);}
        }
    }
}
