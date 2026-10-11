// Run only in the independent project prepared by ValidateTopTwoLayers.ps1.
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class TopTwoLayersGpuCheck
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static float Error(Color a,Color b)=>Enumerable.Range(0,4).Max(i=>Mathf.Abs(a[i]-b[i]));
    public static void Run()
    {
        int exit=0;var report=new StringBuilder();
        try
        {
            Require(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null,"Graphics device required");
            report.AppendLine(SystemInfo.graphicsDeviceName+" / "+SystemInfo.graphicsDeviceType);
            ShaderUtil.allowAsyncCompilation=false;
            var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/TopTwoProbe.shader");
            Require(shader&&shader.isSupported,"Probe shader unavailable");
            var mat=new Material(shader);mat.EnableKeyword("_LT_FAST_LAYERS");
            int reference=mat.FindPass("Reference"),actual=mat.FindPass("Optimized"),planar=mat.FindPass("Planar");
            Require(reference>=0&&actual>=0&&planar>=0,"Missing probe passes");
            ShaderUtil.CompilePass(mat,reference,true);ShaderUtil.CompilePass(mat,actual,true);
            Require(!ShaderUtil.ShaderHasError(shader),string.Join("; ",ShaderUtil.GetShaderMessages(shader).Select(m=>m.message)));
            var arrays=new Texture2DArray[3];
            var colors=new Color[12];var normals=new Vector3[12];var ao=new float[12];var smooth=new float[12];
            for(int kind=0;kind<3;kind++)arrays[kind]=new Texture2DArray(1,1,12,TextureFormat.RGBAFloat,false,true);
            for(int id=0;id<12;id++)
            {
                colors[id]=new Color((id%3)*.4f,(id%5)*.2f,(id%7)*.12f,1);
                normals[id]=new Vector3((id%3-1)*.2f,(id%4-1)*.15f,1).normalized;
                ao[id]=.2f+.06f*id;smooth[id]=.08f*id;
                arrays[0].SetPixels(new[]{colors[id]},id);
                var n=normals[id];arrays[1].SetPixels(new[]{new Color(n.x*.5f+.5f,n.y*.5f+.5f,n.z*.5f+.5f,1)},id);
            }
            arrays[0].Apply();arrays[1].Apply();
            mat.SetTexture("_LTColorArray",arrays[0]);mat.SetTexture("_LTNormalArray",arrays[1]);mat.SetTexture("_LTMaskArray",arrays[2]);
            var weights=new Texture2D[3];
            for(int group=0;group<3;group++)
            {weights[group]=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);mat.SetTexture("_LTWeights"+group,weights[group]);}
            mat.SetVector("_LTRect",new Vector4(0,0,1,1));mat.SetMatrix("_LTSurfaceWorldToLocal",Matrix4x4.identity);
            mat.SetVector("_CheckPoint",new Vector4(.5f,.3f,.5f,0));
            mat.SetVector("_CheckDx",new Vector4(.01f,.005f,.004f,0));mat.SetVector("_CheckDy",new Vector4(.003f,.01f,.005f,0));
            mat.SetFloat("_LTBaseOnly",0);mat.SetFloat("_LTRoadProjectionEnabled",0);
            var rt=new RenderTexture(1,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
            Require(rt.Create(),"Render target failed");var read=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);
            Color Pixel(int pass,int output)
            {
                mat.SetVector("_LTProbeUV",new Vector4(0,0,output,0));RenderTexture.active=rt;
                GL.sRGBWrite=false;GL.Clear(false,true,new Color(-9,-9,-9,-9));
                Require(mat.SetPass(pass),"SetPass failed");Graphics.DrawProceduralNow(MeshTopology.Triangles,3);
                read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();var p=read.GetPixel(0,0);
                Require(Enumerable.Range(0,4).All(i=>!float.IsNaN(p[i])&&!float.IsInfinity(p[i])),"Nonfinite output");
                Require(p.a>=0,"Clear sentinel survived");return p;
            }
            int samples=0,colorSamples=0;
            for(int test=0;test<12;test++)for(int reverse=0;reverse<2;reverse++)for(int tri=0;tri<2;tri++)
            {
                var raw=new float[12];var heights=Enumerable.Repeat(.5f,12).ToArray();
                float blend=0;
                if(test==0)raw[11]=1;
                if(test==1){raw[3]=.7f;raw[9]=.3f;}
                if(test==2||test==3){Array.Copy(new[]{.36f,.28f,.2f,.1f,.06f},raw,5);blend=test==3?.5f:0;heights[4]=1;heights[0]=0;}
                if(test==4){Array.Copy(new[]{.55f,.2f,.15f,.08f,.02f},raw,5);Array.Copy(new[]{0f,0f,.9f,.5f,1f},heights,5);blend=1;}
                if(test==5){for(int i=0;i<5;i++)raw[i]=.2f;blend=1;}
                // test 6: empty coverage must stay finite and black.
                if(test==7)raw[0]=1; // Single winner at slot zero must not be counted twice.
                if(test==8){Array.Copy(new[]{.35f,.30f,.20f,.15f},raw,4);heights[0]=.15f;heights[1]=.3f;blend=1;}
                if(test==9){for(int i=0;i<12;i++)raw[i]=(12-i)/78f;blend=.7f;}
                if(test==10){raw[0]=.6f;raw[7]=.3f;raw[11]=.000011f;raw[6]=.000009f;blend=.5f;}
                if(test==11){for(int i=0;i<12;i++)raw[i]=.01f;blend=1;}
                for(int id=0;id<12;id++)arrays[2].SetPixels(new[]{new Color(ao[id],heights[id],smooth[id],1)},id);
                arrays[2].Apply();
                for(int group=0;group<3;group++)
                {
                    var w=Color.clear;var slices=Vector4.zero;
                    for(int c=0;c<4;c++)
                    {
                        int slot=group*4+c,id=reverse==0?slot:11-slot;w[c]=raw[id];slices[c]=id;
                        mat.SetVector("_LTTiling"+slot,new Vector4(1,1,0,0));mat.SetVector("_LTTint"+slot,Vector4.one);
                        mat.SetVector("_LTSettings"+slot,new Vector4(1,id*.04f,.5f,1));mat.SetVector("_LTFlags"+slot,new Vector4(1,1,1,0));
                    }
                    weights[group].SetPixel(0,0,w);weights[group].Apply();mat.SetVector("_LTLayerSlices"+group,slices);
                }
                mat.SetFloat("_LTHeightBlend",blend);mat.SetFloat("_LTTriplanar",tri);
                var baseNormal=tri==0?Vector3.up:new Vector3(.4f,.8f,.3f).normalized;
                mat.SetVector("_LTProbeNormal",baseNormal);
                mat.SetVector("_LTFastLayerParams",Vector4.zero);
                mat.DisableKeyword("_LT_FAST_PREHEIGHT");
                for(int output=0;output<3;output++)
                {Require(Error(Pixel(reference,output),Pixel(actual,output))<.00005f,"Disabled mode changed legacy result");samples++;}
                // A stale pre-height bit must have no effect while Top Two is off.
                mat.SetVector("_LTFastLayerParams",new Vector4(0,0,0,1));
                for(int output=0;output<3;output++)
                {Require(Error(Pixel(reference,output),Pixel(actual,output))<.00005f,"Pre-height bit affected disabled Top Two");samples++;}
                for(int before=0;before<2;before++)
                {
                // Restore full authored coverage before each independently measured arm.
                for(int group=0;group<3;group++)
                {
                    var w=Color.clear;
                    for(int c=0;c<4;c++)w[c]=raw[reverse==0?group*4+c:11-(group*4+c)];
                    weights[group].SetPixel(0,0,w);weights[group].Apply();
                }
                mat.SetFloat("_LTHeightBlend",blend);
                // Independent oracle: prune coverage before height only in the new
                // arm, preserve its original magnitude, then evaluate height blending.
                var coverage=(float[])raw.Clone();
                var prePair=Enumerable.Range(0,12).Where(i=>raw[i]>0).OrderByDescending(i=>raw[i]).ThenBy(i=>i).Take(2).ToArray();
                if(before!=0)for(int i=0;i<12;i++)if(!prePair.Contains(i))coverage[i]=0;
                float highest=Enumerable.Range(0,12).Where(i=>coverage[i]>.00001f).Select(i=>coverage[i]+heights[i]).DefaultIfEmpty(-1).Max();
                var post=Enumerable.Range(0,12).Select(i=>coverage[i]*Mathf.Lerp(1,Mathf.Clamp01((heights[i]+coverage[i]-highest+.2f)/.2f),blend)).ToArray();
                var pair=Enumerable.Range(0,12).Where(i=>post[i]>0).OrderByDescending(i=>post[i]).ThenBy(i=>i).Take(2).ToArray();
                if(test==4)Require(before==0?pair.Contains(4)&&pair.Contains(2):pair.SequenceEqual(new[]{0}),"Pre/post height winner fixture invalid");
                float total=pair.Sum(i=>post[i]);Color expectedColor=Color.clear;Vector3 n=Vector3.zero;float a=0,s=0,m=0;
                foreach(int id in pair)
                {float w=post[id]/total;expectedColor+=colors[id]*w;n+=normals[id]*w;a+=ao[id]*w;s+=smooth[id]*w;m+=id*.04f*w;}
                n=(n+new Vector3(0,0,.00001f)).normalized;
                Vector3 gradient=new Vector3(-n.x/Mathf.Max(n.z,.0001f),0,-n.y/Mathf.Max(n.z,.0001f));
                // The planar case checks normals analytically; triplanar checks all
                // outputs against a reference restricted to the known winning pair.
                mat.SetVector("_LTFastLayerParams",new Vector4(.15f,0,1,before));
                if(before!=0)mat.EnableKeyword("_LT_FAST_PREHEIGHT");else mat.DisableKeyword("_LT_FAST_PREHEIGHT");
                var captured=Enumerable.Range(0,3).Select(output=>Pixel(actual,output)).ToArray();
                if(tri==0)for(int output=0;output<3;output++)
                {Require(Error(captured[output],Pixel(planar,output))<.00005f,"Direct planar path differs from projected XZ path");samples++;}
                Require(Error(captured[0],new Color(expectedColor.r,expectedColor.g,expectedColor.b,a))<.00005f,"Top-two color/AO mismatch: "+test);
                Require(Error(captured[2],new Color(m,a,s,1))<.00005f,"Top-two material mismatch: "+test);
                if(tri==0)Require(Error(captured[1],new Color(gradient.x,gradient.y,gradient.z,s))<.00005f,"Top-two normal mismatch: "+test);
                if(before!=0)
                {
                    mat.EnableKeyword("_LT_FAST_COLOR_REMAINDER");
                    mat.SetVector("_LTFastLayerParams",new Vector4(.15f,0,1,2));
                    var colored=Enumerable.Range(0,3).Select(o=>Pixel(actual,o)).ToArray();
                    float pairMass=prePair.Sum(i=>raw[i]),remainderMass=0;
                    var expected=expectedColor*pairMass;
                    foreach(int id in Enumerable.Range(0,12).Where(i=>!prePair.Contains(i)&&raw[i]>.00001f))
                    {expected+=colors[id]*raw[id];remainderMass+=raw[id];}
                    expected=remainderMass>0?expected/(pairMass+remainderMass):expectedColor;
                    Require(Error(colored[0],new Color(expected.r,expected.g,expected.b,a))<.00005f,"Color remainder oracle mismatch: "+test);colorSamples++;
                    for(int output=1;output<3;output++)
                    {Require(Error(colored[output],captured[output])<.00005f,"Remainder changed normals/PBR");colorSamples++;}
                    if(tri==0)for(int output=0;output<3;output++)
                    {Require(Error(colored[output],Pixel(planar,output))<.00005f,"Color remainder planar routing");colorSamples++;}
                    // Base-only short circuit must ignore every remainder entry.
                    mat.SetFloat("_LTBaseOnly",1);
                    var baseColor=Pixel(actual,0);mat.DisableKeyword("_LT_FAST_COLOR_REMAINDER");
                    Require(Error(baseColor,Pixel(actual,0))<.00005f,"Base-only affected by color remainder");colorSamples++;
                    mat.SetFloat("_LTBaseOnly",0);mat.SetVector("_LTFastLayerParams",new Vector4(.15f,0,1,1));
                    for(int output=0;output<3;output++)
                    {Require(Error(captured[output],Pixel(actual,output))<.00005f,"Remainder OFF did not restore strict pair");colorSamples++;}
                }
                // Feed the expected final pair into the frozen full sampler, with
                // height blend disabled so it cannot re-evaluate the reduced set.
                for(int group=0;group<3;group++)
                {
                    var w=Color.clear;
                    for(int c=0;c<4;c++){int id=reverse==0?group*4+c:11-(group*4+c);if(pair.Contains(id))w[c]=post[id]/total;}
                    weights[group].SetPixel(0,0,w);weights[group].Apply();
                }
                mat.SetFloat("_LTHeightBlend",0);
                for(int output=0;output<3;output++)
                {Require(Error(captured[output],Pixel(reference,output))<.00005f,"Selected-pair reference mismatch: "+test);samples++;}
                report.AppendLine($"PASS case {test}, reverse={reverse}, triplanar={tri}, beforeHeight={before}, pair=[{string.Join(",",pair)}]");
                }
            }
            report.AppendLine($"PASS {samples} GPU comparisons: legacy off, pre-height and post-height selection, discarded height winner, authored pair magnitudes, ties, slot permutations, empty/single/two/five layers, normals and PBR properties.");
            report.AppendLine($"PASS {colorSamples} color-only remainder GPU comparisons: independent weighted-color oracle, up to 12 layers, ties/permutations, tiny weights, unchanged normals/PBR, planar/triplanar, base-only and OFF restoration.");
            RoadCases(mat,Pixel,weights,report);
            report.AppendLine("Isolated production-sampler probe. Does not validate full HDRP draw or live-scene performance.");
        }
        catch(Exception e){exit=1;report.AppendLine("FAIL "+e);}
        File.WriteAllText("top-two-result.txt",report.ToString());Debug.Log(report.ToString());EditorApplication.Exit(exit);
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Chunk {public Vector4 rect,options,fastLayerParams;}
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Layer {public Vector4 tiling,tint,settings,flags,indices,asphaltStyle;}
    static void RoadCases(Material mat,Func<int,int,Color> pixel,Texture2D[] weights,StringBuilder report)
    {
        mat.EnableKeyword("_LT_FAST_ROADS");
        int reference=mat.FindPass("Reference"),actual=mat.FindPass("Optimized"),adapter=mat.FindPass("RoadAdapter");
        Require(adapter>=0,"Road adapter pass missing");
        ShaderUtil.CompilePass(mat,actual,true);ShaderUtil.CompilePass(mat,adapter,true);
        Require(!ShaderUtil.ShaderHasError(mat.shader),string.Join("; ",ShaderUtil.GetShaderMessages(mat.shader).Select(m=>m.message)));
        var arrays=Enumerable.Range(0,3).Select(_=>new Texture2DArray(16,16,12,TextureFormat.RGBAFloat,true,true)
            {filterMode=FilterMode.Trilinear,wrapMode=TextureWrapMode.Repeat}).ToArray();
        var heights=Enumerable.Range(0,12).Select(i=>i==4?1f:i*.07f).ToArray();
        for(int id=0;id<12;id++)for(int kind=0;kind<3;kind++)
        {
            var data=new Color[256];
            for(int y=0;y<16;y++)for(int x=0;x<16;x++)
            {
                float u=x/15f,v=y/15f;var n=new Vector3((u-.5f)*.7f,(v-.5f)*.6f,1).normalized;
                data[y*16+x]=kind==0?new Color(u*.7f,v*.8f,id*.07f,1):
                    kind==1?new Color(n.x*.5f+.5f,n.y*.5f+.5f,n.z*.5f+.5f,1):new Color(.3f+.5f*v,heights[id],.1f+.7f*u,1);
            }
            arrays[kind].SetPixels(data,id);
        }
        foreach(var a in arrays)a.Apply(true);
        var road=new Texture2DArray(257,257,2,TextureFormat.RGBAFloat,false,true);
        var coords=new Color[257*257];var jac=new Color[coords.Length];
        for(int y=0;y<257;y++)for(int x=0;x<257;x++)
        {
            // Owner changes at x=129 force the Jacobian fallback near that boundary.
            float owner=x<129?1:2,u=x/256f,v=y/256f;
            coords[y*257+x]=new Color(.17f+u*.6f-v*.8f,.31f+u*.8f+v*.6f,.6f*owner,.8f*owner);
            jac[y*257+x]=new Color(.6f,.8f,-.8f,.6f);
        }
        road.SetPixels(coords,0);road.SetPixels(jac,1);road.Apply();mat.SetTexture("_LTRoadProjectionMap",road);
        var damage=new Texture2DArray(1,1,1,TextureFormat.RGBAFloat,false,true);
        damage.SetPixels(new[]{new Color(.85f,.35f,0,0)},0);damage.Apply();
        var cracks=new Texture2DArray(1,1,1,TextureFormat.RGBAFloat,false,true);
        cracks.SetPixels(new[]{new Color(.73f,.8f,.2f,0)},0);cracks.Apply();
        mat.SetTexture("_LTAsphaltDamage",damage);mat.SetTexture("_LTAsphaltCracks",cracks);
        mat.SetTexture("_LTColorArray",arrays[0]);mat.SetTexture("_LTNormalArray",arrays[1]);mat.SetTexture("_LTMaskArray",arrays[2]);
        mat.SetTexture("_LTRoadTerrainColor",arrays[0]);mat.SetTexture("_LTRoadTerrainNormal",arrays[1]);mat.SetTexture("_LTRoadTerrainMask",arrays[2]);
        mat.SetTexture("_LTRoadTerrainProjection",road);mat.SetTexture("_LTRoadTerrainDamage",damage);mat.SetTexture("_LTRoadTerrainCracks",cracks);
        var coverageArray=new Texture2DArray(1,1,3,TextureFormat.RGBAFloat,false,true);
        mat.SetTexture("_LTRoadTerrainWeights",coverageArray);
        using var chunks=new ComputeBuffer(1,48);using var layers=new ComputeBuffer(12,96);
        mat.SetBuffer("_LTRoadTerrainChunks",chunks);mat.SetBuffer("_LTRoadTerrainLayers",layers);
        int samples=0,colorSamples=0;
        for(int roadMode=0;roadMode<3;roadMode++)for(int profile=0;profile<3;profile++)
        for(int tri=0;tri<2;tri++)for(int blendMode=0;blendMode<3;blendMode++)for(int select=0;select<3;select++)
        {
            var raw=new float[12];Array.Copy(new[]{.36f,.28f,.2f,.1f,.06f},raw,5);
            if(profile==1){Array.Clear(raw,0,12);raw[7]=.7f;raw[0]=.299991f;raw[11]=.000009f;}
            if(profile==2){for(int i=0;i<12;i++)raw[i]=i<5?.2f:0;}
            bool before=select==2;float blend=blendMode*.5f;
            var data=new Layer[12];
            for(int group=0;group<3;group++)
            {
                var slices=Vector4.zero;var slots=Vector4.zero;
                for(int c=0;c<4;c++)
                {
                    int slot=group*4+c,id=profile==2?11-slot:slot;slices[c]=id;
                    // Mixed road-projected and ordinary layers in every group.
                    slots[c]=slot%2==0?(roadMode==2?-1:1):0;
                    data[slot]=new Layer{tiling=new Vector4(1+id*.13f,1+id*.09f,.11f,.19f),tint=Vector4.one,
                        settings=new Vector4(.8f,id*.04f,.3f,1),flags=new Vector4(1,1.7f,1,0),
                        indices=new Vector4(id,slots[c],0,0),asphaltStyle=new Vector4(slot%3==0?1:0,0,.65f,0)};
                    mat.SetVector("_LTTiling"+slot,data[slot].tiling);mat.SetVector("_LTTint"+slot,data[slot].tint);
                    mat.SetVector("_LTSettings"+slot,data[slot].settings);mat.SetVector("_LTFlags"+slot,data[slot].flags);
                    mat.SetVector("_LTAsphaltStyle"+slot,data[slot].asphaltStyle);
                }
                mat.SetVector("_LTLayerSlices"+group,slices);mat.SetVector("_LTRoadProjectionSlots"+group,slots);
            }
            void Coverage(float[] values)
            {
                for(int group=0;group<3;group++)
                {
                    var w=Color.clear;for(int c=0;c<4;c++)w[c]=values[(int)data[group*4+c].indices.x];
                    weights[group].SetPixel(0,0,w);weights[group].Apply();coverageArray.SetPixels(new[]{w},group);
                }
                coverageArray.Apply();
            }
            Coverage(raw);mat.SetFloat("_LTHeightBlend",blend);mat.SetFloat("_LTTriplanar",tri);
            mat.SetFloat("_LTRoadProjectionEnabled",roadMode==0?0:1);
            mat.SetVector("_LTProbeNormal",tri==0?Vector3.up:new Vector3(.45f,.6f,-.55f).normalized);
            mat.SetVector("_CheckPoint",new Vector4(128.7f/256,.37f,.42f,0));
            mat.SetVector("_CheckDx",new Vector4(.1f,.07f,.035f,0));mat.SetVector("_CheckDy",new Vector4(.045f,.12f,.08f,0));
            var parameters=new Vector4(0,0,select==0?0:1,before?1:0);mat.SetVector("_LTFastLayerParams",parameters);
            if(before)mat.EnableKeyword("_LT_FAST_PREHEIGHT");else mat.DisableKeyword("_LT_FAST_PREHEIGHT");
            var chunk=new Chunk{rect=new Vector4(0,0,1,1),options=new Vector4(blend,tri,0,roadMode==0?0:1),fastLayerParams=parameters};
            chunks.SetData(new[]{chunk});layers.SetData(data);
            var captured=Enumerable.Range(0,3).Select(o=>pixel(actual,o)).ToArray();
            for(int output=0;output<3;output++)
            {Require(Error(captured[output],pixel(adapter,output))<.0001f,$"Adapter parity road={roadMode}, profile={profile}, tri={tri}, blend={blend}, select={select}, out={output}");samples++;}
            if(before)
            {
                var prePair=Enumerable.Range(0,12).Where(i=>raw[i]>0).OrderByDescending(i=>raw[i]).ThenBy(i=>i).Take(2).ToArray();
                mat.EnableKeyword("_LT_FAST_COLOR_REMAINDER");
                var remainderParams=parameters;remainderParams.w=2;mat.SetVector("_LTFastLayerParams",remainderParams);
                chunk.fastLayerParams=remainderParams;chunks.SetData(new[]{chunk});
                var colored=Enumerable.Range(0,3).Select(o=>pixel(actual,o)).ToArray();
                for(int output=0;output<3;output++)
                {Require(Error(colored[output],pixel(adapter,output))<.0001f,"Color remainder road adapter parity");colorSamples++;}
                for(int output=1;output<3;output++)
                {Require(Error(colored[output],captured[output])<.0001f,"Road remainder changed normals/PBR");colorSamples++;}
                Require(Mathf.Abs(colored[0].a-captured[0].a)<.0001f,"Road remainder changed AO");colorSamples++;
                float pairMass=prePair.Sum(i=>raw[i]),remainderMass=0;
                var expected=captured[0]*pairMass;
                // Independent per-layer reference color: preserve road UV/tiling,
                // but deliberately disable asphalt effects on the color-only tail.
                mat.SetFloat("_LTHeightBlend",0);
                for(int slot=0;slot<12;slot++)mat.SetVector("_LTAsphaltStyle"+slot,Vector4.zero);
                foreach(int id in Enumerable.Range(0,12).Where(i=>!prePair.Contains(i)&&raw[i]>.00001f))
                {
                    var single=new float[12];single[id]=1;Coverage(single);
                    expected+=pixel(reference,0)*raw[id];remainderMass+=raw[id];
                }
                expected=remainderMass>0?expected/(pairMass+remainderMass):captured[0];expected.a=captured[0].a;
                Require(Error(colored[0],expected)<.0001f,$"Road color-only oracle road={roadMode}, profile={profile}, tri={tri}, blend={blend}");colorSamples++;
                for(int slot=0;slot<12;slot++)mat.SetVector("_LTAsphaltStyle"+slot,data[slot].asphaltStyle);
                Coverage(raw);mat.SetFloat("_LTHeightBlend",blend);
                mat.DisableKeyword("_LT_FAST_COLOR_REMAINDER");mat.SetVector("_LTFastLayerParams",parameters);
                chunk.fastLayerParams=parameters;chunks.SetData(new[]{chunk});
            }
            if(select!=0)
            {
                var reduced=(float[])raw.Clone();
                var pre=Enumerable.Range(0,12).Where(i=>raw[i]>0).OrderByDescending(i=>raw[i]).ThenBy(i=>i).Take(2).ToArray();
                if(before)for(int i=0;i<12;i++)if(!pre.Contains(i))reduced[i]=0;
                float maximum=Enumerable.Range(0,12).Where(i=>reduced[i]>.00001f).Select(i=>reduced[i]+heights[i]).DefaultIfEmpty(-1).Max();
                var post=Enumerable.Range(0,12).Select(i=>reduced[i]*Mathf.Lerp(1,Mathf.Clamp01(((reduced[i]>.00001f?heights[i]:.5f)+reduced[i]-maximum+.2f)/.2f),blend)).ToArray();
                var pair=Enumerable.Range(0,12).Where(i=>post[i]>0).OrderByDescending(i=>post[i]).ThenBy(i=>i).Take(2).ToArray();
                float sum=pair.Sum(i=>post[i]);for(int i=0;i<12;i++)post[i]=pair.Contains(i)?post[i]/sum:0;
                Coverage(post);mat.SetFloat("_LTHeightBlend",0);
            }
            for(int output=0;output<3;output++)
            {Require(Error(captured[output],pixel(reference,output))<.0001f,$"Road reference road={roadMode}, profile={profile}, tri={tri}, blend={blend}, select={select}, out={output}");samples++;}
            if(select==0)
            {
                chunk.fastLayerParams=new Vector4(-1,0,0,0);chunks.SetData(new[]{chunk});
                for(int output=0;output<3;output++)
                {Require(Error(pixel(adapter,output),pixel(reference,output))<.0001f,"Adapter full mode parity");samples++;}
            }
        }
        report.AppendLine($"PASS {samples} road GPU comparisons: spline UV/Jacobian, rotated normals, asphalt cracks/repairs, AO strength, tiny weights, planar/triplanar, top-two before/after, generated road adapter and full-mode fallback.");
        report.AppendLine($"PASS {colorSamples} road color-only GPU comparisons: adapter parity, independent single-layer color oracle, spline/tiling, unchanged pair normals/PBR.");
    }
}
