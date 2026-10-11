using System;
using System.Collections.Generic;
using System.IO;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void DetailBatchEditChecks()
    {
        var current = new LTDetailEntry();
        var names = new HashSet<string>();
        foreach (var field in LTDetailBatchPatch.Fields)
        {
            Require(field.info != null && names.Add(field.name), "valid unique allowlist: " + field.name);
            object value = field.info.GetValue(current);
            Require(LTDetailBatchPatch.Validate(current, new Dictionary<string, object>{{field.name, value}}, out _), "default accepted: " + field.name);
            Require(!LTDetailBatchPatch.ValidValue(field, null) && !LTDetailBatchPatch.ValidValue(field, "bad"), "null/type guard: " + field.name);
            if (value is float)
                foreach (float bad in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity,-1f})
                    Require(!LTDetailBatchPatch.ValidValue(field,bad), "invalid scalar: " + field.name);
            if (value is Vector2)
                foreach (var bad in new[]{new Vector2(2,1),new Vector2(float.NaN,2),new Vector2(0,float.PositiveInfinity)})
                    Require(!LTDetailBatchPatch.ValidValue(field,bad), "invalid range: " + field.name);
        }
        foreach (var protectedName in new[]{"id","prefab","densityMask","maskModeConfigured","details","detailDensityMask"})
            Require(!LTDetailBatchPatch.Validate(current,new Dictionary<string,object>{{protectedName,true}},out _),"protected field rejected: "+protectedName);
        var patch = new Dictionary<string,object>{{"density",3f},{"cullDistance",10f}};
        Require(!LTDetailBatchPatch.Validate(current,patch,out _),"lower cull cannot silently change unselected fade");
        Require(current.density==2&&current.cullDistance==50&&current.fadeStart==35,"validation never mutates");
        patch["fadeStart"]=5f;
        Require(LTDetailBatchPatch.Validate(current,patch,out _),"coherent selected pair accepted");
        patch["fadeStart"]=11f;
        Require(!LTDetailBatchPatch.Validate(current,patch,out _),"conflicting selected pair rejected");
        Require(!LTDetailBatchPatch.ValidValue(LTDetailBatchPatch.Find("densityMaskMode"),(LTDetailMaskMode)99),"invalid mode rejected");
        Require(!LTDetailBatchPatch.ValidValue(LTDetailBatchPatch.Find("category"),(LTDetailCategory)8),"unknown category bits rejected");
        var unique=LTDetailBatchPatch.UniqueIds(new[]{"a","b","c","b","",null});
        Require(unique.SetEquals(new[]{"a","c"}),"ambiguous/empty IDs excluded");
        var selection=new HashSet<string>{"a","c"};
        selection.IntersectWith(LTDetailBatchPatch.UniqueIds(new[]{"c","b","a"}));
        Require(selection.SetEquals(new[]{"a","c"}),"selection follows IDs after reorder");
        selection.IntersectWith(LTDetailBatchPatch.UniqueIds(new[]{"c","new"}));
        Require(selection.SetEquals(new[]{"c"}),"deleted selection not inherited by replacement row");
        string editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTDetailBatchEditor.cs");
        Require(editor.IndexOf("LTDetailBatchPatch.Validate(current, changes")<editor.IndexOf("foreach (var entry in targets)"),"preflight all rows before writes");
        Require(editor.Contains("field.name == \"densityMaskMode\"")&&editor.Contains("FindPropertyRelative(\"maskModeConfigured\").boolValue = true"),"explicit mask mode persists over legacy flag");
        Require(editor.Contains("Undo.IncrementCurrentGroup()")&&editor.Contains("Undo.CollapseUndoOperations(undo)")&&editor.Contains("owner.ApplyModifiedProperties()"),"isolated serialized Undo commit");
        Require(editor.Contains("ConditionalWeakTable")&&!editor.Contains("SaveAssets(")&&!editor.Contains("SaveScene(")&&!editor.Contains("RegenerateIdentity(")&&!editor.Contains("ValidateAll("),"UI-only state, no save/identity rewrite/global validation");
        Console.WriteLine("DETAIL BATCH PASS: "+names.Count+" allowlisted fields; value/range and identity guards; coupled-distance validation; selection survives reorder, rejects stale/ambiguous IDs; editor write/Undo contracts. Native serialization/UI/Undo require Unity.");
    }
}
