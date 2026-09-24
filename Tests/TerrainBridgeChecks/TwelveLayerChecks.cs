using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void TwelveLayerChecks()
    {
        const int count=12;
        foreach(int slot in Enumerable.Range(0,count))
        {
            var weights=new float[count];weights[0]=1;
            LTPaintMath.Composite(weights,slot,1);
            Require(weights[slot]==1&&weights.Sum()==1,"full paint isolates slot "+slot);
            LTPaintMath.Composite(weights,slot,.35f);
            Require(weights[slot]==1&&weights.Sum()==1,"repeat paint preserves slot "+slot);
            var heights=Enumerable.Repeat(.5f,count).ToArray();
            Require(Math.Abs(LTPaintMath.DisplacementVisibility(weights,heights,1,1<<slot)-1)<1e-6,
                "displacement recognizes slot "+slot);
            Require(LTPaintMath.DisplacementVisibility(weights,heights,1,1<<((slot+1)%count))==0,
                "displacement excludes unrelated slot "+slot);
        }
        foreach(int winner in new[]{8,11})
        {
            var weights=Enumerable.Repeat(1f/count,count).ToArray();
            var heights=new float[count];heights[winner]=1;
            var original=(float[])weights.Clone();
            Require(Math.Abs(LTPaintMath.DisplacementVisibility(weights,heights,1,1<<winner)-1)<1e-6,
                "high-slot height dominance "+winner);
            Require(weights.SequenceEqual(original),"visibility leaves all twelve paint weights unchanged");
            LTDetailMath.HeightBlend(weights,heights,count,1);
            Require(Math.Abs(weights[winner]-1)<1e-6&&weights.Where((w,i)=>i!=winner).All(w=>w==0),
                "height blend selects high slot "+winner);
        }
        var random=new System.Random(120811);
        for(int trial=0;trial<1000;trial++)
        {
            var weights=Enumerable.Range(0,count).Select(_=>(float)random.NextDouble()+.001f).ToArray();
            float sum=weights.Sum();for(int i=0;i<count;i++)weights[i]/=sum;
            var heights=Enumerable.Range(0,count).Select(_=>(float)random.NextDouble()).ToArray();
            var original=(float[])weights.Clone();float blend=(trial%5)/4f;
            LTDetailMath.HeightBlend(weights,heights,count,blend);
            Require(Math.Abs(weights.Sum()-1)<1e-5&&weights.All(w=>float.IsFinite(w)&&w>=0&&w<=1),
                "twelve-slot height blend stays normalized and finite");
            for(int slot=0;slot<count;slot++)
                Require(Math.Abs(LTPaintMath.DisplacementVisibility(original,heights,blend,1<<slot)-weights[slot])<1e-5,
                    "detail and displacement height blend agree at slot "+slot);
        }
        var equal=Enumerable.Repeat(.25f,count).ToArray();
        LTDetailMath.HeightBlend(equal,Enumerable.Repeat(.5f,count).ToArray(),count,.5f);
        Require(equal.All(w=>Math.Abs(w-1f/count)<1e-6),"all twelve equal weights normalize");
        var empty=new float[count];LTDetailMath.HeightBlend(empty,new float[count],count,1);
        Require(empty.All(w=>w==0),"empty height blend stays finite and empty");
        foreach(int slot in new[]{8,11})
        {
            var first=new Color32[25];var second=new Color32[25];var third=new Color32[25];
            third[12]=slot==8?new Color32(1,0,0,0):new Color32(0,0,0,1);
            var mips=LTPaintMath.DisplacementPyramid(first,second,third,5,1<<slot);
            Require(mips[0].Count(p=>p.r>0)==4&&mips[2][0].r==255,"third-map island survives max reduction slot "+slot);
            Require(LTPaintMath.DisplacementPyramid(first,second,third,5,1<<(slot==8?11:8))[2][0].r==0,
                "third-map displacement channel masks remain independent");
        }
        TwelveLayerSourceContracts();
        TerrainTextureBudgetContracts();
        LayerTextureArrayContracts();
        DetailOwnerScopeChecks();
        for(int slot=0;slot<count;slot++)
        {
            var weights=new float[count];weights[slot]=1;
            var settings=new Vector4[count];settings[slot]=new Vector4(.2f,10,24,0);
            Require(Math.Abs(LTDeformationMath.Controls(weights,Enumerable.Repeat(.5f,count).ToArray(),settings,1).x-.2f)<1e-6,
                "deformation recognizes slot "+slot);
        }
        Console.WriteLine("PASS twelve layers: every paint/displacement/deformation slot, slot 8/11 height dominance, 1000 normalized blends and three-weightmap source contracts. No GPU execution.");
    }

    static void DetailOwnerScopeChecks()
    {
        var first=new LTDetailEntry();var copied=new LTDetailEntry();
        LTDetailEntry.ValidateAll(new System.Collections.Generic.List<LTDetailEntry>{first});
        var idField=typeof(LTDetailEntry).GetField("id",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
        idField.SetValue(copied,first.Id);string legacyId=first.Id;
        // Separate lists model entries serialized in distinct layer assets.
        LTDetailEntry.ValidateAll(new System.Collections.Generic.List<LTDetailEntry>{copied});
        Require(first.Id==legacyId&&copied.Id==legacyId,"distinct layer lists preserve shared serialized IDs");
        uint candidate=LTDetailMath.Candidate(42,"layer",legacyId,3,-2,5);
        Require(LTDetailMath.Candidate(42,"layer",copied.Id,3,-2,5)==candidate,"copied layer preserves legacy candidate positions");
        LTDetailEntry.ValidateAll(new System.Collections.Generic.List<LTDetailEntry>{first,copied});
        Require(first.Id==legacyId&&copied.Id!=legacyId,"local duplicate IDs repaired within one list");
        var renderer=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTDetailRenderer.cs");
        int start=renderer.IndexOf("void AddEntries(",StringComparison.Ordinal);
        int end=renderer.IndexOf("foreach(var layer in surface.layers.Keys)",start,StringComparison.Ordinal);
        Require(start>=0&&end>start,"per-owner AddEntries function remains identifiable");
        var add=Regex.Replace(renderer.Substring(start,end-start),@"\s+","");
        Require(add.Contains("varids=newHashSet<string>();")&&add.IndexOf("varids=")<add.IndexOf("foreach(varentryinentries)"),
            "ID set is local to each AddEntries call, before entry iteration");
        Require(add.Contains("stringowner=stamp==null?\"layer\":stamp.id;"),"candidate owner remains legacy layer or stamp ID");
        Require(add.Contains("if(string.IsNullOrEmpty(entry.Id)||!ids.Add(owner+\":\"+entry.Id))thrownewInvalidOperationException("),
            "runtime rejects duplicate and empty IDs within the current owner list");
        var compact=Regex.Replace(renderer,@"\s+","");
        Require(compact.Contains("old.groups.Find(g=>g.source.layer==source.layer&&g.source.owner==source.owner&&g.source.entry.Id==entry.Id)"),
            "scale-only cache lookup cannot reuse a different layer with the same entry ID");
        Require(compact.Contains("LTDetailMath.Candidate(source.seed,source.owner,entry.Id,x,z,i)"),"placement candidate retains owner and entry identity inputs");
        Console.WriteLine("PASS detail owner scope: cross-list copied IDs preserved, local duplicates repaired/rejected, legacy candidates retained and layer-aware cache lookup source contract. No native renderer execution.");
    }

    static void TwelveLayerSourceContracts()
    {
        const string root="Assets/TerrainSystem/LocalTerrain/";
        string Read(string path)=>Regex.Replace(File.ReadAllText(root+path),@"\s+","");
        var runtime=Read("LTPaintRuntime.cs");
        Require(runtime.Contains("constintLayerCapacity=12;"),"CPU layer capacity is twelve");
        var core=Read("Shaders/LTLayerBlendCore.hlsl");
        var domain=Read("Shaders/LTLayerTessellation.hlsl");
        var surface=Read("LTDetailSurface.cs");
        Require(surface.Contains("weights2=newLTDetailSurface.Image(state.weights2)")&&
            surface.Contains("raw[i]=i<4?a[i]:i<8?b[i-4]:c[i-8]"),"detail snapshot captures and unpacks all three maps");
        Require(domain.Contains("{a.x,a.y,a.z,a.w,b.x,b.y,b.z,b.w,c.x,c.y,c.z,c.w}"),"tessellation unpacks all three maps in slot order");
        for(int map=0;map<3;map++)
        {
            string texture="_LTWeights"+map;
            Require(runtime.Contains($"SetTexture(\"{texture}\",state.weights{map})"),"bind weightmap "+map);
            Require(runtime.Contains($"state.weights{map}.SetPixels32(")&&runtime.Contains($"state.weights{map}.Apply(false,false)"),"upload weightmap "+map);
            Require(runtime.Contains($"DestroyOwned(state.weights{map})"),"release weightmap "+map);
            string channels=string.Join(",",Enumerable.Range(map*4,4).Select(i=>$"weights[{i}]"));
            Require(runtime.Contains("newColor("+channels+")"),"RGBA packing covers slots "+map*4+" through "+(map*4+3));
            foreach(var shader in new[]{"Resources/LTEightLayers.shader","Resources/LTEightLayersTessellation.shader","Resources/LTGlobalLayerBake.shader"})
                Require(Read(shader).Contains(texture+"("),shader+" exposes "+texture);
            Require(core.Contains($"SAMPLE_TEXTURE2D_LOD({texture},"),"shared near/bake sampler reads "+texture);
            Require(domain.Contains($"SAMPLE_TEXTURE2D_LOD({texture},"),"tessellation reads "+texture);
        }
        Require(core.Contains("{w0.x,w0.y,w0.z,w0.w,w1.x,w1.y,w1.z,w1.w,w2.x,w2.y,w2.z,w2.w}"),"shader unpacks three RGBA maps in slot order");
        var component=Regex.Match(core,@"int(\w+)=slot(?:&3|%4);");
        string index=component.Groups[1].Value;
        Require(component.Success&&core.Contains($"returnslot<4?_LTLayerSlices0[{index}]:slot<8?_LTLayerSlices1[{index}]:_LTLayerSlices2[{index}];"),
            "twelve local slots map through three world-slice vectors with component indices bounded even in folded branches");
        Require(core.Contains("SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(slot),mip)"),
            "geometry mask helper maps local slot and preserves explicit mip");
        Require(!core.Contains("UnpackNormalScale("),"canonical RGB arrays must not use platform packed-normal decoding");
        for(int slot=0;slot<12;slot++)
        {
            foreach(var mode in new[]{"GRAD","LOD"})
            {
                string suffix=mode=="GRAD"?"dx,dy":"0";
                Require(core.Contains($"colors[{slot}]=SAMPLE_TEXTURE2D_ARRAY_{mode}(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice({slot}),{suffix}).rgb*_LTTint{slot}.rgb"),
                    "color sample uses matching local slot, tint and world slice: "+slot+" / "+mode);
                Require(core.Contains($"normals[{slot}]=UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_{mode}(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice({slot}),{suffix}),_LTSettings{slot}.x)"),
                    "canonical RGB normal uses matching slot and runtime strength: "+slot+" / "+mode);
                Require(core.Contains($"masks[{slot}]=SAMPLE_TEXTURE2D_ARRAY_{mode}(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice({slot}),{suffix}).rgb"),
                    "mask sample uses matching local/world slice: "+slot+" / "+mode);
            }
            Require(domain.Contains($"h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice({slot}),0).g"),
                "height blending retains mip zero at slot "+slot);
            Require(domain.Contains($"geometryHeight=LTSampleLayerMask({slot},layerUV,_LTDisplacement{slot}.z).g"),
                "geometry uses matching slot and smoothing mip "+slot);
        }
        // Declarations may use [12]; executable accesses may not use index 12.
        Require(!Regex.IsMatch(core,@"(?:colors|normals|masks|weights)\[12\](?:=(?!\{)|\.|>|\*=)"),"twelve-slot shader must not access index twelve");
    }

    static void TerrainTextureBudgetContracts()
    {
        const string root="Assets/TerrainSystem/LocalTerrain/";
        var removed=new[]{"_TangentMap","_TangentMapOS","_AnisotropyMap","_IridescenceThicknessMap",
            "_IridescenceMaskMap","_SpecularColorMap","_TransmittanceColorMap","_CoatMaskMap"};
        var keywords=new[]{"_TANGENTMAP","_ANISOTROPYMAP","_IRIDESCENCE_THICKNESSMAP",
            "_SPECULARCOLORMAP","_TRANSMITTANCECOLORMAP","_MATERIAL_FEATURE_IRIDESCENCE","_MATERIAL_FEATURE_CLEAR_COAT"};
        var properties=File.ReadAllText(root+"Shaders/LTEightLayerProperties.hlsl");
        foreach(var texture in removed)
            Require(!properties.Contains("TEXTURE2D("+texture+")"),"unused HDRP binding removed: "+texture);
        foreach(var keyword in keywords)
            Require(properties.Contains("#undef "+keyword),"stale HDRP keyword cannot reference removed texture: "+keyword);
        foreach(var file in new[]{"Resources/LTEightLayers.shader","Resources/LTEightLayersTessellation.shader"})
        {
            var shader=File.ReadAllText(root+file);
            foreach(var texture in removed)
                Require(!Regex.IsMatch(shader,@"(?m)^\s*"+texture+@"\("),"unused HDRP texture property removed in "+file+": "+texture);
            foreach(var keyword in keywords)
                Require(!Regex.IsMatch(shader,@"(?m)^\s*#pragma shader_feature[^\r\n]*\b"+keyword+@"\b"),
                    "unsupported HDRP variant removed in "+file+": "+keyword);
        }
        var check=File.ReadAllText(root+"Editor/LTRoadCheck.cs");
        Require(check.Contains("Application.logMessageReceivedThreaded+=Capture;")&&
            check.Contains("Application.logMessageReceivedThreaded-=Capture;"),"native diagnostics captured and unsubscribed");
        Require(check.Contains("\"texture parameters\"")&&check.Contains("\"keyword space\""),"plain native limit/assert messages are failures");
        int clean=check.IndexOf("RequireClean(\"GPU bake/readback\")",StringComparison.Ordinal);
        int pass=check.IndexOf("Debug.Log(\"PASS Road Rendering",StringComparison.Ordinal);
        Require(clean>=0&&pass>clean,
            "diagnostics checked before reporting PASS");
        Console.WriteLine("PASS terrain texture budget source contracts: eight unused Lit maps/variants remain removed; native failure capture before PASS. Three layer arrays/twelve mappings checked separately; actual device binding budget requires Unity validation.");
    }
}
