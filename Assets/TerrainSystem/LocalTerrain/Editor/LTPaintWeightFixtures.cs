using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Shared numeric fixtures for managed and native tests. No scene/asset access.
    public sealed class LTPaintWeightFixture
    {
        public const int Count=24;
        public LTPaintWeightInput input;
        public LTRoadMath.Snapshot[] roads;
        public struct Buffer<T>:ILTHeightBuffer<T> {public T[] values;public T this[int i]=>values[i];}
        public LTPaintWeightMath.Pixel Scalar(int pixel)=>LTPaintWeightMath.Evaluate(pixel,input.stamps.Length,
            new Buffer<LTPaintWeightMath.Stamp>{values=input.stamps},new Buffer<LTPaintWeightMath.RoadHit>{values=input.hits},new Buffer<float>{values=input.masks},
            new Buffer<Vector2>{values=input.coordinates},new LTPaintTerrainSnapshot.Access{value=input.terrain},input.terrain.terrain);
        public static bool Same(LTPaintWeightMath.Pixel a,LTPaintWeightMath.Pixel b)=>a.first.Equals(b.first)&&a.second.Equals(b.second)&&a.third.Equals(b.third);
        // Rock palettes omit road stamps, while retaining ordered ordinary paint
        // and junction coverage. Exercise their masks without a road hiding them.
        public static LTPaintWeightFixture BuildRock(int fixture,int size=65)
        {
            var f=Build(fixture,size);var stamps=new List<LTPaintWeightMath.Stamp>();
            foreach(var stamp in f.input.stamps)if(stamp.kind!=1)stamps.Add(stamp);
            f.input.stamps=stamps.ToArray();f.input.hits=Array.Empty<LTPaintWeightMath.RoadHit>();
            f.roads=new LTRoadMath.Snapshot[stamps.Count];return f;
        }
        public LTPaintWeightInput PreparedInput(LTPaintWeightInputPool pool=null)
        {
            var original=input.terrain;var layout=original.terrain;
            var sources=new LTPaintWeightTerrainCache.Source[original.tiles.Length];
            for(int i=0;i<sources.Length;i++)
            {
                var t=original.tiles[i];if(t.valid==0)continue;
                // Use the production bin builder, not the fixture's brute-force
                // bins. ReferenceSurface continues to scan the original faces.
                var normals=t.hasNormals!=0?original.normals:Array.Empty<Vector3>();
                var source=LTPaintWeightTerrainCache.BuildSource(t.rect,original.vertices,normals,original.indices,null,out _,out _);
                sources[i]=LTPaintWeightTerrainCache.BuildSource(t.rect,(Vector3[])original.vertices.Clone(),(Vector3[])normals.Clone(),
                    (int[])original.indices.Clone(),source,out bool binsReused,out bool surfaceReused);
                if(!binsReused||!surfaceReused||!ReferenceEquals(source,sources[i]))
                    throw new InvalidOperationException("Identical terrain source did not reuse its identity.");
            }
            var cache=new LTPaintWeightTerrainCache();var range=new RectInt(0,0,layout.countX,layout.countZ);
            LTPaintWeightTerrainCache.Source Lookup(int x,int z)=>sources[z*layout.countX+x];
            if(!cache.TryGet(layout,range,Lookup,out var packed,out bool reused)||reused||
                !cache.TryGet(layout,range,Lookup,out var repeated,out reused)||!reused||!ReferenceEquals(packed,repeated))
                throw new InvalidOperationException("Paint preparation cache fixture failed.");
            cache.Clear(); // outstanding input remains valid after eviction
            if(pool==null)return new LTPaintWeightInput{rect=input.rect,size=input.size,stamps=input.stamps,hits=input.hits,
                masks=input.masks,coordinates=input.coordinates,terrain=packed};
            var lease=pool.Rent(input.stamps.Length,input.hits.Length,input.masks.Length,input.coordinates.Length,out _,out _,out _);
            try
            {
                lease.rect=input.rect;lease.size=input.size;lease.terrain=packed;
                Array.Copy(input.stamps,lease.stamps,input.stamps.Length);Array.Copy(input.hits,lease.hits,input.hits.Length);
                Array.Copy(input.masks,lease.masks,input.masks.Length);Array.Copy(input.coordinates,lease.coordinates,input.coordinates.Length);
                return lease;
            }
            catch{lease.Dispose();throw;}
        }
        public void OverwriteReturnedBuffers(LTPaintWeightInputPool pool)
        {
            using(var lease=pool.Rent(input.stamps.Length,input.hits.Length,input.masks.Length,input.coordinates.Length,out _,out int reused,out _))
            {
                if(reused<2)throw new InvalidOperationException("Paint input reuse was not exercised.");
                Array.Clear(lease.stamps,0,lease.stamps.Length);Array.Clear(lease.hits,0,lease.hits.Length);
                for(int i=0;i<lease.masks.Length;i++)lease.masks[i]=.12345f;
                for(int i=0;i<lease.coordinates.Length;i++)lease.coordinates[i]=new Vector2(123456,-654321);
            }
        }
        // Original managed paint equations, not the worker kernel. Reference road
        // projections and PixelTerrainCache are deliberately evaluated independently.
        public LTPaintWeightMath.Pixel Reference(int pixel)
        {
            var v=input;int size=v.size;
            var point=new Vector3(v.rect.xMin+v.rect.width*(pixel%size)/(size-1),0,v.rect.yMin+v.rect.height*(pixel/size)/(size-1));
            var weights=new float[12];weights[0]=1;
            var cache=new LTPaintMath.PixelTerrainCache(ReferenceSurface,v.terrain.terrain.sizeX,v.terrain.terrain.sizeZ,v.stamps.Length);cache.Begin(point.x,point.z);
            for(int i=0;i<v.stamps.Length;i++)
            {
                var s=v.stamps[i];
                if(s.kind==1)
                {
                    roads[i].PaintWeights(point.x,point.z,out float g,out float w);
                    if(s.slot>=0)LTPaintMath.Composite(weights,s.slot,g);
                    if(s.wheelSlot>=0)LTPaintMath.Composite(weights,s.wheelSlot,w);continue;
                }
                if(s.kind==2)
                {
                    var j=new LTRoadJunctionMath.Snapshot{centre=s.centre,coreRadius=s.coreRadius,blend=s.blend};
                    LTPaintMath.Composite(weights,s.slot,j.Weight(point.x,point.z));continue;
                }
                var local=s.inverse.MultiplyPoint3x4(point);
                var p=new Vector2(local.x/Mathf.Max(.01f,s.size.x)*2,local.z/Mathf.Max(.01f,s.size.y)*2);
                float alpha=LTPaintMath.Coverage(p,s.rectangle!=0,s.falloff,s.strength);
                if(alpha<=0)continue;
                if(s.maskOffset>=0)alpha*=v.masks[s.maskOffset+pixel];
                if(s.noiseEnabled!=0)alpha*=LTPaintMath.NoiseCoverage(new Vector2(local.x,local.z),s.noiseSize,s.noiseSeed,s.noiseStrength,s.noiseThreshold,s.noiseSoftness);
                if(alpha<=0)continue;
                if(s.height.enabled!=0||s.slope.enabled!=0||s.curve.enabled!=0)
                {
                    if(!cache.Surface(out float h,out float slope))continue;
                    alpha*=s.height.Evaluate(h)*s.slope.Evaluate(slope);
                    if(alpha>0&&s.curve.enabled!=0)
                    {if(!cache.Curvature(s.curveRadius,out float curve))continue;alpha*=s.curve.Evaluate(curve);}
                }
                LTPaintMath.Composite(weights,s.slot,alpha);
            }
            return new LTPaintWeightMath.Pixel{first=new Color(weights[0],weights[1],weights[2],weights[3]),
                second=new Color(weights[4],weights[5],weights[6],weights[7]),third=new Color(weights[8],weights[9],weights[10],weights[11])};
        }
        bool ReferenceSurface(float x,float z,out float height,out float slope)
        {
            // Brute-force the chosen tile independently of the worker's bins.
            var snap=input.terrain;var t=snap.terrain;x=Mathf.Clamp(x,0,t.sizeX);z=Mathf.Clamp(z,0,t.sizeZ);height=slope=0;
            int key=Mathf.Clamp(Mathf.FloorToInt(z/t.depth),0,t.countZ-1)*t.countX+Mathf.Clamp(Mathf.FloorToInt(x/t.width),0,t.countX-1);
            var tile=snap.tiles[key];if(tile.valid==0)return false;
            bool found=false;float top=float.NegativeInfinity;Vector3 normal=Vector3.up;
            var bin=snap.bins[tile.firstBin]; // Fixtures deliberately put all tile faces in every bin.
            for(int i=0;i<bin.y;i++)
            {
                int at=snap.triangles[bin.x+i],ia=snap.indices[at],ib=snap.indices[at+1],ic=snap.indices[at+2];
                var a=snap.vertices[ia];var b=snap.vertices[ib];var c=snap.vertices[ic];
                if(!LTPaintMath.TriangleWeights(new Vector2(x,z),a,b,c,out var w))continue;
                float y=w.x*a.y+w.y*b.y+w.z*c.y;if(y<top)continue;top=y;found=true;
                normal=tile.hasNormals!=0?(w.x*snap.normals[ia]+w.y*snap.normals[ib]+w.z*snap.normals[ic]).normalized:Vector3.Cross(b-a,c-a).normalized;
            }
            if(!found)return false;height=top;slope=Mathf.Acos(Mathf.Clamp(Mathf.Abs(normal.y),0,1))*Mathf.Rad2Deg;return true;
        }
        public static LTPaintWeightFixture Build(int fixture,int size=65)
        {
            float shift=fixture%3==0?100000:fixture%3==1?-512:0;
            var rect=new Rect(shift,shift,32,32);
            var terrain=new LTPaintTerrainSnapshot{terrain=new LTPaintWeightMath.Terrain{sizeX=Math.Max(64,shift+64),sizeZ=Math.Max(64,shift+64),width=Math.Max(64,shift+64)/2,depth=Math.Max(64,shift+64)/2,countX=2,countZ=2}};
            var vertices=new List<Vector3>();var normals=new List<Vector3>();var indices=new List<int>();
            for(int z=0;z<5;z++)for(int x=0;x<5;x++)
            {
                float px=shift+x*8,pz=shift+z*8;
                vertices.Add(new Vector3(px,.12f*x*x-.23f*z+.2f*(float)Math.Sin(x*z),pz));normals.Add(new Vector3(-.1f*x,1,.03f*z).normalized);
            }
            for(int z=0;z<4;z++)for(int x=0;x<4;x++)
            {if(fixture%4==0&&x==2&&z==2)continue;int a=z*5+x;indices.AddRange(new[]{a,a+5,a+1,a+1,a+5,a+6});}
            terrain.vertices=vertices.ToArray();terrain.normals=normals.ToArray();terrain.indices=indices.ToArray();
            var triangles=new int[indices.Count/3];for(int i=0;i<triangles.Length;i++)triangles[i]=i*3;terrain.triangles=triangles;
            terrain.tiles=new LTPaintWeightMath.Tile[4];terrain.bins=new Vector2Int[4*16*16];
            for(int t=0;t<4;t++)
            {
                terrain.tiles[t]=new LTPaintWeightMath.Tile{valid=fixture%5==0&&t==1?0:1,rect=new Rect(t%2*terrain.terrain.width,t/2*terrain.terrain.depth,terrain.terrain.width,terrain.terrain.depth),axis=16,firstBin=t*256,hasNormals=fixture%2};
                for(int b=0;b<256;b++)terrain.bins[t*256+b]=new Vector2Int(0,triangles.Length);
            }
            var stamps=new List<LTPaintWeightMath.Stamp>();var sources=new List<LTRoadMath.Snapshot>();var hits=new List<LTPaintWeightMath.RoadHit>();
            int pixels=size*size;var masks=new float[pixels];for(int p=0;p<pixels;p++)masks[p]=((p*17)%251)/250f;
            for(int i=0;i<14;i++)
            {
                var s=new LTPaintWeightMath.Stamp{slot=(i*7)%12,wheelSlot=-1,maskOffset=-1};LTRoadMath.Snapshot road=null;
                if(i==3||i==9)
                {
                    var settings=LTRoadMath.Settings.Default;settings.mode=fixture%4==0?LTRoadMode.Asphalt:LTRoadMode.Offroad;
                    settings.pattern=fixture%2==0?LTRoadPattern.Tracks:LTRoadPattern.Solid;
                    settings.wheelTracks.enabled=true;settings.wheelLayerId=21;settings.variation.enabled=fixture%3!=0;
                    settings.straightStart=fixture%2==0;settings.straightEnd=fixture%3==0;settings.junctionStartLength=3;settings.junctionEndLength=4;
                    var points=new[]{new LTRoadPoint(new Vector3(shift+8+i,0,shift)),new LTRoadPoint(new Vector3(shift+4+i,1,shift+15)),new LTRoadPoint(new Vector3(shift+9+i,2,shift+32))};
                    road=LTRoadMath.Build(points,Matrix4x4.identity,settings);s.kind=1;s.wheelSlot=(s.slot+1)%12;s.hitOffset=hits.Count;s.road=road.PaintKernelData();
                    for(int p=0;p<pixels;p++)
                    {float x=rect.xMin+rect.width*(p%size)/(size-1),z=rect.yMin+rect.height*(p/size)/(size-1);road.TrySample(x,z,out var hit);hits.Add(LTPaintWeightMath.RoadHit.From(hit));}
                }
                else if(i==6){s.kind=2;s.centre=new Vector3(shift+16,0,shift+10);s.coreRadius=4;s.blend=3;}
                else
                {
                    s.inverse=Matrix4x4.identity;s.inverse.m03=-shift-16;s.inverse.m23=-shift-16;
                    s.inverse.m00=.9f;s.inverse.m02=.13f;s.inverse.m03=-shift*1.03f-16;
                    s.size=new Vector2(40+i*2,39);s.rectangle=i%2;s.strength=.24f+i*.03f;s.falloff=.43f;
                    s.noiseEnabled=1;s.noiseSize=2+i;s.noiseSeed=fixture*41+i;s.noiseStrength=.85f;s.noiseThreshold=.45f;s.noiseSoftness=.35f;
                    if(i==0)s.maskOffset=0;
                    if(i%2==0)s.height=new LTPaintWeightMath.Filter{enabled=1,range=new Vector2(-.3f,2),feather=.4f};
                    if(i%3==0)s.slope=new LTPaintWeightMath.Filter{enabled=1,range=new Vector2(0,10),feather=5};
                    if(i%3==1){s.curve=new LTPaintWeightMath.Filter{enabled=1,range=new Vector2(-1,.08f),feather=.1f};s.curveRadius=i%2==0?3:5;}
                }
                stamps.Add(s);sources.Add(road);
            }
            if(fixture>=12){stamps.Reverse();sources.Reverse();}
            var coordinates=new List<Vector2>();
            for(int p=0;p<pixels;p++)coordinates.Add(new Vector2(rect.xMin+rect.width*(p%size)/(size-1),rect.yMin+rect.height*(p/size)/(size-1)));
            for(int i=0;i<stamps.Count;i++)if(stamps[i].kind==0)
            {
                var stamp=stamps[i];stamp.localOffset=coordinates.Count;stamps[i]=stamp;
                for(int p=0;p<pixels;p++){var xy=coordinates[p];var local=stamp.inverse.MultiplyPoint3x4(new Vector3(xy.x,0,xy.y));coordinates.Add(new Vector2(local.x,local.z));}
            }
            return new LTPaintWeightFixture{input=new LTPaintWeightInput{rect=rect,size=size,stamps=stamps.ToArray(),hits=hits.ToArray(),masks=masks,coordinates=coordinates.ToArray(),terrain=terrain},roads=sources.ToArray()};
        }
    }
}
