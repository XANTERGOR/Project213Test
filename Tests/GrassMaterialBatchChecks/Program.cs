using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

static class Program
{
    const string Strength="_LTGrassTerrainStrength",Height="_LTGrassTerrainHeight",
        Patch="_LTGrassPatchStrength",Root="_LTGrassRootStrength",Tint="_LTGrassTintA";
    static int assertions;
    static void Check(bool condition,string label)
    {
        assertions++;if(!condition)throw new Exception(label);
    }
    static bool Supported(Material m)=>m&&m.HasProperty(Strength);
    static Material Make(float height,float patch,float root,Color tint)
    {
        var m=new Material();m.floats[Strength]=0;m.floats[Height]=height;
        m.floats[Patch]=patch;m.floats[Root]=root;m.colors[Tint]=tint;return m;
    }
    static void ResetRecords(){Undo.records.Clear();EditorUtility.dirty.Clear();}
    static void Main()
    {
        var a=Make(.3f,.1f,.2f,new Color(.5f,.6f,.7f));
        var b=Make(.8f,.4f,.6f,new Color(.8f,.7f,.6f));
        var c=Make(1.2f,.9f,.8f,new Color(.3f,.4f,.5f));
        var unsupported=new Material();
        var destroyed=Make(.1f,0,0,new Color());destroyed.destroyed=true;
        var prefab=new GameObject{renderers=new[]{
            new MeshRenderer{sharedMaterials=new[]{a,b,a,null}},
            new MeshRenderer{sharedMaterials=new[]{c,b,unsupported,destroyed}}}};
        var gathered=LTGrassMaterialBatch.Gather(new Object[]{a,prefab,b,null,new Object()});
        Check(gathered.SequenceEqual(new[]{a,b,c,unsupported}),"gather distinct prefab/LOD material slots, skip missing and destroyed");
        Check(prefab.includedInactive,"gather includes inactive children");
        var entries=new List<LTGrassMaterialEntry>();
        Check(LTGrassMaterialBatch.Add(entries,gathered,Supported)==3,"add only supported materials");
        Check(entries.All(e=>e.included),"new entries checked");
        Check(a.writes+b.writes+c.writes==0&&Undo.records.Count==0&&EditorUtility.dirty.Count==0,"adding has no material side effects");
        entries[2].included=false;
        Check(LTGrassMaterialBatch.Add(entries,new[]{c,a,null,destroyed},Supported)==0,"duplicates ignored");
        Check(!entries[2].included,"adding duplicate preserves unchecked state");
        entries.Add(null);
        entries.Add(new LTGrassMaterialEntry{material=null});
        entries.Add(new LTGrassMaterialEntry{material=unsupported});
        entries.Add(new LTGrassMaterialEntry{material=destroyed});
        entries.Add(new LTGrassMaterialEntry{material=a});
        var selected=LTGrassMaterialBatch.Selected(entries,Supported);
        Check(selected.SequenceEqual(new[]{a,b}),"selected filters incompatible, missing, unchecked and duplicate entries");
        Check(!LTGrassMaterialBatch.FloatMixed(selected,Strength),"same float not mixed");
        Check(LTGrassMaterialBatch.FloatMixed(selected,Height),"different heights mixed");
        Check(LTGrassMaterialBatch.ColorMixed(selected,Tint),"different colors mixed");
        Check(!LTGrassMaterialBatch.FloatMixed(new[]{a},Height),"single float not mixed");
        Check(!LTGrassMaterialBatch.ColorMixed(new[]{a},Tint),"single color not mixed");
        Check(LTGrassMaterialBatch.SetFloat(selected,Strength,.5f),"batch strength changed");
        Check(a.GetFloat(Strength)==.5f&&b.GetFloat(Strength)==.5f,"both checked targets edited");
        Check(c.GetFloat(Strength)==0&&c.writes==0,"unchecked target untouched");
        Check(a.GetFloat(Height)==.3f&&b.GetFloat(Height)==.8f,"individual heights preserved");
        Check(a.GetFloat(Patch)==.1f&&b.GetFloat(Patch)==.4f,"individual variation preserved");
        Check(a.GetFloat(Root)==.2f&&b.GetFloat(Root)==.6f,"individual roots preserved");
        Check(Undo.records.Count==1&&Undo.records[0].SequenceEqual(selected),"one Undo record contains all changed targets");
        Check(EditorUtility.dirty.SequenceEqual(selected),"only changed materials marked dirty");
        ResetRecords();
        Check(!LTGrassMaterialBatch.SetFloat(selected,Strength,.5f),"unchanged field is no-op");
        Check(Undo.records.Count==0&&EditorUtility.dirty.Count==0,"no-op has no Undo/dirty effects");
        foreach(float bad in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity})
            Check(!LTGrassMaterialBatch.SetFloat(selected,Strength,bad),"reject invalid float");
        Check(a.GetFloat(Strength)==.5f&&Undo.records.Count==0,"invalid edits have no side effects");
        Check(!LTGrassMaterialBatch.SetFloat(Array.Empty<Material>(),Strength,.1f),"empty batch no-op");
        Check(LTGrassMaterialBatch.SetFloat(new[]{a,b,a,null,unsupported,destroyed},Strength,.7f),"safe mixed input");
        Check(Undo.records.Count==1&&Undo.records[0].Length==2,"duplicate input recorded once");
        Check(a.writes==2&&b.writes==2&&unsupported.writes==0&&destroyed.writes==0,"duplicate input written once, incompatible skipped");
        ResetRecords();
        a.floats[Strength]=.6f;
        Check(LTGrassMaterialBatch.SetFloat(selected,Strength,.7f),"only differing material edited");
        Check(Undo.records.Count==1&&Undo.records[0].SequenceEqual(new[]{a}),"Undo excludes unchanged peer");
        Check(EditorUtility.dirty.SequenceEqual(new[]{a}),"dirty excludes unchanged peer");
        ResetRecords();
        var tint=new Color(.4f,.6f,.8f);
        Check(LTGrassMaterialBatch.SetColor(new[]{a,b,a,unsupported,null,destroyed},Tint,tint),"color changed on compatible targets");
        Check(a.GetColor(Tint).Equals(tint)&&b.GetColor(Tint).Equals(tint),"color assigned");
        Check(!LTGrassMaterialBatch.ColorMixed(selected,Tint),"uniform color no longer mixed");
        Check(a.GetFloat(Height)==.3f&&b.GetFloat(Height)==.8f&&a.GetFloat(Strength)==.7f,"color edit leaves floats alone");
        Check(Undo.records.Count==1&&EditorUtility.dirty.Count==2,"color single Undo, distinct targets");
        ResetRecords();
        Check(!LTGrassMaterialBatch.SetColor(selected,Tint,tint),"same color no-op");
        Check(Undo.records.Count==0&&EditorUtility.dirty.Count==0,"same color no side effects");
        Check(LTGrassMaterialBatch.Change(selected,m=>{m.SetFloat(Patch,0);m.SetFloat(Root,0);}),"multi-property action");
        Check(a.GetFloat(Patch)==0&&b.GetFloat(Patch)==0&&a.GetFloat(Root)==0&&b.GetFloat(Root)==0,"action applied to both materials");
        Check(a.GetFloat(Height)==.3f&&b.GetFloat(Height)==.8f&&c.GetFloat(Patch)==.9f,"action preserves unrelated/unchecked values");
        Check(Undo.records.Count==1&&Undo.records[0].Length==2,"action grouped for Undo");
        ResetRecords();entries.Clear();
        Check(LTGrassMaterialBatch.Selected(entries,Supported).Length==0,"clear list removes selection");
        Check(Undo.records.Count==0&&EditorUtility.dirty.Count==0&&a.GetFloat(Height)==.3f,"clear list does not mutate assets");
        Check(!LTGrassMaterialBatch.Change(new[]{null,destroyed},_=>throw new Exception("must not execute")),"invalid targets no-op");
        Console.WriteLine($"PASS: {assertions} managed batch-material assertions against production helper. Native IMGUI, Unity Undo and live rendering not exercised.");
    }
}
