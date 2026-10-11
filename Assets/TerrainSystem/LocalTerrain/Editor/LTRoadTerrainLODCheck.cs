using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public static partial class LTRoadTerrainLODCheck
    {
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        internal static LTRoadTerrainSurface.Part Grid(float height,int level=0,bool hole=true)
        {
            var vertices=new List<Vector3>();var indices=new List<int>();
            for(int z=-4;z<68;z++)for(int x=16;x<48;x++)
            {
                if(hole&&x>=29&&x<35&&z>=29&&z<35)continue;
                int start=vertices.Count;
                vertices.Add(new Vector3(x*.25f,height,z*.25f));vertices.Add(new Vector3(x*.25f,height,(z+1)*.25f));
                vertices.Add(new Vector3((x+1)*.25f,height,(z+1)*.25f));vertices.Add(new Vector3((x+1)*.25f,height,z*.25f));
                indices.AddRange(new[]{start,start+1,start+2,start+2,start+3,start});
            }
            return new LTRoadTerrainSurface.Part{vertices=vertices.ToArray(),indices=indices.ToArray(),level=level};
        }
        public static string RunData()
        {
            const int n=LTSpatialLODMath.N,resolution=64,budget=100000,divisions=4;
            var rect=new Rect(0,0,16,16);var fine=new List<Vector3Int>();
            for(int z=0;z<n;z+=n/resolution)for(int x=0;x<n;x+=n/resolution)fine.Add(new Vector3Int(x,z,n/resolution));
            var original=fine.ToArray();var part=Grid(.04f);var surface=new LTRoadTerrainSurface(new[]{part});
            LTRoadTerrainLODProtection Prepare(LTRoadTerrainSurface data,Func<float,float,float> h)
            {var p=new LTRoadTerrainLODProtection();foreach(var step in p.Prepare(fine,rect,new[]{data},h)){}return p;}
            Rect Area(Vector3Int p)=>new Rect(p.x/(float)n*16,p.y/(float)n*16,p.z/(float)n*16,p.z/(float)n*16);
            var protection=Prepare(surface,(x,z)=>0);
            Require(protection.ReferenceEdgeCells>0&&protection.ReferenceEdgeCells<fine.Count/2,"Edge/hole reference became a blanket lock.");
            Require(!protection.Protects(new Rect(7,7,1,1)),"Flat hole edge cannot simplify.");
            Require(!protection.Protects(new Rect(3.5f,4,.5f,.5f)),"Flat external road edge remains locked.");
            Require(!protection.Protects(new Rect(0,0,2,2)),"Shoulder stays permanently locked.");
            Require(!protection.Protects(new Rect(5,3,1,1)),"Safe asphalt foundation cannot merge.");
            var cuts=new LTRoadTerrainLODProtection();
            foreach(var _ in cuts.Prepare(fine,rect,new[]{surface},(x,z)=>0,remeshed:new[]{new Rect(5.2f,3.2f,.1f,.1f)})){}
            Require(cuts.Protects(new Rect(5,3,1,1))&&!cuts.Protects(new Rect(5,5,1,1)),"Post-cut fresh-height guard must be local, not blanket.");
            var low=Grid(-.01f,1);var all=new LTRoadTerrainSurface(new[]{part,low});
            Require(Prepare(all,(x,z)=>0).Protects(new Rect(5,3,1,1)),"Lower road LOD was ignored.");
            Require(Prepare(surface,(x,z)=>x==5.5f&&z==3.5f?.1f:0).Protects(new Rect(5,3,1,1)),"Centre fan height was ignored.");
            Require(Prepare(surface,(x,z)=>x==5&&z==3.5f?.1f:0).Protects(new Rect(5,3,1,1)),"Stitch midpoint height was ignored.");
            // Clearance uses clipped triangles, not just road samples on a terrain stencil.
            var dip=new LTRoadTerrainSurface(new[]{new LTRoadTerrainSurface.Part{
                vertices=new[]{new Vector3(.1f,-.1f,.1f),new Vector3(.2f,-.1f,.3f),new Vector3(.3f,-.1f,.1f)},indices=new[]{0,1,2}}});
            Require(dip.Blocks(new Rect(0,0,1,1),0,0,0,0),"Sub-stencil road depression missed.");
            Require(!dip.Blocks(new Rect(.3f,.3f,.2f,.2f),0,0,0,0),"Triangle AABB creates false terrain lock.");
            Require(dip.Exceeds(new Vector3(0,0,0),new Vector3(0,0,1),new Vector3(1,0,0),-.0001),"Exact triangle intersection missed a small depression.");
            Require(!dip.Exceeds(new Vector3(.3f,0,.3f),new Vector3(.3f,0,.5f),new Vector3(.5f,0,.3f),-.0001),"Disjoint triangles falsely intersect.");
            var tinyPart=new LTRoadTerrainSurface.Part{vertices=new[]{new Vector3(0,.04f,0),new Vector3(0,.04f,.125f),new Vector3(.125f,.04f,0)},indices=new[]{0,1,2}};
            var tiny=new LTRoadTerrainSurface(new[]{tinyPart});
            Require(tiny.Blocks(new Rect(0,0,1,1),0,0,0,.1),"False-positive fixture no longer exercises old envelope rejection.");
            Require(!Prepare(tiny,(x,z)=>.1f*x*z).Protects(new Rect(0,0,1,1)),"Exact test did not release the safe merge rejected by v1.");
            float ShoulderBump(float x,float z)=>x==3.75f&&z==4.25f?.2f:0;
            Require(Prepare(surface,ShoulderBump).Protects(new Rect(3.5f,4,.5f,.5f)),"Visible shoulder error ignored.");
            Require(!Prepare(new LTRoadTerrainSurface(new[]{Grid(.04f)},.3f),ShoulderBump).Protects(new Rect(3.5f,4,.5f,.5f)),"Edge error setting cannot release a safe merge.");
            float HoleBump(float x,float z)=>x==7.5f&&z==7.5f?.2f:0;
            Require(Prepare(surface,HoleBump).Protects(new Rect(7,7,1,1)),"Visible hole-floor/rim error ignored.");
            var tilted=Grid(0);for(int i=0;i<tilted.vertices.Length;i++){var p=tilted.vertices[i];p.y=.05f+p.x*.2f+p.z*.1f;tilted.vertices[i]=p;}
            Require(!Prepare(new LTRoadTerrainSurface(new[]{tilted}),(x,z)=>x*.2f+z*.1f).Protects(new Rect(5,3,1,1)),"Sloped surface cannot merge.");
            // Input arrays can be released/reused without changing the accepted snapshot.
            part.vertices[0]=new Vector3(100,100,100);part.indices[0]=1;
            Require(surface.Bounds.xMax==12&&!surface.Blocks(new Rect(5,3,1,1),0,0,0,0),"Surface retained mutable caller arrays.");

            var levels=new List<Vector3Int>[5];levels[0]=fine;var preparation=new LTLODMesh.Preparation(fine,rect,(x,z)=>0);
            for(int l=1;l<levels.Length;l++)levels[l]=preparation.Coarsen(l,.1f,null,protection.Protects,divisions,false);
            Require(levels[4].Count<100,"Flat far foundation still retains the old edge ring (v1: 904 cells).");
            var retained=new HashSet<Vector3Int>(levels[4]);
            foreach(var p in fine.Where(p=>protection.Protects(Area(p))))Require(retained.Contains(p),"Protected leaf lost.");
            var whole=preparation.Coarsen(4,.1f,null,protection.Protects);
            LTLODMesh.ValidateBoundary(fine,whole);Require(whole.Count<fine.Count,"WholeChunk remains fixed.");
            var output=LTSpatialLODMath.BuildLayout(levels,divisions,budget,p=>0,p=>protection.Protects(Area(p)));
            LTStampMesh.EmitSpatialVariants(output,rect,new Vector2(16,16),budget,(x,z)=>0,null);
            var topology=new LTSpatialLODTopology(1,1,divisions,new[]{new LTSpatialLODTopology.Chunk{cells=output.cells,patches=output.patches}});
            var buffer=new List<int>();int[] baseline=null;int far=0;
            for(int pass=0;pass<7;pass++)
            {
                for(int p=0;p<divisions*divisions;p++)topology.SetLevel(0,p,pass<5?pass:pass==5?p%5:0);
                topology.Update();topology.Validate();topology.WriteIndices(0,buffer);
                if(pass==0)baseline=buffer.ToArray();if(pass==4)far=buffer.Count/3;
                if(pass==6)Require(baseline.SequenceEqual(buffer),"Returning near failed to restore LOD0 indices.");
                VerifyEdges(output.vertices,buffer);
            }
            Require(fine.SequenceEqual(original),"LOD0/collider plan changed.");
            // Exercise the actual emitter for every possible midpoint mask and both diagonal policies.
            int emitted=0;var rng=new System.Random(941);
            for(int round=0;round<8;round++)
            {
                var values=Enumerable.Range(0,9).Select(_=>(float)rng.NextDouble()*.02f).ToArray();
                float H(float x,float z)=>values[Mathf.RoundToInt(z*2)*3+Mathf.RoundToInt(x*2)];
                var one=new Rect(0,0,1,1);double h=H(0,0),sx=H(1,0)-h,sz=H(0,1)-h,residual=0;
                for(int z=0;z<3;z++)for(int x=0;x<3;x++)residual=Math.Max(residual,H(x*.5f,z*.5f)-h-sx*x*.5-sz*z*.5);
                for(int mask=0;mask<16;mask++)foreach(bool regular in new[]{false,true})
                {
                    LTStampMesh.Emit(one,Vector2.one,budget,null,0,new List<Vector3Int>{new Vector3Int(0,0,n)},H,null,
                        out var vertices,out _,out _,out var indices,_=>regular,midpoint:(cell,side)=>(mask&(1<<side))!=0);
                    for(int t=0;t<indices.Length;t+=3)for(int sample=0;sample<5;sample++)
                    {
                        float a=(float)rng.NextDouble(),b=(float)rng.NextDouble()*(1-a);
                        var p=vertices[indices[t]]*a+vertices[indices[t+1]]*b+vertices[indices[t+2]]*(1-a-b);
                        Require(p.y<=h+sx*p.x+sz*p.z+residual+1e-7,"Emitted triangle escaped the safety envelope.");emitted++;
                    }
                }
            }
            VerifyFourChunks(surface);int variants=VerifyTriangulation();
            return $"PASS data: foundation cells {fine.Count} -> {levels[4].Count}; tris {baseline.Length/3} -> {far} (v1 same flat fixture: 2588 far tris); {emitted} envelope probes, {variants} exact emitter variants; safe envelope false-positive released, controlled edge/hole error, all road LODs, mixed patch/chunk seams, whole-chunk boundary, exact return-near and unchanged LOD0.";
        }
        static int VerifyTriangulation()
        {
            const int n=LTSpatialLODMath.N;int count=0;
            foreach(float offset in new[]{0f,901.37f,-123.45f})foreach(bool regular in new[]{false,true})foreach(int parity in new[]{0,1})
            {
                var rect=new Rect(offset,offset,1.7f,2.3f);var cell=new Vector3Int(parity*n/2,0,n/2);
                var plan=new List<Vector3Int>{cell};float H(float x,float z)=>.1f*Mathf.Sin(x*5+z*7);
                var p=new LTRoadTerrainLODProtection();foreach(var _ in p.Prepare(plan,rect,Array.Empty<LTRoadTerrainSurface>(),H,regular:_=>regular)){}
                var points=new Vector3[9];for(int z=0;z<3;z++)for(int x=0;x<3;x++)
                {
                    float gx=cell.x+x*cell.z*.5f,gz=cell.y+z*cell.z*.5f;
                    points[z*3+x]=new Vector3(gx/n*rect.width,H((float)(rect.xMin+(double)gx/n*rect.width),(float)(rect.yMin+(double)gz/n*rect.height)),gz/n*rect.height);
                }
                var union=TriangleSet(points,p.AllTriangles(cell));
                for(int mask=0;mask<16;mask++)
                {
                    LTStampMesh.Emit(rect,new Vector2(1000,1000),1000,null,0,plan,H,null,out var vertices,out _,out _,out var indices,
                        _=>regular,midpoint:(c,side)=>(mask&(1<<side))!=0);
                    var emitted=TriangleSet(vertices,indices);Require(emitted.SetEquals(TriangleSet(points,p.Topology(cell,mask))),"Road clearance topology differs from real emitter.");
                    Require(emitted.IsSubsetOf(union),"Reachable stitch triangle is missing from clearance tests.");count++;
                }
            }
            return count;
        }
        static HashSet<(Vector3,Vector3,Vector3)> TriangleSet(Vector3[] vertices,IReadOnlyList<int> indices)
        {
            var set=new HashSet<(Vector3,Vector3,Vector3)>();
            for(int i=0;i<indices.Count;i+=3)
            {var p=new[]{vertices[indices[i]],vertices[indices[i+1]],vertices[indices[i+2]]}.OrderBy(v=>v.x).ThenBy(v=>v.z).ThenBy(v=>v.y).ToArray();set.Add((p[0],p[1],p[2]));}
            return set;
        }
        static void VerifyFourChunks(LTRoadTerrainSurface surface)
        {
            const int n=LTSpatialLODMath.N,budget=100000,divisions=2;
            var outputs=new LTSpatialLODMath.Output[4];var vertices=new List<Vector3>();var offsets=new int[4];
            for(int c=0;c<4;c++)
            {
                var rect=new Rect(c%2*8,c/2*8,8,8);var fine=new List<Vector3Int>();
                for(int z=0;z<n;z+=n/32)for(int x=0;x<n;x+=n/32)fine.Add(new Vector3Int(x,z,n/32));
                var protection=new LTRoadTerrainLODProtection();foreach(var step in protection.Prepare(fine,rect,new[]{surface},(x,z)=>0)){}
                var levels=new List<Vector3Int>[5];levels[0]=fine;var prep=new LTLODMesh.Preparation(fine,rect,(x,z)=>0);
                for(int l=1;l<5;l++)levels[l]=prep.Coarsen(l,.1f,null,protection.Protects,divisions,false);
                outputs[c]=LTSpatialLODMath.BuildLayout(levels,divisions,budget,p=>0,p=>protection.Protects(new Rect(rect.xMin+p.x/(float)n*8,rect.yMin+p.y/(float)n*8,p.z/(float)n*8,p.z/(float)n*8)));
                LTStampMesh.EmitSpatialVariants(outputs[c],rect,new Vector2(16,16),budget,(x,z)=>0,null);
                offsets[c]=vertices.Count;vertices.AddRange(outputs[c].vertices.Select(p=>p+new Vector3(rect.xMin,0,rect.yMin)));
            }
            var topology=new LTSpatialLODTopology(2,2,divisions,outputs.Select(o=>new LTSpatialLODTopology.Chunk{cells=o.cells,patches=o.patches}).ToArray());
            var buffer=new List<int>();var all=new List<int>();
            for(int pass=0;pass<12;pass++)
            {
                for(int c=0;c<4;c++)for(int p=0;p<4;p++)topology.SetLevel(c,p,pass<5?pass:(p*3+c+pass)%5);
                topology.Update();topology.Validate();all.Clear();
                for(int c=0;c<4;c++){topology.WriteIndices(c,buffer);all.AddRange(buffer.Select(i=>i+offsets[c]));}
                VerifyEdges(vertices.ToArray(),all);
            }
        }
        static void VerifyEdges(Vector3[] vertices,IReadOnlyList<int> indices)
        {
            var edges=new Dictionary<(Vector3,Vector3),int>();
            bool Less(Vector3 a,Vector3 b)=>a.x<b.x||a.x==b.x&&(a.z<b.z||a.z==b.z&&a.y<b.y);
            for(int i=0;i<indices.Count;i+=3)for(int k=0;k<3;k++)
            {var a=vertices[indices[i+k]];var b=vertices[indices[i+(k+1)%3]];var key=Less(a,b)?(a,b):(b,a);edges.TryGetValue(key,out int count);edges[key]=count+1;}
            foreach(var pair in edges)
            {
                var a=pair.Key.Item1;var b=pair.Key.Item2;
                bool outside=a.x==b.x&&(a.x==0||a.x==16)||a.z==b.z&&(a.z==0||a.z==16);
                Require(pair.Value==(outside?1:2),"Open or non-manifold mixed-LOD terrain edge.");
            }
        }
    }
}
