using System;
using System.IO;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void DeformationChecks()
    {
        MudHullChecks();
        DeformationOptimizationChecks();
        var weights=new float[8];weights[0]=1;
        Require(LTDeformationMath.Resolution(10,2,1024)==501,"10m area at 2cm needs 501 endpoint samples");
        Require(LTDeformationMath.Resolution(100,2,1024)==1024,"area depth map must respect maximum resolution");
        Require(LTDeformationMath.Resolution(5,2,1024)==251,"rectangular areas must size axes independently");
        Require(LTDeformationMath.Contact(.02f,0,.01f,.08f)==0,"vertical tolerance must not expand the horizontal footprint");
        Require(LTDeformationMath.Contact(0,.09f,.01f,.08f)==0,"floating object must not press");
        Require(LTDeformationMath.Contact(0,0,.01f,.08f)==1,"interior contact must retain full pressure");
        var heights=new float[8];Array.Fill(heights,.5f);
        var settings=new Vector4[8];
        Require(LTDeformationMath.Controls(weights,heights,settings,0).x==0,"opt-out layer must not deform");
        settings[0]=new Vector4(.2f,10,24,0);
        var controls=LTDeformationMath.Controls(weights,heights,settings,0);
        Require(Math.Abs(controls.x-.2f)<1e-6&&Math.Abs(controls.y-.02f)<1e-6&&controls.z==24,"pure layer settings must be preserved");
        Require(LTDeformationMath.Recover(.2f,controls.y,10)<1e-6,"full depth must recover in configured time");
        settings[0].y=0;
        Require(LTDeformationMath.Controls(weights,heights,settings,0).y==0,"zero recovery must keep tracks");
        Require(LTDeformationMath.Recover(.1f,0,1000)==.1f,"permanent track must persist");
        Require(LTDeformationMath.Press(0,.2f,.3f,0,1)==0,"no contact must not indent");
        Require(LTDeformationMath.Press(.15f,.2f,10,1,1)==.2f,"depth must be capped");
        Require(LTDeformationMath.Recover(.01f,1,2)==0,"recovery cannot overshoot above the surface");
        weights[0]=.4f;weights[1]=.6f;
        Require(LTDeformationMath.Controls(weights,heights,settings,0).x==0,"stone-dominant area must remain undeformed");
        weights[0]=.6f;weights[1]=.4f;
        float partial=LTDeformationMath.Controls(weights,heights,settings,0).x;
        Require(partial>0&&partial<.2f,"mixed edge must taper depth");
        heights[0]=0;heights[1]=1;
        Require(LTDeformationMath.Controls(weights,heights,settings,1).x==0,"height-blended stone must block mud");
        var random=new System.Random(920);
        for(int test=0;test<2000;test++)
        {
            float initial=(float)random.NextDouble()*.2f,rate=(float)random.NextDouble();
            float whole=LTDeformationMath.Press(initial,.2f,rate,.7f,1);
            float split=initial;
            for(int step=0;step<30;step++)split=LTDeformationMath.Press(split,.2f,rate,.7f,1f/30);
            Require(Math.Abs(whole-split)<1e-5,"contact integration must be frame-rate independent");
            whole=LTDeformationMath.Recover(initial,rate,1);split=initial;
            for(int step=0;step<30;step++)split=LTDeformationMath.Recover(split,rate,1f/30);
            Require(Math.Abs(whole-split)<1e-5,"recovery integration must be frame-rate independent");
        }
        const string root="Assets/TerrainSystem/LocalTerrain/";
        var runtime=File.ReadAllText(root+"LTPaintRuntime.cs");
        var sim=File.ReadAllText(root+"LTPaintDeformation.cs");
        var deformer=File.ReadAllText(root+"LTTerrainDeformer.cs");
        var domain=File.ReadAllText(root+"Shaders/LTLayerTessellation.hlsl");
        var hull=File.ReadAllText(root+"Shaders/LTAdaptiveHull.hlsl");
        Require(runtime.Contains("||wantsDeformation")&&runtime.Contains("BeginDeformationControls"),"mud must work independently of texture displacement");
        Require(runtime.Contains("maxDisplacement+=maxDepression"),"renderer/culling bounds must include mud depth");
        Require(sim.Contains("shape.Closest(point-offset)")&&sim.Contains("collider.ClosestPoint(point)")&&sim.Contains("deformationTerrain.Sample"),"contact must check collider shape and actual source terrain, including holes");
        Require(deformer.Contains("world==target")&&deformer.Contains("SupportedCollider()"),"deformer must be scoped and collider support checked");
        Require(sim.Contains("travel>4?1")&&sim.Contains("1.0/30")&&sim.Contains("ReleaseDeformation"),"sweep/teleport handling and transient lifecycle missing");
        Require(domain.Contains("input.positionRWS.y-=LTDeformationDepth(position)*envelope")&&!domain.Contains("input.normalWS="),"mud must change positions only");
        Require(domain.IndexOf("if(activeAmplitude<=0)return 0")<domain.IndexOf("SAMPLE_TEXTURE2D_LOD(_LTWeights0"),"mud-only domain must skip ordinary displacement texture reads");
        Require(sim.Contains("if(d.contacted.Contains(p))continue"),"recovery must wait until contact leaves");
        Require(sim.Contains("Dictionary<LTPaintStamp,DeformationState> deformationRegions")&&
            sim.Contains("state.deformationRegions.Add(d)"),"one area map must be shared across overlapping terrain chunks");
        Require(sim.Contains("live.Count>=8||reserved+bytes>128L*1024*1024"),"area maps must have explicit count and memory budgets");
        Require(sim.Contains("if(!region.depthMap)continue")&&sim.Contains("if(d.active.Count==0)"),"untouched/recovered area maps must not cost GPU depth samples");
        Require(sim.Contains("Graphics.CopyTexture(d.uploadTile")&&sim.Contains("MarkDepthDirty(d,p)")&&
            sim.Contains("d.depthMap).Apply(false,d.partialUpload)")&&sim.Contains("CopyTextureSupport.Basic"),
            "depth requires dirty block GPU copies, GPU-only destination and compatibility fallback");
        Require(sim.Contains("foreach(int p in source.active)")&&sim.Contains("UpdateActivityFactors(world,dt)")&&
            sim.Contains("d.factorLive")&&sim.Contains("ResetFactorMap(d)"),
            "mud tessellation must follow active tracks and reset to coarse");
        Require(domain.Contains("_LTDeformationRegionSize7.xy-1")&&domain.Contains("depth=max(depth,SAMPLE_TEXTURE2D_LOD(_LTDeformationRegion0"),"area maps need endpoint-correct sampling and non-additive overlap");
        Require(!sim.Contains(".sharedMesh=")&&!sim.Contains(".vertices="),"simulation must not mutate mesh/collider geometry");
        Require(hull.Contains("channels.ba:channels.rg")&&hull.Contains("LTMudFactor(q,r),LTMudFactor(r,p),LTMudFactor(p,q)"),"mud density must be separate from ordinary density and edge-symmetric");
        Require(hull.Contains("LTMudTriangleFactor(p,q,r)")&&
            hull.Contains("sampler_PointClamp,uv,node.z")&&sim.Contains("conservative max"),
            "mud max mips must be queried against the actual triangle");
        Require(!hull.Contains("a=saturate(")&&!hull.Contains("LTMudFactor(min(p"),
            "mud queries must not clamp outside vertices or use triangle bounding boxes");
        var gpu=File.ReadAllText(root+"LTPaintMudGpu.cs");
        var compute=File.ReadAllText(root+"Resources/LTMudSimulation.compute");
        Require(gpu.Contains("RenderTextureFormat.RGFloat")&&gpu.Contains("g.contacts.Clear()")&&
            gpu.Contains("if(!contact&&now-g.updated<MudUpdateInterval(world,d))return"),
            "GPU state must be persistent, contacts drained, contact must bypass distant throttling");
        Require(!gpu.Contains("ReadPixels")&&!gpu.Contains("GetData(")&&gpu.Contains("g.tiles.Remove(tile)"),
            "GPU lifetime must avoid blocking readback and retire recovered tiles");
        Require(sim.Contains("wheel.GetGroundHit(out var hit)")&&sim.Contains("shape.kind<0")&&
            sim.Contains("predictedMudAreas")&&gpu.Contains("predictedMudAreas.Add"),
            "wheel grounding and density-only prediction missing");
        Require(compute.Contains("elapsed-c.duration")&&compute.Contains("min(c.limit,depth+c.dose)")&&
            compute.Contains("index=group.x+group.y*32768"),"GPU integration and dispatch indexing");
        Console.WriteLine("PASS deformation: opt-out/contact/caps, permanent/recovering tracks, height-blended restrictions, 2000 integration cases; lifecycle, collider/geometry and independent edge density source contracts. Unity contact/render verification still required.");
    }

    static void DeformationOptimizationChecks()
    {
        var random=new System.Random(518);
        Require(LTDeformationMath.UpdateInterval(20,30,100)==1f/30&&
            LTDeformationMath.UpdateInterval(50,30,100)==.1f&&
            LTDeformationMath.UpdateInterval(150,30,100)==.5f,"distance update tiers");
        Require(LTDeformationMath.Prediction(new Vector3(100,0,0),.2f).magnitude==3,"prediction capped at 3m");
        Require(LTDeformationMath.Prediction(Vector3.one,0)==Vector3.zero,"prediction can be disabled");
        // CPU reference of GPU integration, not execution of the compute shader.
        float GpuStep(float depth,float rate,float elapsed,float duration,float dose,float limit)
            =>Math.Min(limit,Math.Max(0,depth-rate*Math.Max(0,elapsed-duration))+dose);
        Require(Math.Abs(GpuStep(.2f,.02f,5,0,0,.2f)-.1f)<1e-6,"distant recovery preserves elapsed time");
        Require(Math.Abs(GpuStep(.1f,.02f,1,1,.02f,.2f)-.12f)<1e-6,"contact interval must not recover");
        Require(GpuStep(.1f,0,100,0,0,.2f)==.1f,"GPU permanent tracks");
        Require(GpuStep(.2f,.02f,20,0,0,.2f)==0,"GPU recovery clamps at zero");
        Require(GpuStep(.19f,.02f,.03f,.03f,.1f,.2f)==.2f,"GPU contact clamps depth");
        foreach(int count in new[]{1,32768,32769,65536})
        {
            int width=Math.Min(count,32768),rows=(count+32767)/32768;
            var visited=new bool[count];
            for(int y=0;y<rows;y++)for(int x=0;x<width;x++)
            {int index=x+y*32768;if(index<count){Require(!visited[index],"unique dispatch tile");visited[index]=true;}}
            Require(Array.TrueForAll(visited,v=>v),"large GPU dispatch covers all tiles");
        }
        var area=new Rect(2,3,4,5);
        Require(LTDeformationMath.ActivityFactor(area,4,5,1,1,32)==32,"contact core full density");
        Require(LTDeformationMath.ActivityFactor(area,0,5,1,1,32)==1,"untouched exterior coarse");
        Require(LTDeformationMath.ActivityFactor(area,1.5f,5,1,1,32)==16.5f,"activity apron interpolates");
        Require(LTDeformationMath.FadeFactor(1,32,.033f)==32,"first contact immediately dense");
        Require(LTDeformationMath.FadeFactor(63,1,.5f)==1,"recovered tracks become coarse");
        Require(LTDeformationMath.FadeFactor(32,1,.033f)>1,"density does not pop off");
        foreach(var dims in new[]{(2,2),(33,65),(251,501),(256,256),(1024,513)})
        {
            int width=dims.Item1,height=dims.Item2;
            var source=new float[width*height];var gpuReference=new float[source.Length];
            var dirty=new System.Collections.Generic.HashSet<int>();
            for(int round=0;round<3;round++)
            {
                // Include bottom/right partial tiles and zeroing after recovery.
                dirty.Clear();
                for(int i=0;i<500;i++)
                {
                    int p=i==0?source.Length-1:random.Next(source.Length);
                    source[p]=round==2?0:(float)random.NextDouble();
                    dirty.Add(LTDeformationMath.BlockIndex(p%width,p/width,width,32));
                }
                foreach(int block in dirty)
                {
                    var r=LTDeformationMath.BlockRect(block,width,height,32);
                    Require(r.width>0&&r.height>0&&r.xMax<=width&&r.yMax<=height,"partial upload stays in bounds");
                    for(int y=0;y<r.height;y++)
                        Array.Copy(source,(r.y+y)*width+r.x,gpuReference,(r.y+y)*width+r.x,r.width);
                }
                for(int p=0;p<source.Length;p++)Require(source[p]==gpuReference[p],"dirty blocks reproduce full upload including cleared texels");
            }
        }
        Require(LTDeformationMath.ClosestSphere(Vector3.zero,Vector3.zero,1)==Vector3.zero,"sphere inside stays inside");
        Require(LTDeformationMath.ClosestCapsule(Vector3.zero,new Vector3(0,-2,0),new Vector3(0,2,0),1)==Vector3.zero,"capsule inside");
        for(int i=0;i<2000;i++)
        {
            Vector3 Point()=>new Vector3((float)random.NextDouble()*10-5,(float)random.NextDouble()*10-5,(float)random.NextDouble()*10-5);
            var p=Point();var center=Point();float radius=.1f+(float)random.NextDouble()*2;
            var closest=LTDeformationMath.ClosestSphere(p,center,radius);
            Require(Math.Abs((p-closest).magnitude-Math.Max(0,(p-center).magnitude-radius))<1e-4,"sphere analytic distance");
            var a=Point();var b=Point();var axis=b-a;
            float t=Mathf.Clamp01(Vector3.Dot(p-a,axis)/axis.sqrMagnitude);
            closest=LTDeformationMath.ClosestCapsule(p,a,b,radius);
            Require(Math.Abs((p-closest).magnitude-Math.Max(0,(p-a-axis*t).magnitude-radius))<1e-4,"capsule analytic distance");
            Require((LTDeformationMath.ClosestCapsule(p,a,b,radius)-LTDeformationMath.ClosestCapsule(p,b,a,radius)).magnitude<1e-4,"capsule endpoint symmetry");
            float angle=(float)random.NextDouble()*6.28f;
            var x=new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle));
            var z=new Vector3(-Mathf.Sin(angle),0,Mathf.Cos(angle));
            var half=new Vector3(.5f,1,2);
            closest=LTDeformationMath.ClosestBox(p,center,x,Vector3.up,z,half);
            var delta=p-center;
            float dx=Mathf.Max(0,Mathf.Abs(Vector3.Dot(delta,x))-half.x);
            float dy=Mathf.Max(0,Mathf.Abs(delta.y)-half.y);
            float dz=Mathf.Max(0,Mathf.Abs(Vector3.Dot(delta,z))-half.z);
            Require(Math.Abs((p-closest).magnitude-Mathf.Sqrt(dx*dx+dy*dy+dz*dz))<1e-4,"rotated box distance");
            Require((closest-LTDeformationMath.ClosestBox(p,center,-x,Vector3.up,z,half)).magnitude<1e-4,"mirrored box basis");
        }
        Console.WriteLine("PASS deformation optimizations: active footprint/fade, dirty tile reconstruction on five rectangular sizes, 2000 analytic primitive distance cases. Native Collider and GPU copies require Unity.");
    }
    // CPU reference of the HLSL max-factor traversal, not GPU execution.
    static void MudHullChecks()
    {
        const int size=32;
        var levels=new System.Collections.Generic.List<float[]>();
        float Query(Vector2 a,Vector2 b,Vector2 c,out bool fallback,int budget=256)
        {
            fallback=false;
            if(!HullBox(a,b,c,0,0,size,0))return 1;
            float ceiling=levels[levels.Count-1][0],best=1;
            if(ceiling<=1)return 1;
            var pending=new System.Collections.Generic.Stack<(int x,int y,int level)>();
            pending.Push((0,0,levels.Count-1));
            int visits=0;
            while(pending.Count>0&&visits++<budget)
            {
                var n=pending.Pop();int span=1<<n.level;
                if(!HullBox(a,b,c,n.x*span,n.y*span,span,0))continue;
                float factor=levels[n.level][n.y*(size/span)+n.x];
                if(factor<=best)continue;
                if(n.level==0){best=factor;if(best>=ceiling)return best;continue;}
                pending.Push((n.x*2,n.y*2,n.level-1));pending.Push((n.x*2+1,n.y*2,n.level-1));
                pending.Push((n.x*2,n.y*2+1,n.level-1));pending.Push((n.x*2+1,n.y*2+1,n.level-1));
                Require(pending.Count<=40,"mud traversal stack capacity");
            }
            fallback=pending.Count>0;
            while(pending.Count>0)
            {
                var n=pending.Pop();int span=1<<n.level;
                if(HullBox(a,b,c,n.x*span,n.y*span,span,0))
                    best=Math.Max(best,levels[n.level][n.y*(size/span)+n.x]);
            }
            return best;
        }
        void Pyramid(float[] leaf)
        {
            levels.Clear();levels.Add(leaf);
            for(int width=size;width>1;width/=2)
            {
                var previous=levels[levels.Count-1];int next=width/2;
                var mip=new float[next*next];
                for(int y=0;y<next;y++)for(int x=0;x<next;x++)
                {
                    int p=y*2*width+x*2;
                    mip[y*next+x]=Math.Max(Math.Max(previous[p],previous[p+1]),
                        Math.Max(previous[p+width],previous[p+width+1]));
                }
                levels.Add(mip);
            }
        }
        var leaf=new float[size*size];Array.Fill(leaf,1);
        leaf[28*size+28]=32;Pyramid(leaf);
        Require(Query(Vector2.zero,new Vector2(31,0),new Vector2(0,31),out _)==1,
            "mud island inside AABB but outside triangle must not subdivide");
        Array.Fill(leaf,1);leaf[24*size+4]=24;Pyramid(leaf);
        Require(Query(Vector2.zero,new Vector2(31,31),new Vector2(31,31),out _)==1,
            "mud island off diagonal edge must not subdivide");
        Require(Query(Vector2.zero,new Vector2(31,31),new Vector2(0,31),out _)==24,
            "enclosed mud island must retain interior density");
        Require(Query(new Vector2(-5,24),new Vector2(-1,24),new Vector2(-1,30),out _)==1,
            "outside primitive must not project onto map");
        var random=new System.Random(2701);int fallbacks=0;
        Array.Fill(leaf,1);
        for(int i=0;i<100;i++)leaf[random.Next(leaf.Length)]=random.Next(2,64);
        Pyramid(leaf);
        for(int i=0;i<1000;i++)
        {
            Vector2 Point()=>new Vector2(random.Next(-5,38),random.Next(-5,38));
            var a=Point();var b=Point();var c=i%2==0?b:Point();
            if(i%2==0&&(a.x>b.x||(a.x==b.x&&a.y>b.y))){var swap=a;a=b;b=swap;c=b;}
            float expected=1;
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                if(HullBox(a,b,c,x,y,1,0))expected=Math.Max(expected,leaf[y*size+x]);
            float actual=Query(a,b,c,out bool fallback);
            if(fallback)fallbacks++;
            Require(fallback?actual>=expected:actual==expected,"mud traversal vs exhaustive intersecting leaves");
            Require(actual<=levels[levels.Count-1][0],"mud factor bounded by map maximum");
            float limited=Query(a,b,c,out _,1);
            Require(limited>=expected,"bounded mud traversal must not erase islands");
        }
        Console.WriteLine($"PASS mud hull CPU reference: off-triangle/off-edge islands, enclosed island, outside map, 1000 leaf scans; fallbacks {fallbacks}.");
    }
}
