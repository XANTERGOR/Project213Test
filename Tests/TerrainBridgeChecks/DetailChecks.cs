using System;
using System.Collections.Generic;
using System.IO;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void DetailChecks()
    {
        var shared=new LTDetailDensityMask{enabled=true,patchSize=9,patchCoverage=.3f,
            patchSoftness=.4f,patchMinimumDensity=.2f,patchSeed=91};
        var inherited=new LTDetailEntry();
        Require(inherited.EffectiveMaskMode==LTDetailMaskMode.Common,"new entries inherit common mask");
        inherited.ResolveDensityMask(shared);
        Require(inherited.densityMask&&inherited.patchSize==9&&inherited.patchSeed==91&&inherited.patchCoverage==.3f,
            "common settings resolved into generation snapshot");
        shared.patchSize=17;inherited.ResolveDensityMask(shared);
        Require(inherited.EffectiveMaskMode==LTDetailMaskMode.Common&&inherited.patchSize==17,
            "resolving inherited mask repeatedly never converts it into own");
        shared.enabled=false;inherited.ResolveDensityMask(shared);
        Require(!inherited.densityMask,"disabled common mask bypasses inheritance");
        var own=new LTDetailEntry{densityMaskMode=LTDetailMaskMode.Own,patchSize=4,patchSeed=123};
        own.ResolveDensityMask(shared);
        Require(own.densityMask&&own.patchSize==4&&own.patchSeed==123,"own settings independent of disabled common");
        var none=new LTDetailEntry{densityMaskMode=LTDetailMaskMode.None};
        shared.enabled=true;none.ResolveDensityMask(shared);
        Require(!none.densityMask,"none bypasses enabled common");
        var legacy=new LTDetailEntry{densityMask=true,patchSize=11,patchCoverage=.42f,patchSeed=44};
        legacy.OnAfterDeserialize();
        Require(legacy.densityMaskMode==LTDetailMaskMode.Own&&legacy.patchSize==11&&legacy.patchCoverage==.42f&&legacy.patchSeed==44,
            "legacy enabled masks migrate without parameter loss");
        legacy.densityMaskMode=LTDetailMaskMode.Common;legacy.OnAfterDeserialize();legacy.ResolveDensityMask(shared);
        Require(legacy.EffectiveMaskMode==LTDetailMaskMode.Common&&legacy.patchSize==17,
            "legacy bool never overrides explicit common mode after migration");
        var legacyOff=new LTDetailEntry();legacyOff.OnAfterDeserialize();
        Require(legacyOff.EffectiveMaskMode==LTDetailMaskMode.Common,"legacy off inherits disabled-by-default common");
        shared.patchSize=-1;shared.patchCoverage=3;shared.patchSoftness=-2;shared.patchMinimumDensity=4;shared.Validate();
        Require(shared.patchSize==.1f&&shared.patchCoverage==1&&shared.patchSoftness==0&&shared.patchMinimumDensity==1,
            "common mask validation");
        foreach(var field in typeof(LTDetailDensityMask).GetFields())
            Require(Attribute.GetCustomAttribute(field,typeof(TooltipAttribute))!=null,"shared mask tooltip: "+field.Name);
        var patch=new LTDetailEntry{densityMask=true,patchSize=6,patchCoverage=.5f,
            patchSoftness=.2f,patchMinimumDensity=0,patchSeed=71};
        int empty=0,full=0,changed=0;
        for(int z=-32;z<32;z++)for(int x=-32;x<32;x++)
        {
            float px=x*1.37f,pz=z*1.61f;
            float a=LTDetailMath.DensityMask(patch,px,pz,123);
            Require(a>=0&&a<=1,"density mask bounded");
            Require(a==LTDetailMath.DensityMask(patch,px,pz,123),"mask reproducible after reload");
            if(a==0)empty++;if(a==1)full++;
            patch.patchSeed++;
            if(Math.Abs(a-LTDetailMath.DensityMask(patch,px,pz,123))>.01f)changed++;
            patch.patchSeed--;
            patch.patchCoverage=.7f;
            Require(LTDetailMath.DensityMask(patch,px,pz,123)>=a,"increasing coverage only adds density");
            patch.patchCoverage=.5f;
            patch.patchSize=12;
            Require(Math.Abs(a-LTDetailMath.DensityMask(patch,px*2,pz*2,123))<.00001f,"patch scale in terrain metres");
            patch.patchSize=6;
        }
        Require(empty>100&&full>100&&changed>100,"mask produces coherent gaps and full patches, seed changes pattern");
        for(int edge=-10;edge<=10;edge++)
        {
            float p=edge*6;
            Require(Math.Abs(LTDetailMath.DensityMask(patch,p-.0001f,2.3f,123)-
                LTDetailMath.DensityMask(patch,p+.0001f,2.3f,123))<.002f,"continuous X noise boundaries including negative coordinates");
            Require(Math.Abs(LTDetailMath.DensityMask(patch,2.3f,p-.0001f,123)-
                LTDetailMath.DensityMask(patch,2.3f,p+.0001f,123))<.002f,"continuous Z noise boundaries");
        }
        patch.patchMinimumDensity=.1f;patch.patchCoverage=0;
        Require(LTDetailMath.DensityMask(patch,9,17,123)==.1f,"zero coverage leaves requested minimum");
        patch.patchCoverage=1;
        Require(LTDetailMath.DensityMask(patch,9,17,123)==1,"full coverage unchanged");
        patch.patchCoverage=.5f;patch.patchMinimumDensity=1;
        Require(LTDetailMath.DensityMask(patch,9,17,123)==1,"full minimum disables thinning");
        patch.patchMinimumDensity=0;patch.patchSoftness=0;
        float hard=LTDetailMath.DensityMask(patch,9,17,123);
        Require(hard==0||hard==1,"hard edge is binary");
        patch.densityMask=false;
        Require(LTDetailMath.DensityMask(patch,9,17,123)==1&&!new LTDetailEntry().densityMask,"opt-out preserves existing distribution");
        patch.patchSize=-1;patch.patchCoverage=2;patch.patchSoftness=-1;patch.patchMinimumDensity=2;
        patch.Validate(new HashSet<string>());
        Require(patch.patchSize==.1f&&patch.patchCoverage==1&&patch.patchSoftness==0&&patch.patchMinimumDensity==1,"mask parameter validation");
        var cameras=new List<Vector2>{new Vector2(2048,2048)};
        var plan=new List<Vector2Int>();
        Require(LTDetailStreaming.Plan(cameras,new Vector2(4096,4096),16,182,4096,plan),"large world plans only local cells");
        Require(plan.Count>0&&plan.Count<600,"streaming count independent of 65536 world cells");
        var original=new HashSet<Vector2Int>(plan);
        float previous=-1;
        foreach(var key in plan)
        {
            float distance=LTDetailStreaming.DistanceSquared(cameras[0],LTDetailStreaming.CellRect(key,16,new Vector2(4096,4096)));
            Require(distance>=previous,"nearest cells first");previous=distance;
        }
        cameras.Add(cameras[0]);
        Require(LTDetailStreaming.Plan(cameras,new Vector2(4096,4096),16,182,4096,plan)&&original.SetEquals(plan),"overlapping cameras deduplicate");
        cameras[1]=new Vector2(64,64);
        Require(LTDetailStreaming.Plan(cameras,new Vector2(4096,4096),16,182,4096,plan)&&original.IsSubsetOf(plan),"second camera adds area");
        Require(!LTDetailStreaming.Plan(cameras,new Vector2(4096,4096),16,182,10,plan),"explicit active cell budget");
        cameras.Clear();cameras.Add(new Vector2(-1000,-1000));
        Require(LTDetailStreaming.Plan(cameras,new Vector2(4096,4096),16,100,4096,plan)&&plan.Count==0,"outside camera does not clamp into world");
        cameras[0]=new Vector2(2048,2048);
        LTDetailStreaming.Plan(cameras,new Vector2(4096,4096),16,182,4096,plan);
        Require(original.SetEquals(plan),"returning camera restores same cell coordinates");
        Require(!LTDetailStreaming.Expired(false,4,0,5)&&LTDetailStreaming.Expired(false,5,0,5)&&
            !LTDetailStreaming.Expired(true,100,0,5),"unload delay and active retention");
        foreach(var field in typeof(LTDetailEntry).GetFields())
        {
            var tooltip=(TooltipAttribute)Attribute.GetCustomAttribute(field,typeof(TooltipAttribute));
            Require(tooltip!=null&&!string.IsNullOrWhiteSpace(tooltip.tooltip),"detail parameter tooltip: "+field.Name);
        }
        var inspector=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTDetailInspector.cs");
        Require(inspector.Contains("DrawMaskPreview(owner,mask)")&&
            inspector.Contains("LTDetailMath.DensityMask(entry,(x+.5f)/128*preview.metres")&&
            inspector.Contains("mask.FindPropertyRelative(\"patchMinimumDensity\").floatValue"),
            "inspector preview samples production density mask with current serialized settings");
        Require(inspector.Contains("preview.key!=key")&&inspector.Contains("AssemblyReloadEvents.beforeAssemblyReload+=ClearPreviews")&&
            inspector.Contains("previews.Count>=8")&&inspector.Contains("Object.DestroyImmediate(preview.texture)"),
            "preview uses bounded texture cache and cleanup");
        Require(inspector.Contains("DragAndDrop.objectReferences")&&inspector.Contains("new LTDetailEntry{prefab=prefab}")&&
            inspector.Contains("Undo.RecordObject")&&inspector.Contains("existing.Add(prefab)")&&
            inspector.Contains("EditorUtility.IsPersistent(go)"),
            "bulk detail drop: fresh defaults, undo, deduplication and asset-only guard");
        Require(LTDetailMath.TextHash("grass")==LTDetailMath.TextHash("grass"),"stable detail text hash");
        var seen=new HashSet<uint>();
        for(int x=-10;x<10;x++)for(int z=-10;z<10;z++)for(int i=0;i<8;i++)
        {
            uint id=LTDetailMath.Candidate(123,"layer","entry",x,z,i);
            Require(seen.Add(id),"distinct candidate fixture identities");
            Require(id==LTDetailMath.Candidate(123,"layer","entry",x,z,i),"repeatable candidate identity");
            float a=LTDetailMath.Unit(id,1),b=LTDetailMath.Unit(id,2);
            Require(a>=0&&a<1&&b>=0&&b<1,"detail random channels in range");
            Require(!LTDetailMath.Accept(id,0)&&LTDetailMath.Accept(id,1),"density endpoints");
            Require(!LTDetailMath.Accept(id,.2f)||LTDetailMath.Accept(id,.7f),"density selects nested stable subset");
        }
        Require(LTDetailMath.Candidate(1,"a","b",3,4,5)!=LTDetailMath.Candidate(1,"a","b",3,5,4),
            "cell and candidate index must not be interchangeable hash inputs");
        for(int x=0;x<20;x++)for(int z=0;z<20;z++)for(int i=0;i<9;i++)
        {
            uint id=LTDetailMath.Candidate(19,"layer","grass",x,z,i);
            Require(!LTDetailMath.DensityAccept(id,i,2.35f)||LTDetailMath.DensityAccept(id,i,8.7f),
                "increasing absolute density preserves positions across integer candidate counts");
            Require(!LTDetailMath.DensityAccept(id,i,0),"zero density rejects all");
            Require(LTDetailMath.DensityAccept(id,i,9),"whole density accepts each owned slot");
        }
        var low=new HashSet<uint>();var high=new HashSet<uint>();
        void Collect(int cellSize,HashSet<uint> result)
        {
            for(int cz=0;cz<24;cz+=cellSize)for(int cx=0;cx<24;cx+=cellSize)
            for(int z=cz;z<Math.Min(24,cz+cellSize);z++)for(int x=cx;x<Math.Min(24,cx+cellSize);x++)
            for(int i=0;i<3;i++)
            {
                uint id=LTDetailMath.Candidate(19,"layer","grass",x,z,i);
                if(LTDetailMath.DensityAccept(id,i,2.35f))result.Add(id);
            }
        }
        Collect(4,low);Collect(7,high);
        Require(low.SetEquals(high),"visibility-cell size does not alter generated candidates");
        var weights=new[]{.4f,.6f};var heights=new[]{.2f,.8f};
        LTDetailMath.HeightBlend(weights,heights,2,1);
        Require(weights[0]==0&&Math.Abs(weights[1]-1)<.00001f,"height dominance matches material blend");
        weights=new[]{.4f,.6f};LTDetailMath.HeightBlend(weights,heights,2,0);
        Require(Math.Abs(weights[0]-.4f)<.00001f&&Math.Abs(weights[1]-.6f)<.00001f,"disabled height blend preserves coverage");
        weights=new[]{.25f,.25f};heights=new[]{.5f,.5f};
        LTDetailMath.HeightBlend(weights,heights,2,.5f);
        Require(Math.Abs(weights[0]-.5f)<.00001f&&Math.Abs(weights[1]-.5f)<.00001f,"height blend normalizes weights");
        Require(LTDetailMath.Suppress(.8f,1,false)==0&&LTDetailMath.Suppress(.8f,1,true)==.8f,"remove/replace suppress while add preserves");
        Require(Math.Abs(LTDetailMath.Suppress(.8f,.25f,false)-.6f)<.00001f,"feathered stamp suppression");
        float screen=LTDetailMath.ScreenHeight(2,10,false,5,60,1);
        Require(Math.Abs(screen-.17320508f)<.00001f,"perspective LOD projected height");
        Require(LTDetailMath.ScreenHeight(2,10,true,5,60,1)==LTDetailMath.ScreenHeight(2,100,true,5,60,1),"orthographic LOD independent of distance");
        Require(Math.Abs(LTDetailMath.ScreenHeight(2,10,false,5,60,2)-2*screen)<.00001f,"LOD bias");
        Require(LTDetailMath.AreaWeight(0,0,10,20,true,.2f)==1,"stamp center");
        Require(LTDetailMath.AreaWeight(5,0,10,20,true,.2f)==0,"ellipse boundary");
        Require(LTDetailMath.AreaWeight(4,8,10,20,true,0)==0,"ellipse excludes rectangle corner");
        Require(LTDetailMath.AreaWeight(4,8,10,20,false,0)==1,"rectangle includes corner interior");
        float feather=LTDetailMath.AreaWeight(4.5f,0,10,20,true,.2f);
        Require(feather>0&&feather<1,"stamp feather");
        var first=new LTDetailEntry{density=-1,scaleRange=new Vector2(2,-1),fadeStart=100,cullDistance=25};
        var second=new LTDetailEntry();
        var entries=new List<LTDetailEntry>{first,second};
        LTDetailEntry.ValidateAll(entries);
        string id0=first.Id,id1=second.Id;
        Require(!string.IsNullOrEmpty(id0)&&id0!=id1,"entry identities assigned");
        entries.Reverse();LTDetailEntry.ValidateAll(entries);
        Require(first.Id==id0&&second.Id==id1,"reordering preserves detail identities");
        Require(first.density==0&&first.scaleRange.x>0&&first.scaleRange.y==2&&first.fadeStart==25,"detail settings clamped");
        // Simulate Unity copying serialized fields when duplicating a list element.
        typeof(LTDetailEntry).GetField("id",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(second,id0);
        entries=new List<LTDetailEntry>{first,second};LTDetailEntry.ValidateAll(entries);
        Require(first.Id==id0&&second.Id!=id0,"duplicate list entry gets independent identity");
        string source=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTDetailPrefab.cs");
        Require(source.Contains("renderer.shadowCastingMode")&&source.Contains("renderer.receiveShadows")&&
            source.Contains("renderer.renderingLayerMask")&&source.Contains("renderer.sharedMaterials"),
            "prefab recipe preserves renderer shadow/material settings");
        Require(!source.Contains("Object.Instantiate(")&&!source.Contains(".enableInstancing="),
            "recipe validation must not create instances or mutate materials");
        var renderer=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTDetailRenderer.cs");
        Require(renderer.Contains("weight*=LTDetailMath.DensityMask(entry,px,pz,source.seed)"),"mask multiplies final acceptance using terrain coordinates");
        Require(renderer.Contains("Graphics.RenderMeshInstanced")&&!renderer.Contains("Object.Instantiate("),
            "instanced backend without per-instance GameObjects");
        Require(renderer.Contains("shadowCastingMode=part.castShadows")&&renderer.Contains("receiveShadows=part.receiveShadows")&&
            renderer.Contains("renderingLayerMask=part.renderingLayerMask"),"draw inherits per-part shadow/rendering flags");
        Require(renderer.Contains("part.castShadows==ShadowCastingMode.Off&&!GeometryUtility.TestPlanesAABB"),
            "offscreen shadowcasters not rejected by camera frustum");
        Require(renderer.Contains("if(old!=null&&old.hash==hash)")&&renderer.Contains("cells[key]=cell;"),
            "unchanged cells reused and completed cells swapped");
        Console.WriteLine("PASS details: stable candidates, absolute density subsets, cell-size invariance, height blend, stamp suppression, perspective/ortho LOD, identity/clamping, instanced/shadow/lifecycle source contracts; no live renderer/GPU test.");
    }
}
