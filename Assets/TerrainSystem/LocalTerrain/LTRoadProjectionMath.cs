using System;
using System.Collections.Generic;
using UnityEngine;
namespace LocalTerrainPrototype
{
    // Shared-slot atlas. Ownership is encoded in orientation length (1..N), never
    // in UV. At an ownership seam use a local affine frame, not interpolation of
    // unrelated longitudinal coordinates. Single-owner cells retain old bilinear UVs.
    public static class LTRoadProjectionMath
    {
        public static bool SameInputs(LTRoadMath.Snapshot a,LTRoadMath.Snapshot b,bool sharedLayer)
            =>a==null?b==null:b!=null&&a.projectionHash==b.projectionHash&&(!sharedLayer||a.paintHash==b.paintHash);
        public sealed class Map { public Color[] coordinates,derivatives; public int queries; }
        public static int Owner(Color p)=>(int)Math.Round(Math.Sqrt((double)p.b*p.b+(double)p.a*p.a));
        public static Map Bake(IReadOnlyList<LTRoadMath.Snapshot> roads,Rect rect,int size)
        {
            if(roads==null||roads.Count<1||roads.Count>1024||size<2||rect.width<=0||rect.height<=0)throw new ArgumentException("Invalid shared road projection layout.");
            int count=size*size;var result=new Map{coordinates=new Color[count],derivatives=roads.Count>1?new Color[count]:null};
            var scores=roads.Count>1?new float[count]:null;
            if(scores!=null)for(int i=0;i<count;i++)scores[i]=float.NegativeInfinity;
            for(int owner=0;owner<roads.Count;owner++)
            {
                var road=roads[owner];var region=road.TextureBakeRegion(rect,size);int blockSize=LTRoadMath.TextureBakeBlockSize;
                for(int bz=region.yMin;bz<region.yMax;bz+=blockSize)for(int bx=region.xMin;bx<region.xMax;bx+=blockSize)
                {
                    var block=new RectInt(bx,bz,Math.Min(blockSize,region.xMax-bx),Math.Min(blockSize,region.yMax-bz));
                    if(!road.TextureBakeBlockIntersects(rect,size,block))continue;
                    for(int z=block.yMin;z<block.yMax;z++)for(int x=block.xMin;x<block.xMax;x++)
                    {
                        float px=rect.xMin+rect.width*x/(size-1),pz=rect.yMin+rect.height*z/(size-1);int at=z*size+x;
                        if(scores!=null)
                        {
                            road.TrySample(px,pz,out var hit);result.queries++;
                            // Coverage first, distance second; last hierarchy contributor
                            // wins exact ties. Stable across frames and neighbouring chunks.
                            float score=road.PaintWeight(px,pz)*10000-Math.Min(9999,hit.radialDistance/Math.Max(.01f,road.width));
                            if(score<scores[at])continue;scores[at]=score;
                        }
                        if(!road.TryTextureCoordinates(px,pz,out var uv,out var right))throw new InvalidOperationException("Invalid shared road UV.");
                        result.queries++;right*=owner+1;result.coordinates[at]=new Color(uv.x,uv.y,right.x,right.y);
                    }
                }
            }
            if(result.derivatives!=null)
            {
                var needed=new bool[count];
                for(int z=0;z<size-1;z++)for(int x=0;x<size-1;x++)
                {
                    int a=z*size+x;int owner=Owner(result.coordinates[a]);
                    if(owner==Owner(result.coordinates[a+1])&&owner==Owner(result.coordinates[a+size])&&owner==Owner(result.coordinates[a+size+1]))continue;
                    needed[a]=needed[a+1]=needed[a+size]=needed[a+size+1]=true;
                }
                for(int at=0;at<count;at++)if(needed[at])
                {
                    int owner=Owner(result.coordinates[at])-1;if(owner<0)continue;var road=roads[owner];
                    float px=rect.xMin+rect.width*(at%size)/(size-1),pz=rect.yMin+rect.height*(at/size)/(size-1);
                    float h=Math.Max(.001f,Math.Min(rect.width,rect.height)/(size-1)*.05f);
                    road.TryTextureCoordinates(px+h,pz,out var xp,out _);road.TryTextureCoordinates(px-h,pz,out var xm,out _);
                    road.TryTextureCoordinates(px,pz+h,out var zp,out _);road.TryTextureCoordinates(px,pz-h,out var zm,out _);
                    var dx=(xp-xm)/(2*h);var dz=(zp-zm)/(2*h);
                    result.derivatives[at]=new Color(dx.x,dx.y,dz.x,dz.y);result.queries+=4;
                }
            }
            return result;
        }
        public static Color Sample(Color[] map,Color[] derivatives,Rect rect,int size,float x,float z,out Vector2 dx,out Vector2 dz)
        {
            float px=Mathf.Clamp01((x-rect.xMin)/rect.width)*(size-1),pz=Mathf.Clamp01((z-rect.yMin)/rect.height)*(size-1);
            int ix=Math.Min((int)px,size-2),iz=Math.Min((int)pz,size-2),at=iz*size+ix;float fx=px-ix,fz=pz-iz;
            Color a=map[at],b=map[at+1],c=map[at+size],d=map[at+size+1];
            if(derivatives!=null&&(Owner(a)!=Owner(b)||Owner(a)!=Owner(c)||Owner(a)!=Owner(d)))
            {
                int ox=fx>=.5f?1:0,oz=fz>=.5f?1:0;int chosen=at+ox+oz*size;
                // Prefer valid guard data to an empty corner at the finite ROI edge.
                if(Owner(map[chosen])==0)
                {
                    float best=-1;
                    for(int j=0;j<2;j++)for(int i=0;i<2;i++)
                    {int n=at+i+j*size;float weight=(i==0?1-fx:fx)*(j==0?1-fz:fz);if(Owner(map[n])>0&&weight>best){best=weight;chosen=n;ox=i;oz=j;}}
                }
                var frame=derivatives[chosen];dx=new Vector2(frame.r,frame.g);dz=new Vector2(frame.b,frame.a);
                var p=map[chosen];var uv=new Vector2(p.r,p.g)+dx*((fx-ox)*rect.width/(size-1))+dz*((fz-oz)*rect.height/(size-1));
                return new Color(uv.x,uv.y,p.b,p.a);
            }
            dx=Vector2.LerpUnclamped(new Vector2(b.r-a.r,b.g-a.g),new Vector2(d.r-c.r,d.g-c.g),fz)*((size-1)/rect.width);
            dz=Vector2.LerpUnclamped(new Vector2(c.r-a.r,c.g-a.g),new Vector2(d.r-b.r,d.g-b.g),fx)*((size-1)/rect.height);
            return Color.LerpUnclamped(Color.LerpUnclamped(a,b,fx),Color.LerpUnclamped(c,d,fx),fz);
        }
    }
}
