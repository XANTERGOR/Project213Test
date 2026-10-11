using System;

namespace LocalTerrainPrototype
{
    internal static class LTEditorReloadDiagnostics
    {
        // Explanation only; does not replace or relax the production guards.
        internal static string Pending(bool initialized,bool densityPending,int dirty,int colliders,
            bool buildActive,int lods,bool paintPending,bool rockSeamsPending)
        {
            var reasons=new System.Collections.Generic.List<string>();
            if(!initialized)reasons.Add("state-not-initialized");
            if(densityPending)reasons.Add("density-restore-pending");
            if(dirty!=0)reasons.Add("dirty-chunks="+dirty);
            if(colliders!=0)reasons.Add("pending-colliders="+colliders);
            if(buildActive)reasons.Add("geometry-build-active");
            if(lods!=0)reasons.Add("pending-lods="+lods);
            if(paintPending)reasons.Add("paint-pending");
            if(rockSeamsPending)reasons.Add("rock-seam-bake-pending");
            return reasons.Count==0?null:string.Join(", ",reasons);
        }
    }
    // Session-only: never accept a first/opened world merely because meshes exist.
    [Serializable]
    internal sealed class LTEditorReloadProof
    {
        public const int Version=1;
        public int version=Version;
        public string code,owner,inputs,outputs;
        public bool Matches(string nextCode,string nextOwner,string nextInputs,string nextOutputs,out string reason)
        {
            reason=version!=Version?"формат контрольных данных изменился":
                string.IsNullOrEmpty(code)||code!=nextCode?"изменился код системы terrain":
                string.IsNullOrEmpty(owner)||owner!=nextOwner?"изменилась сцена или владелец":
                string.IsNullOrEmpty(inputs)||inputs!=nextInputs?"изменились входные данные":
                string.IsNullOrEmpty(outputs)||outputs!=nextOutputs?"изменилась или отсутствует готовая геометрия":null;
            return reason==null;
        }
    }
}
