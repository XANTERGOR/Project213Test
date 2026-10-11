using LocalTerrainPrototype;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

static class HeightStampChecks
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    public static void Run()
    {
        for(int mode=0;mode<9;mode++)
        {
            var scene=new Scene();var root=scene.Root("World");
            var world=root.Add(new LTWorld());world.generatedRoot=root.Child("Generated").transform;
            var chunk=world.generatedRoot.gameObject.Child("Chunk").Add(new LTChunk{mesh=new Mesh{persistent=true}});
            var go=root.Child("Height");go.active=false;
            var stamp=go.Add(new LTHeightStamp());var extra=go.Add(new MeshFilter());var child=go.Child("Keep child");
            var orphan=scene.Root("Unowned").Add(new LTHeightStamp());
            if(mode==1)chunk.mesh=null;
            if(mode==2)chunk.mesh.persistent=false;
            if(mode==3)chunk.lodsPending=true;
            if(mode==4)world.chunksX=2;
            if(mode==5)world.generatedRoot=null;
            if(mode==6)world.generatedRoot.gameObject.Child("Duplicate").Add(new LTChunk{mesh=new Mesh{persistent=true}});
            var stripper=new LTHeightStampBuildStripper();
            stripper.OnProcessScene(scene,null);
            Check(!stamp.destroyed,"Play Mode must retain authoring components");
            bool failed=false;BuildPipeline.isBuildingPlayer=mode==8;
            try{stripper.OnProcessScene(scene,mode==8?null:new BuildReport());}
            catch(BuildFailedException){failed=true;}
            finally{BuildPipeline.isBuildingPlayer=false;}
            Check(failed==(mode>=1&&mode<=6),"Reject incomplete or unsaved terrain");
            Check(stamp.destroyed==!failed,"Strip only after validating terrain");
            Check(!extra.destroyed&&!child.destroyed&&!go.destroyed&&!orphan.destroyed,"Keep other components, hierarchy and unowned stamps");
        }
        Console.WriteLine("PASS height-stamp build stripping: saved terrain, invalid/missing/pending/duplicate chunks, inactive stamps, Play Mode, null-report builds, preserved hierarchy.");
    }
}
