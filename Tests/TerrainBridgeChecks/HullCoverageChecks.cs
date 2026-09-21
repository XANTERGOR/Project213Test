using System;
using System.Collections.Generic;
using UnityEngine;
using LocalTerrainPrototype;
partial class Checks
{
    // CPU reference of LTAdaptiveHull.hlsl, not execution of the GPU shader.
    static bool HullBox(Vector2 a,Vector2 b,Vector2 c,float x,float y,float span,float halo=1)
    {
        float loX=x-halo,loY=y-halo,hiX=x+span+halo,hiY=y+span+halo;
        if(Math.Max(a.x,Math.Max(b.x,c.x))<loX||Math.Min(a.x,Math.Min(b.x,c.x))>hiX||
           Math.Max(a.y,Math.Max(b.y,c.y))<loY||Math.Min(a.y,Math.Min(b.y,c.y))>hiY)return false;
        var center=new Vector2((loX+hiX)*.5f,(loY+hiY)*.5f);
        float half=(span+2*halo)*.5f;
        bool Separated(Vector2 e)
        {
            var axis=new Vector2(-e.y,e.x);
            float p=Vector2.Dot(axis,a-center),q=Vector2.Dot(axis,b-center),r=Vector2.Dot(axis,c-center);
            float radius=(Math.Abs(axis.x)+Math.Abs(axis.y))*half;
            return Math.Min(p,Math.Min(q,r))>radius||Math.Max(p,Math.Max(q,r))<-radius;
        }
        return !Separated(b-a)&&!Separated(c-b)&&!Separated(a-c);
    }
    static bool HullQuery(List<Color32[]> mips,int size,Vector2 a,Vector2 b,Vector2 c,out bool fallback,int budget=256,float halo=1)
    {
        var pending=new Stack<(int x,int y,int level)>();pending.Push((0,0,mips.Count-1));
        int visits=0;fallback=false;
        while(pending.Count>0&&visits++<budget)
        {
            var n=pending.Pop();int span=1<<n.level;
            if(!HullBox(a,b,c,n.x*span,n.y*span,span,halo))continue;
            if(mips[n.level][n.y*(size/span)+n.x].r==0)continue;
            if(n.level==0||mips[n.level][n.y*(size/span)+n.x].g>0)return true;
            pending.Push((n.x*2,n.y*2,n.level-1));pending.Push((n.x*2+1,n.y*2,n.level-1));
            pending.Push((n.x*2,n.y*2+1,n.level-1));pending.Push((n.x*2+1,n.y*2+1,n.level-1));
            Require(pending.Count<=40,"hull DFS stack capacity");
        }
        fallback=pending.Count>0;return fallback;
    }
    static void HullCoverageChecks()
    {
        ViewEdgeTessellationChecks();
        Require(LTPaintMath.DisplacementBaseStep(2,true)==.5f,"fine source step divides by four");
        Require(LTPaintMath.DisplacementGpuFactor(32,true)==8,"fine mode GPU factor divides by four");
        Require(LTPaintMath.DisplacementBaseStep(2,false)==2&&LTPaintMath.DisplacementGpuFactor(32,false)==32,"old settings preserved when disabled");
        Require(LTPaintMath.DisplacementBaseStep(0,true)==.125f,"fine source minimum remains positive");
        Require(LTPaintMath.DisplacementGpuFactor(1,true)==1&&LTPaintMath.DisplacementGpuFactor(100,true)==16,"fine GPU factor stays bounded");
        Console.WriteLine("PASS fine-base production settings: 2m/32 to 0.5m/8, disabled restoration, bounds.");
        // Same corner order/indices as LTStampMesh.Emit. Count incident triangles
        // across a regular grid: reproduce old 4/8 hubs, verify new constant six.
        foreach(bool consistent in new[]{false,true})
        {
            const int cells=6;
            var incidence=new int[(cells+1)*(cells+1)];
            var segments=new Dictionary<(int,int),int>();
            for(int z=0;z<cells;z++)for(int x=0;x<cells;x++)
            {
                int a=z*(cells+1)+x,b=a+cells+1,c=b+1,d=a+1;
                int[] tris=LTPaintMath.UseRegularCellDiagonal(x,z,consistent)
                    ?new[]{a,b,d,d,b,c}:new[]{a,b,c,c,d,a};
                foreach(int v in tris)incidence[v]++;
                for(int t=0;t<tris.Length;t+=3)for(int e=0;e<3;e++)
                {
                    int u=tris[t+e],v=tris[t+(e+1)%3];
                    var key=(Math.Min(u,v),Math.Max(u,v));
                    segments.TryGetValue(key,out int count);segments[key]=count+1;
                }
            }
            for(int z=1;z<cells;z++)for(int x=1;x<cells;x++)
                Require(incidence[z*(cells+1)+x]==(consistent?6:((x+z)%2==0?4:8)),
                    "regular topology removes checkerboard 4/8 hubs");
            foreach(var pair in segments)
            {
                int u=pair.Key.Item1,v=pair.Key.Item2;
                bool boundary=u%(cells+1)==v%(cells+1)&&(u%(cells+1)==0||u%(cells+1)==cells)
                    ||u/(cells+1)==v/(cells+1)&&(u/(cells+1)==0||u/(cells+1)==cells);
                Require(pair.Value==(boundary?1:2),"regular grid retains shared edges without holes");
            }
        }
        Console.WriteLine("PASS production diagonal selection: old 4/8 hubs vs regular six, shared-edge incidence.");
        // Production triangulator: every balanced-cell midpoint pattern, including
        // collinear boundary points. Check area, nondegeneracy and edge incidence.
        for(int pattern=0;pattern<16;pattern++)foreach(float aspect in new[]{.25f,1f,4f})
        {
            var polygon=new List<Vector2>();
            Vector2[] corners={new Vector2(0,0),new Vector2(0,1),new Vector2(aspect,1),new Vector2(aspect,0)};
            for(int side=0;side<4;side++)
            {
                polygon.Add(corners[side]);
                if((pattern&(1<<side))!=0)polygon.Add((corners[side]+corners[(side+1)%4])*.5f);
            }
            int[] topology=LTPaintMath.TriangulateTransition(polygon.ToArray());
            Require(topology.Length==(polygon.Count-2)*3,"transition emits n-2 triangles");
            float areaSum=0;var edges=new Dictionary<(int,int),int>();
            for(int t=0;t<topology.Length;t+=3)
            {
                Vector2 a=polygon[topology[t+1]]-polygon[topology[t]],b=polygon[topology[t+2]]-polygon[topology[t]];
                float cross=a.x*b.y-a.y*b.x;
                Require(cross<0,"transition preserves winding without zero-area triangles");areaSum-=cross*.5f;
                for(int e=0;e<3;e++)
                {
                    int i=topology[t+e],j=topology[t+(e+1)%3];var key=(Math.Min(i,j),Math.Max(i,j));
                    edges.TryGetValue(key,out int uses);edges[key]=uses+1;
                }
            }
            Require(Math.Abs(areaSum-aspect)<1e-5f,"transition covers cell exactly");
            for(int i=0;i<polygon.Count;i++)
            {
                int j=(i+1)%polygon.Count;var key=(Math.Min(i,j),Math.Max(i,j));
                Require(edges.TryGetValue(key,out int uses)&&uses==1,"transition preserves every boundary segment");edges.Remove(key);
            }
            foreach(int uses in edges.Values)Require(uses==2,"internal diagonals have two incident triangles");
        }
        Console.WriteLine("PASS production transition triangulation: all 16 midpoint patterns, 3 aspects, area/winding/shared boundaries.");
        var transitionMip=new List<Color32[]>{new[]{new Color32(255,255,0,255)}};
        Vector2 edgeA=new Vector2(2.5f,0),edgeB=new Vector2(2.5f,1);
        Require(!HullQuery(transitionMip,1,edgeA,edgeB,edgeB,out _),"old support leaves near exterior edge coarse");
        Require(HullQuery(transitionMip,1,edgeA,edgeB,edgeB,out _,halo:2),"transition supports nearby exterior edge");
        Require(HullQuery(transitionMip,1,edgeB,edgeA,edgeA,out _,halo:2),"reversed shared edge retains support");
        var farA=new Vector2(5,0);var farB=new Vector2(5,1);
        Require(!HullQuery(transitionMip,1,farA,farB,farB,out _,halo:2),"far empty region remains coarse");
        for(int maximum=1;maximum<=64;maximum++)
        {
            float transitionFactor=1+.5f*(maximum-1);
            Require(transitionFactor>=1&&transitionFactor<=maximum,"transition factor remains bounded");
            float interior=Math.Max(1,(transitionFactor+1+1)/3);
            Require(interior>=1&&interior<=maximum,"transition interior remains bounded");
        }
        Console.WriteLine("PASS transition CPU reference: near/far support, reversed edge, factor bounds.");
        // Actual production distance builder: hole, straight edge, anisotropic metres,
        // empty/full masks and no artificial empty border at an internal chunk edge.
        const int distanceSize=9;
        var distanceMask=new Color32[distanceSize*distanceSize];
        var emptyDistance=LocalTerrainPrototype.LTPaintMath.MaskInteriorDistance(distanceMask,distanceSize,.25f,.5f);
        Require(Array.TrueForAll(emptyDistance,v=>v==0),"empty distance mask is zero");
        for(int i=0;i<distanceMask.Length;i++)distanceMask[i]=new Color32(255,0,0,255);
        var fullDistance=LocalTerrainPrototype.LTPaintMath.MaskInteriorDistance(distanceMask,distanceSize,.25f,.5f);
        Require(fullDistance[0]>1&&fullDistance[0]==fullDistance[40],"full mask has no invented chunk boundary");
        distanceMask[40]=new Color32(0,0,0,255);
        var holeDistance=LocalTerrainPrototype.LTPaintMath.MaskInteriorDistance(distanceMask,distanceSize,.25f,.5f);
        Require(holeDistance[40]==0&&holeDistance[41]==.25f&&holeDistance[49]==.5f,"hole distance in metres");
        Require(Math.Abs(holeDistance[50]-(float)Math.Sqrt(.25f*.25f+.5f*.5f))<1e-6f,"diagonal distance");
        for(int z=0;z<distanceSize;z++)for(int x=0;x<distanceSize;x++)distanceMask[z*distanceSize+x]=new Color32((byte)(x<3?0:255),0,0,255);
        var edgeDistance=LocalTerrainPrototype.LTPaintMath.MaskInteriorDistance(distanceMask,distanceSize,.25f,.5f);
        for(int z=0;z<distanceSize;z++)for(int x=0;x<distanceSize;x++)
            Require(edgeDistance[z*distanceSize+x]==Math.Max(0,x-2)*.25f,"straight boundary distance stays independent of row");
        Console.WriteLine("PASS mask edge distance: empty/full, holes, diagonal, straight edge, anisotropic metres, chunk border.");
        // CPU reference of the pre-HDRP edge cap, not native HLSL execution.
        float EdgeFactor(Vector3 a,Vector3 b,float target,float maximum)
            =>target<=0?maximum:Math.Min(maximum,Math.Max(1,(a-b).magnitude/Math.Max(.02f,target)));
        Require(Math.Abs(EdgeFactor(Vector3.zero,new Vector3(.1f,0,0),.02f,64)-5)<.0001f,"two-centimetre target is not clamped to five centimetres");
        Require(EdgeFactor(Vector3.zero,new Vector3(.1f,0,0),.01f,64)==EdgeFactor(Vector3.zero,new Vector3(.1f,0,0),.02f,64),"subminimum positive target clamps to two centimetres");
        Require(EdgeFactor(Vector3.zero,new Vector3(.125f,0,0),.25f,8)==1,"short edge does not need subdivision");
        Require(EdgeFactor(Vector3.zero,new Vector3(.5f,0,0),.25f,8)==2,"half-metre edge uses factor two not eight");
        Require(EdgeFactor(Vector3.zero,new Vector3(4,0,0),.25f,8)==8,"long edge obeys maximum");
        Require(EdgeFactor(Vector3.zero,new Vector3(.5f,0,0),0,8)==8,"disabled target retains fixed factor");
        Require(EdgeFactor(Vector3.zero,new Vector3(.5f,0,0),.25f,1)==1,"factor one stays one");
        var capRandom=new System.Random(955);
        for(int i=0;i<1000;i++)
        {
            var a=new Vector3((float)capRandom.NextDouble()*4,(float)capRandom.NextDouble(),(float)capRandom.NextDouble()*4);
            var b=new Vector3((float)capRandom.NextDouble()*4,(float)capRandom.NextDouble(),(float)capRandom.NextDouble()*4);
            float small=EdgeFactor(a,b,.25f,8),large=EdgeFactor(a,b,.5f,8);
            Require(small==EdgeFactor(b,a,.25f,8),"shared edge orientation preserves factor");
            Require(large<=small&&large>=1&&small<=8,"larger target never increases subdivision");
        }
        Console.WriteLine("PASS target-edge CPU reference: short/long edges, off switch, cap, 1000 symmetry/monotonicity checks.");
        const int size=64,res=65;
        var weights=new Color32[res*res];var empty=new Color32[weights.Length];
        for(int y=0;y<res;y++)for(int x=0;x<res;x++)
            if(x<16||x>48||y<16||y>48)weights[y*res+x]=new Color32(255,0,0,255);
        var ring=LTPaintMath.DisplacementPyramid(weights,empty,res,1);
        Require(HullQuery(ring,size,Vector2.zero,new Vector2(63,0),new Vector2(0,63),out var limited,1)&&limited,"budget exhaustion remains conservative and distinguishable from hit");
        Require(!HullQuery(ring,size,new Vector2(-20,-20),new Vector2(-10,-20),new Vector2(-20,-10),out limited,1)&&!limited,"completed empty query is not marked as budget fallback");
        Require(!HullQuery(ring,size,new Vector2(24,24),new Vector2(40,24),new Vector2(24,40),out var fallback)&&!fallback,"black hole inside white ring stays coarse");
        Array.Clear(weights,0,weights.Length);weights[60*res+60]=new Color32(255,0,0,255);
        var corner=LTPaintMath.DisplacementPyramid(weights,empty,res,1);
        Require(!HullQuery(corner,size,Vector2.zero,new Vector2(63,0),new Vector2(0,63),out fallback)&&!fallback,"white pixel inside AABB but outside triangle excluded");
        Array.Clear(weights,0,weights.Length);weights[50*res+10]=new Color32(255,0,0,255);
        var offEdge=LTPaintMath.DisplacementPyramid(weights,empty,res,1);
        Require(!HullQuery(offEdge,size,Vector2.zero,new Vector2(63,63),new Vector2(63,63),out fallback)&&!fallback,"diagonal edge excludes off-edge white pixel inside its AABB");
        Require(HullQuery(offEdge,size,Vector2.zero,new Vector2(63,63),new Vector2(0,63),out limited)&&!limited,"confirmed interior hit is not marked as budget fallback");
        var random=new System.Random(128);int fallbackCount=0;
        for(int i=0;i<2000;i++)
        {
            Vector2 Point()=>new Vector2(random.Next(-10,75),random.Next(-10,75));
            var a=Point();var b=Point();var c=i%2==0?b:Point();
            bool actual=HullQuery(ring,size,a,b,c,out fallback);if(fallback)fallbackCount++;
            bool expected=false;
            for(int y=0;y<size&&!expected;y++)for(int x=0;x<size&&!expected;x++)
                expected=ring[0][y*size+x].r>0&&HullBox(a,b,c,x,y,1);
            Require(fallback?actual:actual==expected,"hierarchical query equals leaf scan unless explicitly budget-limited");
            Require(actual==HullQuery(ring,size,b,a,i%2==0?a:c,out _),"shared edges and reversed winding agree");
        }
        Console.WriteLine($"PASS hull coverage CPU reference: holes, off-triangle/off-edge islands, 2000 leaf-scan comparisons; conservative fallbacks {fallbackCount}.");
    }
}
