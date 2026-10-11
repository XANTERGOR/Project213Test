using LocalTerrainPrototype;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine.SceneManagement;

static class DetailPlacementBuildChecks
{
    public static void Run()
    {
        void Check(bool value,string message){if(!value)throw new Exception(message);}
        var guard=new LTDetailPlacementBuildGuard();
        Check(guard.callbackOrder>1&&guard.callbackOrder<100,"Check placement after LOD0 restoration and before stamp stripping");
        var scene=new Scene();var renderer=scene.Root("World").Child("Details").Add(new LTDetailRenderer());
        BuildPipeline.isBuildingPlayer=false;
        renderer.failure=new InvalidOperationException("stale cell (-1, 2)");
        guard.OnProcessScene(scene,null);Check(renderer.validations==0,"Play Mode must not run the build guard");
        foreach(bool withReport in new[]{true,false})
        {
            BuildPipeline.isBuildingPlayer=!withReport;
            bool rejected=false;
            try{guard.OnProcessScene(scene,withReport?new BuildReport():null);}
            catch(BuildFailedException error)
            {
                rejected=true;
                Check(error.Message.Contains(scene.path)&&error.Message.Contains("World/Details")&&
                    error.Message.Contains("stale cell (-1, 2)")&&error.Message.Contains("Запечь зоны детализации"),"Actionable build error");
            }
            Check(rejected,"Both scripted and reported builds must reject invalid placement");
        }
        BuildPipeline.isBuildingPlayer=false;renderer.failure=null;
        renderer.requireBakedPlacementForBuild=false;renderer.failure=new Exception("No bake required");
        int optionalBefore=renderer.validations;
        guard.OnProcessScene(scene,new BuildReport());
        Check(renderer.validations==optionalBefore,"Optional bake permits build without a saved placement");
        renderer.requireBakedPlacementForBuild=true;renderer.failure=null;
        int before=renderer.validations;
        guard.OnProcessScene(scene,new BuildReport());Check(renderer.validations==before+1,"Valid build must check placement");
        renderer.gameObject.active=false;
        guard.OnProcessScene(scene,new BuildReport());Check(renderer.validations==before+2,"Inactive hierarchy must not bypass validation");
        renderer.enabled=false;renderer.failure=new Exception("disabled");
        guard.OnProcessScene(scene,new BuildReport());Check(renderer.validations==before+2,"Explicitly disabled renderer is excluded");
        Console.WriteLine("PASS detail placement build callback: Play bypass, real/scripted builds, diagnostic, inactive hierarchy, disabled renderer.");
    }
}
