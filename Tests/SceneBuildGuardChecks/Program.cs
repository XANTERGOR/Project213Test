using LocalTerrainPrototype;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

// Runs the real production callbacks against small API doubles, not native Unity.
static class Checks
{
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static LTRockRenderAsset Valid()=>new(){combined=new Mesh(),rocks=new[]{new Mesh(),new Mesh()},transitions=new[]{new Mesh(),new Mesh()}};
    static LTMeshStamp Stamp(Scene scene,string name)
    {
        var root=scene.roots.FirstOrDefault()??scene.Root("LT World");
        var go=root.Child(name);go.Add(new MeshFilter{sharedMesh=new Mesh()});go.Add(new MeshCollider{sharedMesh=new Mesh()});
        return go.Add(new LTMeshStamp{sourceMesh=new Mesh()});
    }
    static void MustFail(Action run,string objectName)
    {
        bool failed=false;
        try{run();}catch(BuildFailedException e)
        {
            failed=true;
            Require(e.Message.Contains(objectName)&&e.Message.Contains("Rebuild All + Save Meshes"),"Build failure must identify repair and object");
        }
        Require(failed,"Real build must reject missing saved LODs");
    }
    static void Main()
    {
        DetailPlacementBuildChecks.Run();
        HeightStampChecks.Run();
        var rock=new LTRockRenderBuildGuard();var terrain=new LTSpatialLODBuildGuard();
        Require(rock.callbackOrder==1&&terrain.callbackOrder==0,"Preserve callback ordering");
        // Missing and invalid serialized assets, including destroyed Unity objects.
        for(int invalid=0;invalid<7;invalid++)
        {
            var scene=new Scene();var stamp=Stamp(scene,"Object006");stamp.renderAsset=Valid();
            switch(invalid)
            {
                case 0:stamp.renderAsset=null;break;
                case 1:stamp.renderAsset.combined=null;break;
                case 2:stamp.renderAsset.rocks=Array.Empty<Mesh>();break;
                case 3:stamp.renderAsset.transitions=new[]{new Mesh()};break;
                case 4:stamp.renderAsset.rocks[1]=null;break;
                case 5:stamp.renderAsset.transitions[1].destroyed=true;break;
                case 6:stamp.renderAsset.destroyed=true;break;
            }
            var mesh=stamp.GetComponent<MeshFilter>().sharedMesh;var collider=stamp.GetComponent<MeshCollider>().sharedMesh;
            var asset=stamp.renderAsset;
            Debug.warnings.Clear();BuildPipeline.isBuildingPlayer=false;
            rock.OnProcessScene(scene,null);
            Require(Debug.warnings.Count==1&&ReferenceEquals(Debug.warnings[0].context,stamp),"One contextual Play warning per invalid stamp");
            string message=Debug.warnings[0].text;
            Require(message.Contains("Assets/TerrainTest.unity :: LT World/Object006")&&message.Contains("Play Mode")&&message.Contains("Rebuild All + Save Meshes"),"Actionable Play warning includes scene and hierarchy");
            Require(ReferenceEquals(mesh,stamp.GetComponent<MeshFilter>().sharedMesh)&&ReferenceEquals(collider,stamp.GetComponent<MeshCollider>().sharedMesh)&&ReferenceEquals(asset,stamp.renderAsset),"Missing LOD warning must not overwrite geometry/collider/asset");
            Debug.warnings.Clear();MustFail(()=>rock.OnProcessScene(scene,new BuildReport()),"Object006");
            Require(Debug.warnings.Count==0,"Real build failure is not downgraded to a warning");
            BuildPipeline.isBuildingPlayer=true;MustFail(()=>rock.OnProcessScene(scene,null),"Object006");BuildPipeline.isBuildingPlayer=false;
        }
        // A missing stamp must not prevent valid siblings from initializing.
        foreach(bool building in new[]{false,true})foreach(bool inactive in new[]{false,true})
        {
            var scene=new Scene();LTMeshStamp missing=null;
            if(!building)missing=Stamp(scene,"Missing");
            var stamp=Stamp(scene,"Valid");stamp.gameObject.active=!inactive;stamp.renderAsset=Valid();
            var saved=stamp.renderAsset;Debug.warnings.Clear();
            rock.OnProcessScene(scene,building?new BuildReport():null);
            Require(ReferenceEquals(stamp.GetComponent<MeshFilter>().sharedMesh,saved.rocks[0])&&ReferenceEquals(stamp.GetComponent<MeshCollider>().sharedMesh,saved.combined),"Valid saved LOD0 and full collider still restored");
            Require(saved.Valid&&Debug.warnings.Count==(building?0:1),"Saved assets unchanged and valid stamps do not warn");
        }
        // Preserve exclusions and cave handling; inactive stamps still require data.
        foreach(Action<LTMeshStamp> skip in new Action<LTMeshStamp>[]
            {s=>s.enabled=false,s=>s.affectHeight=false,s=>s.trimRockInsideTerrain=false,s=>s.sourceMesh=null})
        {
            var scene=new Scene();var stamp=Stamp(scene,"Excluded");skip(stamp);Debug.warnings.Clear();
            rock.OnProcessScene(scene,new BuildReport());rock.OnProcessScene(scene,null);
            Require(Debug.warnings.Count==0,"Excluded stamp must not require render bake");
        }
        {
            var scene=new Scene();var stamp=Stamp(scene,"Cave");stamp.trimRockInsideTerrain=false;stamp.mode=LTMeshConformMode.Cave;
            stamp.gameObject.active=false;MustFail(()=>rock.OnProcessScene(scene,new BuildReport()),"Cave");
        }
        // Chunk guard also receives null reports for Play. Retain its LOD0 reset.
        foreach(bool pending in new[]{false,true})
        {
            var scene=new Scene();var root=scene.Root("LT World");
            var chunk=root.Child("Chunk_0_0").Add(new LTChunk{lodsPending=pending});
            var road=root.Child("Road").Add(new LTRoadLOD());Debug.warnings.Clear();
            terrain.OnProcessScene(scene,null);
            Require(chunk.shown==0&&road.shown==0&&Debug.warnings.Count==(pending?1:0),"Play uses existing LOD0 and diagnoses pending chunks");
            Require(chunk.lodsPending==pending,"Validation must not mark pending data complete");
            int before=chunk.showCalls;
            if(pending)
            {
                MustFail(()=>terrain.OnProcessScene(scene,new BuildReport()),"Chunk_0_0");
                BuildPipeline.isBuildingPlayer=true;MustFail(()=>terrain.OnProcessScene(scene,null),"Chunk_0_0");BuildPipeline.isBuildingPlayer=false;
                Require(chunk.showCalls==before,"Build fails before publishing unfinished LODs");
            }
            else{terrain.OnProcessScene(scene,new BuildReport());Require(chunk.showCalls==before+1,"Valid build LOD0 reset preserved");}
        }
        Console.WriteLine("PASS scene LOD build guards: Play warnings, missing/invalid assets, preserved geometry and collider, valid LOD0 restoration, inactive/cave/excluded stamps, pending chunks, strict real-build rejection including null-report build context. API doubles, not a native Play/build test.");
    }
}
