using System;
using System.IO;
using LocalTerrainPrototype;

partial class Checks
{
    static void StampManagerChecks()
    {
        var both=LTStampManagerRules.Classify(true,true);
        Require(both==(LTStampManagerRules.Kind.Height|LTStampManagerRules.Kind.Density),"mixed stamp classified once with two roles");
        int cases=0;
        foreach(var kind in new[]{LTStampManagerRules.Kind.None,LTStampManagerRules.Kind.Height,LTStampManagerRules.Kind.Density,both,LTStampManagerRules.Kind.Hole})
        foreach(bool active in new[]{false,true})foreach(bool warning in new[]{false,true})
        for(int type=0;type<4;type++)for(int state=0;state<4;state++)
        {
            bool expectedType=type==0||type==1&&(kind&LTStampManagerRules.Kind.Height)!=0||type==2&&(kind&LTStampManagerRules.Kind.Density)!=0||type==3&&kind==LTStampManagerRules.Kind.Hole;
            bool expectedState=state==0||state==1&&active||state==2&&!active||state==3&&warning;
            Require(LTStampManagerRules.Matches(kind,active,warning,"Group/Большая Дюна","Высота",null,type,state)==(expectedType&&expectedState),"type/state truth table");cases++;
        }
        foreach(string query in new[]{"дюна","ДЮНА","  group/  ","высота",""})
            Require(LTStampManagerRules.Matches(both,true,false,"Group/Большая Дюна","Высота",query,0,0),"case-insensitive name/path/type search");
        Require(!LTStampManagerRules.Matches(both,true,false,"Group/Большая Дюна","Высота","дырка",0,0),"unmatched search hidden");
        Require(!LTStampManagerRules.Matches(both,true,false,"Group/Большая Дюна","Высота","Дюна",3,0),"search cannot bypass type filter");
        Require(LTStampManagerRules.ValidRemap(0,1,.01f)&&!LTStampManagerRules.ValidRemap(1,0,1)&&!LTStampManagerRules.ValidRemap(0,0,1),"density remap range");
        foreach(float bad in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity})
        {
            Require(!LTStampManagerRules.Positive(bad)&&!LTStampManagerRules.NonNegative(bad),"nonfinite scalar warning");
            Require(!LTStampManagerRules.ValidRemap(bad,1,1)&&!LTStampManagerRules.ValidRemap(0,bad,1)&&!LTStampManagerRules.ValidRemap(0,1,bad),"nonfinite remap warning");
        }
        Require(!LTStampManagerRules.Positive(0)&&LTStampManagerRules.NonNegative(0),"zero lock valid but zero size invalid");
        string model=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTStampManagerModel.cs");
        string window=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTStampManagerWindow.cs");
        Require(model.Contains("world.generatedRoot")&&model.Contains("GetComponent<LTWorld>()")&&model.Contains("GetComponent<LTRoadGenerated>()"),"collection prunes generated and nested worlds");
        Require(model.Contains("Undo.RecordObject(stamp,")&&model.Contains("Undo.RecordObject(stamp.gameObject,")&&model.Contains("RecordPrefabInstancePropertyModifications(target)"),"component/name Undo and prefab override integration");
        Require(model.Contains("GameObjectUtility.DuplicateGameObject(stamp.gameObject)")&&model.Contains("GetNearestPrefabInstanceRoot")&&model.Contains("CanDuplicate(world, stamp)"),"native duplication ownership guard");
        Require(model.Contains("IsPreviewScene")&&model.Contains("isPlayingOrWillChangePlaymode")&&model.Contains("IsPersistent(stamp)"),"mutation mode/asset/preview guards");
        Require(model.Contains("!SceneWorld(world) || !stamp ||"),"destroyed Unity reference checked before property access");
        Require(!model.Contains(".GetPixels(")&&!model.Contains(".vertices")&&!model.Contains("CaptureHoleSnapshot(")&&!model.Contains(".SaveAssets(")&&!model.Contains(".SaveScene(")&&!model.Contains("LTEditorEngine.Refresh("),"listing is lightweight, does not build/save/read mesh payload");
        foreach(string evt in new[]{"Selection.selectionChanged","EditorApplication.hierarchyChanged","EditorApplication.projectChanged","Undo.undoRedoPerformed","EditorApplication.playModeStateChanged","EditorApplication.update"})
            Require(window.Contains(evt+" +=")&&window.Contains(evt+" -="),"balanced window subscription lifetime");
        Require(window.Contains("int first =")&&window.Contains("int last =")&&window.Contains("for (int i = first; i < last; i++)"),"only viewport rows draw");
        Console.WriteLine($"STAMP MANAGER PASS: {cases} filter/state combinations, mixed roles, Cyrillic/path/type search, validation policy, read-only collection contracts and action/lifetime guards. Unity hierarchy, native Undo and UI are separate checks.");
    }
}
