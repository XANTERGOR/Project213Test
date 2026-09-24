using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

partial class Checks
{
    static void LayerTextureArrayContracts()
    {
        SavedLayerArrayContracts();
        // Source contracts only: the array owner is not instantiated by this CPU harness.
        // GPU format support, native lifetime and rendering require the Unity checks.
        const string root="Assets/TerrainSystem/LocalTerrain/";
        var failures=new List<string>();
        void Check(bool value,string message){if(!value)failures.Add(message);}
        string Read(string path)
        {
            var source=File.ReadAllText(root+path);
            source=Regex.Replace(source,@"(?m)//[^\r\n]*|/\*[\s\S]*?\*/","");
            return Regex.Replace(source,@"\s+","");
        }
        string Between(string source,string start,string end,string label)
        {
            int first=source.IndexOf(start,StringComparison.Ordinal);
            int last=first<0?-1:source.IndexOf(end,first+start.Length,StringComparison.Ordinal);
            Check(first>=0&&last>first,"identifiable source section: "+label);
            return first>=0&&last>first?source.Substring(first,last-first):"";
        }
        var arrays=Read("LTLayerTextureArrays.cs");
        var runtime=Read("LTPaintRuntime.cs");
        var world=Read("LTWorld.cs");
        var core=Read("Shaders/LTLayerBlendCore.hlsl");
        var domain=Read("Shaders/LTLayerTessellation.hlsl");
        var properties=Read("Shaders/LTEightLayerProperties.hlsl");
        foreach(var kind in new[]{"Color","Normal","Mask"})
        {
            Check(Regex.Matches(core,$@"TEXTURE2D_ARRAY\(_LT{kind}Array\);").Count==1,"one shared declaration for "+kind);
            Check(Regex.Matches(core,$@"SAMPLER\(sampler_LT{kind}Array\);").Count==1,"dedicated texture-associated sampler for "+kind);
            Check(arrays.Contains($"material.SetTexture(\"_LT{kind}Array\",{kind.ToLowerInvariant()})"),"owner binds shared "+kind+" array");
        }
        foreach(var source in new[]{core,domain})
            foreach(Match sample in Regex.Matches(source,@"SAMPLE_TEXTURE2D_ARRAY_(?:LOD|GRAD)\(_LT(Color|Normal|Mask)Array,([^,]+),"))
                Check(sample.Groups[2].Value=="sampler_LT"+sample.Groups[1].Value+"Array","sample must not borrow sampler from a texture stripped in this stage");
        Check(!domain.Contains("sampler_LTColorArray")&&!domain.Contains("sampler_LTNormalArray"),"geometry samples masks without color/normal sampler dependencies");
        foreach(var shader in new[]{"Resources/LTEightLayers.shader","Resources/LTEightLayersTessellation.shader","Resources/LTGlobalLayerBake.shader"})
        {
            var source=Read(shader);
            Check(Regex.Matches(source,@"_LT(?:Color|Normal|Mask)Array\(" ).Count==3,"exactly three layer array properties: "+shader);
            foreach(var kind in new[]{"Color","Normal","Mask"})
                Check(Regex.IsMatch(source,$"_LT{kind}Array\\(\"[^\"]*\",2DArray\\)"),"2DArray property type: "+kind+" / "+shader);
            for(int group=0;group<3;group++)
                Check(Regex.IsMatch(source,$"_LTLayerSlices{group}\\(\"[^\"]*\",Vector\\)"),"mapping Vector property "+group+" / "+shader);
            Check(!Regex.IsMatch(source,@"_LT(?:Color|Normal|Mask)\d+\("),"no legacy layer texture properties: "+shader);
        }
        foreach(var source in new[]{core,domain,properties,runtime})
            Check(!Regex.IsMatch(source,@"\b_LT(?:Color|Normal|Mask)\d+\b"),"production binding/sampling contains no legacy numbered layer textures");
        foreach(var file in new[]{"Shaders/LTEightLayerProperties.hlsl","Resources/LTGlobalLayerBake.shader"})
        {
            var buffer=Between(Read(file),"CBUFFER_START(UnityPerMaterial)","CBUFFER_END",file);
            for(int group=0;group<3;group++)
                Check(Regex.Matches(buffer,$@"(?:float4|,)_LTLayerSlices{group}(?=[,;])").Count==1,"exactly one mapping uniform "+group+" in "+file);
        }
        Check(arrays.Contains("for(intgroup=0;group<3;group++)")&&arrays.Contains("for(intchannel=0;channel<4;channel++)")&&
            arrays.Contains("intslot=group*4+channel;")&&arrays.Contains("slices.TryGetValue(layers[slot],outintslice))indices[channel]=slice;")&&
            arrays.Contains("material.SetVector(\"_LTLayerSlices\"+group,indices)"),"all twelve local slots bind world indices through three vectors");

        Check(runtime.Contains("readonlyLTLayerTextureArrayslayerArrays=newLTLayerTextureArrays();")&&
            Regex.Matches(runtime,@"newLTLayerTextureArrays\(").Count==1&&!runtime.Contains("staticreadonlyLTLayerTextureArrays"),
            "one nonstatic cache owned by each painting runtime, not each chunk");
        Check(world.Contains("LTPaintRuntimepaintRuntime;")&&!world.Contains("staticLTPaintRuntimepaintRuntime;")&&
            world.Contains("paintRuntime=newLTPaintRuntime()"),"painting runtime is owned by its LTWorld");
        Check(runtime.Contains("layerArrays.Bind(material,layers);"),"ordinary and rock Bind path uses the shared owner");
        Check(runtime.Contains("varall=world.CollectPaintStamps();"),"palette derives from world-owned stamps");
        var palette=Between(runtime,"varpalette=","intarrayRevision=","full palette");
        Check(palette.Contains("newList<LTSurfaceLayer>{world.baseLayer}")&&
            palette.Contains("foreach(varstampinall)if(stamp.EffectiveLayer&&!palette.Contains(stamp.EffectiveLayer))palette.Add(stamp.EffectiveLayer);"),
            "palette includes base plus every distinct layer, including disabled stamps");
        Check(!palette.Contains("LayerCapacity")&&!palette.Contains(".Take(")&&!arrays.Contains("LayerCapacity")&&!arrays.Contains(".Take("),
            "world palette is not truncated to the twelve local slots");
        var invalidation=Between(runtime,"if(arrayRevision!=layerArrays.Revision)","varroadErrors=","array-generation invalidation");
        Check(invalidation.Contains("foreach(varstateinchunks.Values)")&&invalidation.Contains("state.ready=false;")&&
            invalidation.Contains("state.globalBindingReady=false;")&&invalidation.Contains("state.farMaterialDirty=true;")&&
            invalidation.Contains("foreach(varrockinrocks.Values)rock.paint.ready=false;"),"new palette generation invalidates chunk, far/flat and rock bindings");

        var fingerprint=Between(arrays,"varkey=","stringnext=","texture fingerprint");
        foreach(var token in new[]{"texture.GetInstanceID()","texture.imageContentsHash","texture.updateCount","layer.GetInstanceID()",
            "TextureKey(layer.baseColorMap)","TextureKey(layer.normalMap)","TextureKey(layer.maskMap)"})
            Check(fingerprint.Contains(token),"texture fingerprint includes "+token);
        var allowed=new[]{"GetInstanceID","baseColorMap","normalMap","maskMap"};
        Check(Regex.Matches(fingerprint,@"\blayer\.(\w+)").Cast<Match>().All(m=>allowed.Contains(m.Groups[1].Value))&&
            !fingerprint.Contains("JsonUtility")&&!fingerprint.Contains("SurfaceHash"),"fingerprint excludes tint, strength, tiling and other scalar surface settings");
        var cacheHit=Between(arrays,"if(signature==next&&color&&normal&&mask)","if(failedSignature==next)","cache hit");
        Check(cacheHit.Contains("SetFiltering(trilinear,anisotropy);")&&cacheHit.Contains("returntrue;")&&!cacheHit.Contains("Build("),
            "unchanged textures reuse arrays; filtering updates do not repack");

        Check(arrays.Contains("publicboolEnsure(List<LTSurfaceLayer>palette,intc,intn,intm,intbudgetMiB,intrevision,booltrilinear,intanisotropy)"),
            "native tests can use production packing without constructing an LTWorld");
        var validation=Between(arrays,"publicboolEnsure(List<LTSurfaceLayer>palette,","varkey=","palette admission before hashing");
        Check(validation.Contains("if(palette==null||palette.Count==0)")&&
            Regex.IsMatch(validation,@"foreach\(varlayerinpalette\)if\(!layer\|\|!\w+\.Add\(layer\)\)")&&
            validation.Contains("newHashSet<LTSurfaceLayer>()")&&validation.Contains("ReleaseMipRequests();")&&
            validation.Contains("returnfalse;")&&!validation.Contains("Build("),
            "null, empty, null-entry and duplicate palettes are rejected before hashing/allocation and release pending requests");
        var sources=Between(arrays,"boolSourcesReady(","publicboolEnsure(","streaming readiness");
        Check(sources.Contains("if(streamingSignature!=next){ReleaseMipRequests();streamingSignature=next;}")&&
            sources.Contains("new[]{layer.baseColorMap,layer.normalMap,layer.maskMap}"),"changed source set releases old requests and checks every texture kind");
        Check(sources.Contains("if(!texture||!texture.streamingMipmaps)continue;")&&
            sources.Contains("texture.requestedMipmapLevel=0;")&&sources.Contains("ready&=texture.IsRequestedMipmapLevelLoaded();"),
            "streaming readiness waits for all requested mip-zero sources");
        // Sharing source textures across worlds requires shared leases. Do not pin
        // this contract to the old per-owner Dictionary<Texture2D,int> representation
        // or to local names such as pair.Key/pair.Value used while restoring requests.
        Check(Regex.IsMatch(arrays,@"static(?:readonly)?Dictionary<Texture2D,\w+>")&&
            Regex.IsMatch(arrays,@"(?<!static)readonlyHashSet<Texture2D>"),
            "source mip requests use shared lease storage and a per-owner texture set");
        var releaseRequests=Between(arrays,"voidReleaseMipRequests()","boolSourcesReady(","streaming release");
        Check(releaseRequests.Contains(".requestedMipmapLevel==0")&&releaseRequests.Contains(".ClearRequestedMipmapLevel();")&&
            Regex.IsMatch(releaseRequests,@"\.requestedMipmapLevel=(?!0;)\w+(?:\.\w+)*;")&&
            releaseRequests.Contains(".Clear();")&&releaseRequests.Contains("streamingSignature=null;"),
            "streaming cleanup can restore previous requests and clears owner tracking");
        // Bind acquisition to the owner's Add guard and restoration to the last-user
        // guard, without depending on dictionary/local variable names.
        var acquisition=Between(sources,"if(mipRequests.Add(texture))","texture.requestedMipmapLevel=0;","one lease per owner/source");
        Check(Regex.IsMatch(acquisition,@"if\(!\w+\.TryGetValue\(texture,outvar\w+\)\)")&&
            Regex.IsMatch(acquisition,@"\w+=texture\.requestedMipmapLevel")&&
            Regex.IsMatch(acquisition,@"\w+\.users\+\+;"),"only first owner captures original mip request; each owner increments once");
        var lastUser=Regex.Match(releaseRequests,@"if\(!\w+\.TryGetValue\(texture,outvar\w+\)\|\|--\w+\.users>0\)continue;");
        int restore=releaseRequests.IndexOf(".ClearRequestedMipmapLevel();",StringComparison.Ordinal);
        Check(lastUser.Success&&restore>lastUser.Index+lastUser.Length&&releaseRequests.Contains(".Remove(texture);"),
            "nonfinal lease release skips restoration; final release removes the shared entry before restoring");
        var failedHit=Between(arrays,"if(failedSignature==next)","vartimer=","cached failure");
        Check(cacheHit.Contains("ReleaseMipRequests();")&&failedHit.Contains("ReleaseMipRequests();")&&failedHit.Contains("returnfalse;"),
            "both cached-success and cached-failure returns release abandoned streaming requests");
        var wait=Between(arrays,"if(!SourcesReady(palette,next))","Application.logMessageReceivedThreaded+=Capture;","streaming wait");
        Check(wait.Contains("waiting=true;")&&wait.Contains("returnfalse;")&&!wait.Contains("failedSignature="),
            "streaming wait is retryable, not cached as a packing failure");
        Check(arrays.Contains("finally{Application.logMessageReceivedThreaded-=Capture;if(!waiting)ReleaseMipRequests();}"),
            "all completed/failed packing paths unsubscribe diagnostics and restore mip requests");
        int ready=arrays.IndexOf("if(!SourcesReady(palette,next))",StringComparison.Ordinal);
        int firstBuild=arrays.IndexOf("newColor=Build(",StringComparison.Ordinal);
        int nativeCheck=arrays.IndexOf("lock(logLock)if(nativeFailure!=null)thrownewInvalidOperationException(nativeFailure);",StringComparison.Ordinal);
        int publish=arrays.IndexOf("color=newColor;",StringComparison.Ordinal);
        Check(ready>=0&&firstBuild>ready&&nativeCheck>firstBuild&&publish>nativeCheck,
            "source readiness precedes packing; captured native failures reject publication");

        var disposal=Between(arrays,"publicvoidDispose()","publicboolEnsure(","array disposal");
        foreach(var token in new[]{"Destroy(color);","Destroy(normal);","Destroy(mask);","color=normal=mask=null;","slices.Clear();"})
            Check(disposal.Contains(token),"array disposal clears owned resource: "+token);
        var runtimeDisposal=Between(runtime,"publicvoidDispose()","publicstaticRectStampBounds(","runtime disposal");
        Check(runtimeDisposal.Contains("layerArrays.Dispose();")&&runtimeDisposal.Contains("arrayWorld.arrayColorPreview=arrayWorld.arrayNormalPreview=arrayWorld.arrayMaskPreview=null;"),
            "runtime disposes cache and clears world previews");
        Check(disposal.Contains("ReleaseMipRequests();"),"disposing while waiting restores source mip requests");
        Check(runtimeDisposal.IndexOf("foreach(varstateinchunks.Values)Release(state);",StringComparison.Ordinal)>=0&&
            runtimeDisposal.IndexOf("layerArrays.Dispose();",StringComparison.Ordinal)>runtimeDisposal.IndexOf("foreach(varstateinchunks.Values)Release(state);",StringComparison.Ordinal),
            "release consumer materials before shared arrays");
        Check(world.Contains("paintRuntime?.Dispose();paintRuntime=null;"),"world release disposes its painting runtime");
        var chunkRelease=Between(runtime,"staticvoidRelease(ChunkStatestate)","publicvoidRestoreMaterialsForSave()","chunk release");
        Check(!chunkRelease.Contains("layerArrays"),"releasing one chunk must not dispose world-shared arrays");

        Check(!Regex.IsMatch(arrays,@"\b(ReadPixels|GetPixels(?:32)?|GetPixelData|AsyncGPUReadback)\b"),"packing remains GPU-only without CPU readback");
        var build=Between(arrays,"staticTexture2DArrayBuild(","publicvoidBind(","GPU packing");
        int apply=build.IndexOf("array.Apply(false,true);",StringComparison.Ordinal),copy=build.IndexOf("Graphics.CopyTexture(",StringComparison.Ordinal);
        Check(apply>=0&&copy>apply&&Regex.Matches(build,@"\.Apply\(").Count==1,"release array CPU backing before copies; no Apply after GPU copies");
        int generate=build.IndexOf("staging.GenerateMips();",StringComparison.Ordinal);
        Check(generate>build.IndexOf("Graphics.Blit(",StringComparison.Ordinal)&&generate<copy&&
            build.Contains("for(intmip=0;mip<array.mipmapCount;mip++)Graphics.CopyTexture(staging,0,mip,array,slice,mip);"),
            "generate staging mip chain and copy every matching mip of every slice");
        Check(build.Contains("for(intslice=0;slice<palette.Count;slice++)")&&build.Contains("TextureFormat.RGBA32,true,kind!=0")&&
            build.Contains("kind==0?RenderTextureReadWrite.sRGB:RenderTextureReadWrite.Linear"),"full palette, color sRGB and linear normal/mask mipmapped allocation");
        Check(build.Contains("array.graphicsFormat!=staging.graphicsFormat")&&build.Contains("GL.sRGBWrite=srgb;RenderTexture.active=previous;if(staging)staging.Release();Destroy(staging);"),
            "copy formats match and staging/render state restored");
        int guarded=build.IndexOf("try{",StringComparison.Ordinal);
        Check(build.Contains("RenderTexturestaging=null;")&&guarded>=0&&apply>guarded&&
            build.IndexOf("staging=newRenderTexture(",StringComparison.Ordinal)>guarded,
            "Apply/staging construction are guarded so partial allocations are released");
        var pack=Read("Resources/LTLayerArrayPack.shader");
        Check(pack.Contains("#include\"UnityCG.cginc\"")&&pack.Contains("float4(UnpackNormal(value)*.5+.5,1)"),
            "packing converts imported platform normals to canonical RGB at unit strength");

        var admission=Between(arrays,"varrequiredCopy=","varshader=","GPU limits and peak budget");
        foreach(var token in new[]{"SystemInfo.supports2DArrayTextures","CopyTextureSupport.DifferentTypes","CopyTextureSupport.RTToTexture",
            "SystemInfo.maxTextureSize","palette.Count>SystemInfo.maxTextureArraySlices","EstimateBytes(palette.Count,c,n,m)",
            "bytes+Bytes+staging>(long)Math.Max(16,budgetMiB)*1024*1024"})
            Check(admission.Contains(token),"pre-allocation capability/peak-budget guard: "+token);
        Check(arrays.Contains("for(;size>0;size>>=1)sum+=(long)size*size*4;")&&
            arrays.Contains("Math.Max(1,count)*(Mips(colorSize)+Mips(normalSize)+Mips(maskSize))"),"budget includes all RGBA8 mips, arrays and palette slices");
        Check(arrays.Contains("Destroy(newColor);Destroy(newNormal);Destroy(newMask);failedSignature=next;")&&
            build.Contains("catch{Destroy(array);throw;}"),"failed packing releases partial array allocations");
        if(failures.Count>0)throw new Exception("Texture array source contracts failed:\n - "+string.Join("\n - ",failures));
        Console.WriteLine("PASS texture array source contracts: three arrays/twelve mappings, world cache/full palette, texture-only fingerprint, disposal, GPU mip packing and peak budget. No native GPU execution.");
    }
}
