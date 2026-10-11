using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void DensityMaskChecks()
    {
        LTStampDensityMask Mask(int w,int h,Func<int,int,float> sample,bool invert=false,Vector2? range=null,float power=1)
            =>new LTStampDensityMask(w,h,y=>Enumerable.Range(0,w).Select(x=>new Color(sample(x,y),0,0,1)).ToArray(),invert,range??new Vector2(0,1),power);
        void Near(float a,float b,string label)=>Require(Math.Abs(a-b)<1e-6,label+$" ({a} vs {b})");
        Matrix4x4 InverseFootprint(float x,float z,float angle,float sx=1,float sz=1)
        {
            float c=(float)Math.Cos(angle*Math.PI/180),s=(float)Math.Sin(angle*Math.PI/180);
            var m=Matrix4x4.identity;m.m00=c/sx;m.m02=-s/sx;m.m20=s/sz;m.m22=c/sz;
            m.m03=-m.m00*x-m.m02*z;m.m23=-m.m20*x-m.m22*z;return m;
        }
        var white=Mask(3,5,(x,y)=>1);var black=Mask(3,5,(x,y)=>0);var grey=Mask(1,1,(x,y)=>.5f);
        Near(white.Maximum(new Rect(-1,-1,3,3)),1,"white mask");
        Near(black.Maximum(new Rect(0,0,1,1)),0,"black mask");
        Near(grey.Sample(.2f,.8f),.5f,"single texel sampling");
        Near(Mask(1,1,(x,y)=>.25f,true).Sample(0,0),.75f,"inversion");
        Near(Mask(1,1,(x,y)=>.5f,false,new Vector2(.25f,.75f),2).Sample(0,0),.25f,"range and power");
        Near(Mask(1,1,(x,y)=>.1f,true,new Vector2(.8f,1)).Sample(0,0),.5f,"invert before remap");
        var random=new System.Random(741);
        int queries=0;
        foreach(var dims in new[]{(1,1),(1,11),(13,1),(3,5),(64,32),(127,65)})
        {
            int w=dims.Item1,h=dims.Item2;var input=new float[w*h];
            for(int i=0;i<input.Length;i++)input[i]=(float)random.NextDouble();
            foreach(bool invert in new[]{false,true})
            {
                var mask=Mask(w,h,(x,y)=>input[y*w+x],invert);
                for(int q=0;q<200;q++)
                {
                    float a=(float)random.NextDouble()*1.4f-.2f,b=(float)random.NextDouble()*1.4f-.2f;
                    float c=(float)random.NextDouble()*1.4f-.2f,d=(float)random.NextDouble()*1.4f-.2f;
                    var uv=Rect.MinMaxRect(Math.Min(a,b),Math.Min(c,d),Math.Max(a,b),Math.Max(c,d));
                    int x0=Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(uv.xMin)*w-.5f),0,w-1),x1=Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(uv.xMax)*w-.5f),0,w-1);
                    int y0=Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(uv.yMin)*h-.5f),0,h-1),y1=Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(uv.yMax)*h-.5f),0,h-1);
                    float expected=0;
                    for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)expected=Math.Max(expected,invert?1-input[y*w+x]:input[y*w+x]);
                    Near(mask.Maximum(uv),expected,"conservative query vs brute force");queries++;
                }
            }
        }
        var region=new Rect(0,0,8,8);var inverse=Matrix4x4.Translate(new Vector3(-4,0,-4));
        var size=new Vector2(8,8);
        LTStampMesh.Zone Zone(LTStampDensityMask mask)=>mask.CreateZone(region,inverse,size,LTStampShape.Rectangle,.125f,2);
        List<Vector3Int> Plan(params LTStampMesh.Zone[] zones)=>LTStampMesh.Plan(region,4,true,.1f,100000,zones.ToList(),(x,z)=>0,new LTLODMesh.BuildProgress(false));
        var blackPlan=Plan(Zone(black));var whitePlan=Plan(Zone(white));
        Require(blackPlan.Count==16,"black uses max cell size");
        Require(whitePlan.Count==4096,"white uses min cell size");
        Near(Zone(grey).CellSizeAt(4,4),1.0625f,"grey interpolation");
        // The isolated texel is far from all the initial cell-centre samples.
        var island=Mask(64,64,(x,y)=>x==13&&y==21?1:0);var islandZone=Zone(island);var islandPlan=Plan(islandZone);
        Require(islandPlan.Count>blackPlan.Count&&islandPlan.Count<whitePlan.Count,"small island refines locally");
        float px=(13.5f/64)*8,pz=(21.5f/64)*8;
        foreach(var cell in islandPlan)
        {
            float x=cell.x/(float)(1<<24)*8,z=cell.y/(float)(1<<24)*8,step=cell.z/(float)(1<<24)*8;
            if(px>=x&&px<x+step&&pz>=z&&pz<z+step)Require(step<=.125001f,"white island cannot be skipped by centre-only sampling");
        }
        // Later full-footprint stamps still override earlier masks in either direction.
        Require(Plan(Zone(white),Zone(black)).SequenceEqual(blackPlan),"later coarse mask wins");
        Require(Plan(Zone(black),Zone(white)).SequenceEqual(whitePlan),"later fine mask wins");
        var shapeCoarse=new LTStampMesh.Zone{bounds=region,inverse=inverse,size=size,shape=LTStampShape.Rectangle,cellSize=2,edgeCellSize=2};
        Require(Plan(Zone(white),shapeCoarse).SequenceEqual(blackPlan),"later shape wins over mask");
        var ellipse=white.CreateZone(region,inverse,size,LTStampShape.Ellipse,.125f,2);
        Require(ellipse.Contains(4,4)&&!ellipse.Contains(.1f,.1f),"ellipse retains footprint");
        var rotated=island.CreateZone(new Rect(-30,-30,70,70),InverseFootprint(12,-7,37,2,3),size,LTStampShape.Rectangle,.125f,2);
        float co=(float)Math.Cos(37*Math.PI/180),si=(float)Math.Sin(37*Math.PI/180);
        var point=new Vector3(12+co*2*(px-4)+si*3*(pz-4),0,-7-si*2*(px-4)+co*3*(pz-4));
        // Float forward/inverse transforms move this sample slightly off the
        // exact texel centre. The region maximum must still reach min exactly.
        Require(Math.Abs(rotated.CellSizeAt(point.x,point.z)-.125f)<.00002f,"rotated nonuniform scale UV");
        Require(rotated.CellSizeForRegion(new Rect(point.x-.01f,point.z-.01f,.02f,.02f))<=.125001f,"rotated conservative query");
        // Source ownership: reused/overwritten caller rows cannot mutate the snapshot.
        var row=new[]{new Color(1,0,0,1),new Color(0,0,0,1)};int reads=0;
        var immutable=new LTStampDensityMask(2,2,y=>{reads++;return row;},false,new Vector2(0,1),1);
        row[0]=Color.black;
        for(int i=0;i<100;i++)Near(immutable.Maximum(new Rect(0,0,1,1)),1,"immutable snapshot");
        Require(reads==2,"no texture reads during queries");
        foreach(var bad in new[]{new Vector2(1,0),new Vector2(.5f,.5f),new Vector2(float.NaN,1)})
        {bool threw=false;try{Mask(1,1,(x,y)=>1,false,bad);}catch(ArgumentException){threw=true;}Require(threw,"invalid range rejected");}
        bool budget=false;try{LTStampMesh.Plan(region,4,true,.1f,20,new List<LTStampMesh.Zone>{Zone(white)},(x,z)=>0,new LTLODMesh.BuildProgress(false));}catch(InvalidOperationException){budget=true;}
        Require(budget,"mask obeys existing mesh budget without clamping");
        // A thin bright strip crossing a chunk boundary must keep an oriented,
        // manifold surface after the real forest balancer and emitter run.
        var seamMask=Mask(128,64,(x,y)=>(x==63||x==64)&&y==21?1:0);
        var seamZone=seamMask.CreateZone(new Rect(0,0,16,8),Matrix4x4.Translate(new Vector3(-8,0,-4)),new Vector2(16,8),LTStampShape.Rectangle,.125f,2);
        var plans=new Dictionary<int,List<Vector3Int>>();
        for(int i=0;i<2;i++)plans[i]=LTStampMesh.Plan(new Rect(i*8,0,8,8),4,true,.1f,100000,new List<LTStampMesh.Zone>{seamZone},(x,z)=>0,new LTLODMesh.BuildProgress(false));
        var forest=new LTBalancedForest(2,1,100000,plans);forest.Balance();
        var edges=new Dictionary<(int,int,int,int),int>();double area=0;
        for(int chunk=0;chunk<2;chunk++)
        {
            LTStampMesh.Emit(new Rect(chunk*8,0,8,8),new Vector2(16,8),100000,forest,chunk,forest.Plan(chunk),(x,z)=>0,null,
                out var positions,out var normals,out var uv,out var indices,null,new LTLODMesh.BuildProgress(false));
            for(int i=0;i<indices.Length;i+=3)
            {
                Vector3 a=positions[indices[i]],b=positions[indices[i+1]],c=positions[indices[i+2]];
                float signed=Vector3.Cross(b-a,c-a).y;Require(signed>0,"mask mesh upward winding");area+=signed*.5;
                for(int k=0;k<3;k++)
                {
                    a=positions[indices[i+k]];b=positions[indices[i+(k+1)%3]];
                    int ax=Mathf.RoundToInt((a.x+chunk*8)*16),az=Mathf.RoundToInt(a.z*16),bx=Mathf.RoundToInt((b.x+chunk*8)*16),bz=Mathf.RoundToInt(b.z*16);
                    var key=ax<bx||ax==bx&&az<bz?(ax,az,bx,bz):(bx,bz,ax,az);
                    edges.TryGetValue(key,out int count);edges[key]=count+1;
                }
            }
        }
        Require(Math.Abs(area-128)<1e-6,"two-chunk mask mesh area preserved");
        foreach(var edge in edges)
        {
            Require(edge.Value<=2,"mask mesh manifold edges");
            if(edge.Value==2)continue;var e=edge.Key;
            Require(e.Item1==0&&e.Item3==0||e.Item1==256&&e.Item3==256||e.Item2==0&&e.Item4==0||e.Item2==128&&e.Item4==128,"no mask-induced hole or T-junction at shared boundary");
        }
        // Frozen pre-change planner oracle: shape mode is exactly unchanged.
        int shapeCases=0;
        foreach(var shape in new[]{LTStampShape.Rectangle,LTStampShape.Ellipse})foreach(bool adaptive in new[]{false,true})foreach(float angle in new[]{0f,23f})
        {
            var inv=InverseFootprint(4,4,angle);
            var oldZone=new LTStampMeshReference.Zone{bounds=region,inverse=inv,size=new Vector2(6,6),shape=shape,cellSize=.25f,edgeCellSize=1};
            var newZone=new LTStampMesh.Zone{bounds=region,inverse=inv,size=new Vector2(6,6),shape=shape,cellSize=.25f,edgeCellSize=1};
            float Height(float x,float z)=>(float)Math.Sin(x*.5f)*.1f;
            var expected=LTStampMeshReference.Plan(region,4,adaptive,.1f,100000,new List<LTStampMeshReference.Zone>{oldZone},Height,new LTLODMesh.BuildProgress(false));
            var actual=LTStampMesh.Plan(region,4,adaptive,.1f,100000,new List<LTStampMesh.Zone>{newZone},Height,new LTLODMesh.BuildProgress(false));
            Require(actual.SequenceEqual(expected),"legacy shape exact plan parity");shapeCases++;
        }
        // Native capture/cache integration is also checked as a source contract;
        // these assertions are NOT a Unity texture-import or visual test.
        string editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        string component=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTHeightStamp.cs");
        Require(component.Contains("Shape = 0, Mask = 1"),"serialized default preserves existing stamps");
        Require(editor.Contains("old.maskKey==maskKey")&&editor.Contains("maskData=old.maskData"),"reuse snapshot for unchanged content/remap");
        Require(editor.Contains("AssetInputHash(densityTexture)")&&editor.Contains("sig+=densityKey"),"texture reimports invalidate geometry even with Affect Height off");
        Require(editor.Contains("!stamp.densityMask||!stamp.densityMask.isReadable"),"missing/unreadable mask is explicit and safe");
        Require(editor.Contains("densityTexture=s.densityMask?s.densityMask:s.mask"),"density override falls back to stamp mask");
        Console.WriteLine($"PASS density mask: {queries} range queries, {shapeCases} exact legacy plans, local island {islandPlan.Count} vs coarse {blackPlan.Count}/fine {whitePlan.Count}; two-chunk seam/winding/area, remap/inversion/rotation/precedence/budget/cache contracts.");
    }
}
