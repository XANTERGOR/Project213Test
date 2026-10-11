using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTRoadJunctionTerrainLODData
    {
        // Concave T footprint; the upper arm has a real 2x2 hole. No overlap
        // between rectangles, so the welded edge map sees only the real outline.
        internal static LTRoadTerrainSurface.Part Shape(float height,int level=0)
        {
            var v=new List<Vector3>();var t=new List<int>();
            void Quad(float x0,float z0,float x1,float z1)
            {
                int i=v.Count;v.AddRange(new[]{new Vector3(x0,height,z0),new Vector3(x0,height,z1),
                    new Vector3(x1,height,z1),new Vector3(x1,height,z0)});
                t.AddRange(new[]{i,i+1,i+2,i+2,i+3,i});
            }
            Quad(6,0,10,8);Quad(2,8,14,10);Quad(2,10,7,12);Quad(9,10,14,12);Quad(2,12,14,14);
            return new LTRoadTerrainSurface.Part{vertices=v.ToArray(),indices=t.ToArray(),level=level};
        }
        internal static string Run()
        {
            int checks=0;void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks++;}
            const int n=LTSpatialLODMath.N;
            var rect=new Rect(0,0,16,16);var fine=new List<Vector3Int>();
            for(int z=0;z<n;z+=n/64)for(int x=0;x<n;x+=n/64)fine.Add(new Vector3Int(x,z,n/64));
            var original=fine.ToArray();var top=Shape(.06f);var middle=Shape(.04f,1);var far=Shape(.02f,2);
            var surface=new LTRoadTerrainSurface(new[]{top,middle,far});
            LTRoadTerrainLODProtection Protect(LTRoadTerrainSurface s,Func<float,float,float> h)
            {var p=new LTRoadTerrainLODProtection();foreach(var _ in p.Prepare(fine,rect,new[]{s},h)){}return p;}
            var protection=Protect(surface,(x,z)=>0);
            Check(surface.CrossesEdge(new Rect(6.9f,10.5f,.2f,.5f)),"Junction hole rim missing.");
            Check(!surface.Blocks(new Rect(7.5f,10.5f,1,1),10,0,0,0),"Junction hole treated as solid asphalt.");
            Check(!surface.Blocks(new Rect(2,2,1,1),10,0,0,0),"Concave empty quadrant treated as asphalt.");
            foreach(var area in new[]{new Rect(7,3,1,1),new Rect(3,9,1,1),new Rect(6.5f,10.5f,1,1),new Rect(5.5f,3,1,1)})
                Check(!protection.Protects(area),"Flat junction interior/edge/hole cannot merge.");
            var low=Shape(-.02f,2);var lowSurface=new LTRoadTerrainSurface(new[]{top,middle,low});
            Check(Protect(lowSurface,(x,z)=>0).Protects(new Rect(7,3,1,1)),"Lower junction LOD clearance ignored.");
            float Bump(float x,float z)=>x==7&&z==11?.2f:0;
            // Give this error-only fixture ample asphalt clearance; increasing
            // edge error must NEVER override the independent penetration guard.
            var raised=Shape(.5f);
            Check(Protect(new LTRoadTerrainSurface(new[]{raised}),Bump).Protects(new Rect(6.5f,10.5f,1,1)),"Junction hole edge error ignored.");
            Check(!Protect(new LTRoadTerrainSurface(new[]{raised},.3f),Bump).Protects(new Rect(6.5f,10.5f,1,1)),"Junction edge error setting ignored.");
            Check(Protect(new LTRoadTerrainSurface(new[]{top},.3f),Bump).Protects(new Rect(6.5f,10.5f,1,1)),"Edge error overrode penetration guard.");
            var tilted=Shape(.06f);for(int i=0;i<tilted.vertices.Length;i++){var p=tilted.vertices[i];p.y+=p.x*.1f+p.z*.2f;tilted.vertices[i]=p;}
            Check(!Protect(new LTRoadTerrainSurface(new[]{tilted}),(x,z)=>x*.1f+z*.2f).Protects(new Rect(7,3,1,1)),"Sloped junction cannot merge.");
            var prep=new LTLODMesh.Preparation(fine,rect,(x,z)=>0);var levels=new List<Vector3Int>[5];levels[0]=fine;
            for(int l=1;l<5;l++)levels[l]=prep.Coarsen(l,.1f,null,protection.Protects,4,false);
            Check(levels[4].Count<fine.Count/4,"Junction still blanket locked.");
            var whole=prep.Coarsen(4,.1f,null,protection.Protects);LTLODMesh.ValidateBoundary(fine,whole);
            Check(whole.Count<fine.Count,"Whole-chunk junction cannot simplify.");
            var output=LTSpatialLODMath.BuildLayout(levels,4,100000,p=>0,p=>protection.Protects(
                new Rect(p.x/(float)n*16,p.y/(float)n*16,p.z/(float)n*16,p.z/(float)n*16)));
            LTStampMesh.EmitSpatialVariants(output,rect,new Vector2(16,16),100000,(x,z)=>0,null);
            var topology=new LTSpatialLODTopology(1,1,4,new[]{new LTSpatialLODTopology.Chunk{cells=output.cells,patches=output.patches}});
            var buffer=new List<int>();int[] near=null;int farTris=0;
            for(int pass=0;pass<7;pass++)
            {
                for(int p=0;p<16;p++)topology.SetLevel(0,p,pass<5?pass:pass==5?p%5:0);
                topology.Update();topology.Validate();topology.WriteIndices(0,buffer);
                if(pass==0)near=buffer.ToArray();if(pass==4)farTris=buffer.Count/3;
                if(pass==6)Check(near.SequenceEqual(buffer),"Junction return-near changed LOD0 indices.");
            }
            Check(fine.SequenceEqual(original),"Junction modified LOD0/collider plan.");
            return $"PASS {checks} junction data checks; T + hole, all LOD clearance, edge error, slope, whole/spatial LOD, mixed levels and return-near; cells {fine.Count}->{levels[4].Count}, triangles {near.Length/3}->{farTris}. Synthetic fixture, not a live-scene performance measurement.";
        }
    }
}
