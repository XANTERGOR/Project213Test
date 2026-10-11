using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Immutable, terrain-local damage contours. Shared by mesh generation and paint;
    // neither the camera nor the terrain's triangulation changes a pit's footprint.
    public static class LTRoadGeometryMath
    {
        public sealed class Pit
        {
            readonly Vector2[] contour;
            public int Count=>contour.Length;
            public Vector2 this[int i]=>contour[i];
            public readonly Rect bounds;
            public readonly float depth;
            public readonly float bevelDepth;
            public readonly float asphaltCoverage;
            readonly int[][] parts;
            public bool Concave=>parts!=null;
            public int PartCount=>parts?.Length??1;
            public int PartSize(int part)=>parts==null?Count:parts[part].Length;
            public Vector2 PartPoint(int part,int point)=>this[parts==null?point:parts[part][point]];
            readonly double centreX,centreZ,insetScale;
            public bool Beveled=>bevelDepth>0;
            // Retain double precision until emission in chunk-local coordinates.
            public double InnerX(int i)=>centreX+(this[i].x-centreX)*insetScale;
            public double InnerZ(int i)=>centreZ+(this[i].y-centreZ)*insetScale;
            public Pit(Vector2[] points,float depth,float bevelWidth=0,float bevelDepth=0,bool allowConcave=false,float asphaltCoverage=0)
            {
                if(points==null||points.Length<3||depth<=0||!Finite(depth))throw new ArgumentException("Invalid asphalt pit.");
                if(!Finite(bevelWidth)||!Finite(bevelDepth)||bevelWidth<0||bevelDepth<0)throw new ArgumentException("Invalid asphalt bevel.");
                if(!Finite(asphaltCoverage)||asphaltCoverage<0||asphaltCoverage>1)throw new ArgumentException("Invalid pit paint coverage.");
                contour=(Vector2[])points.Clone();this.depth=depth;
                this.asphaltCoverage=asphaltCoverage;
                bool convex=true;
                float x=float.PositiveInfinity,z=x,xx=float.NegativeInfinity,zz=xx;
                for(int i=0;i<Count;i++)
                {
                    var a=this[i];var b=this[(i+1)%Count];var c=this[(i+2)%Count];
                    if(!Finite(a.x)||!Finite(a.y))throw new ArgumentException("Invalid asphalt contour coordinate.");
                    if(Cross(a,b,c)<=0)convex=false;
                    x=Math.Min(x,a.x);z=Math.Min(z,a.y);xx=Math.Max(xx,a.x);zz=Math.Max(zz,a.y);
                    centreX+=a.x/(double)Count;centreZ+=a.y/(double)Count;
                }
                bounds=Rect.MinMaxRect(x,z,xx,zz);
                if(!convex)
                {
                    if(!allowConcave)throw new ArgumentException("Pit contour must be strictly convex and counterclockwise.");
                    if(bevelWidth>0||bevelDepth>0)throw new ArgumentException("Connected damage currently uses a sharp shallow rim, not a convex bevel.");
                    parts=Decompose(contour);insetScale=1;return;
                }
                double radius=double.PositiveInfinity;
                for(int i=0;i<Count;i++)
                {
                    var a=this[i];var b=this[(i+1)%Count];double dx=(double)b.x-a.x,dz=(double)b.y-a.y;
                    radius=Math.Min(radius,(dx*(centreZ-a.y)-dz*(centreX-a.x))/Math.Sqrt(dx*dx+dz*dz));
                }
                // Never swallow a small pit or introduce a sub-weld sliver.
                double width=Math.Min(bevelWidth,radius*.35);float drop=Math.Min(bevelDepth,depth*.5f);
                this.bevelDepth=width>=.0001&&drop>=.0001?drop:0;
                insetScale=Beveled?1-width/radius:1;
            }
            // Continuous bevel, then a hard drop to the floor. Independent of LOD.
            public float SurfaceDrop(double x,double z)
            {
                if(!Beveled)return depth;
                double fraction=double.PositiveInfinity;
                for(int i=0;i<Count;i++)
                {
                    var a=this[i];var b=this[(i+1)%Count];double dx=(double)b.x-a.x,dz=(double)b.y-a.y;
                    double full=dx*(InnerZ(i)-a.y)-dz*(InnerX(i)-a.x);
                    fraction=Math.Min(fraction,(dx*(z-a.y)-dz*(x-a.x))/full);
                }
                return fraction>=1-1e-9?depth:(float)(Math.Max(0,fraction)*bevelDepth);
            }
            public bool Contains(float x,float z)
            {
                if(x<bounds.xMin||x>bounds.xMax||z<bounds.yMin||z>bounds.yMax)return false;
                var p=new Vector2(x,z);
                if(Concave)
                {
                    for(int k=0;k<PartCount;k++)
                    {
                        bool inside=true;for(int e=0;e<PartSize(k);e++)
                            if(Cross(PartPoint(k,e),PartPoint(k,(e+1)%PartSize(k)),p)<0){inside=false;break;}
                        if(inside)return true;
                    }
                    return false;
                }
                for(int i=0;i<Count;i++)if(Cross(this[i],this[(i+1)%Count],p)<0)return false;
                return true;
            }

            // Editor-time partition of a simple concave footprint. Internal
            // diagonals never become walls; all LODs share these same pieces.
            static int[][] Decompose(Vector2[] p)
            {
                if(p.Length>32)throw new ArgumentException("Connected pit contour exceeds 32 corners.");
                int n=p.Length;double area=0;
                for(int i=1;i+1<n;i++)area+=Cross(p[0],p[i],p[i+1]);
                if(area<=0)throw new ArgumentException("Connected pit must be counterclockwise.");
                for(int i=0;i<n;i++)for(int j=i+1;j<n;j++)
                {
                    if(j==i+1||i==0&&j==n-1)continue;
                    var a=p[i];var b=p[(i+1)%n];var c=p[j];var d=p[(j+1)%n];
                    if(Math.Max(Math.Min(a.x,b.x),Math.Min(c.x,d.x))>Math.Min(Math.Max(a.x,b.x),Math.Max(c.x,d.x))||
                        Math.Max(Math.Min(a.y,b.y),Math.Min(c.y,d.y))>Math.Min(Math.Max(a.y,b.y),Math.Max(c.y,d.y)))continue;
                    if(Cross(a,b,c)*Cross(a,b,d)<=0&&Cross(c,d,a)*Cross(c,d,b)<=0)
                        throw new ArgumentException("Connected pit contour intersects itself.");
                }
                var remaining=new List<int>();for(int i=0;i<n;i++)remaining.Add(i);
                var result=new List<int[]>();
                while(remaining.Count>3)
                {
                    bool found=false;
                    for(int i=0;i<remaining.Count;i++)
                    {
                        int a=remaining[(i+remaining.Count-1)%remaining.Count],b=remaining[i],c=remaining[(i+1)%remaining.Count];
                        if(Cross(p[a],p[b],p[c])<=0)continue;
                        bool occupied=false;
                        foreach(int q in remaining)if(q!=a&&q!=b&&q!=c&&Cross(p[a],p[b],p[q])>=0&&Cross(p[b],p[c],p[q])>=0&&Cross(p[c],p[a],p[q])>=0){occupied=true;break;}
                        if(occupied)continue;
                        result.Add(new[]{a,b,c});remaining.RemoveAt(i);found=true;break;
                    }
                    if(!found)throw new ArgumentException("Cannot triangulate connected pit contour.");
                }
                if(Cross(p[remaining[0]],p[remaining[1]],p[remaining[2]])<=0)throw new ArgumentException("Degenerate connected pit contour.");
                result.Add(remaining.ToArray());
                // Merge adjacent ears wherever their union stays convex. This
                // avoids extending every ear diagonal through a coarse triangle.
                bool merged=true;
                while(merged)
                {
                    merged=false;
                    for(int a=0;a<result.Count&&!merged;a++)for(int b=a+1;b<result.Count&&!merged;b++)
                    {
                        var left=result[a];var right=result[b];
                        for(int e=0;e<left.Length&&!merged;e++)for(int f=0;f<right.Length&&!merged;f++)
                        {
                            if(left[e]!=right[(f+1)%right.Length]||left[(e+1)%left.Length]!=right[f])continue;
                            var ring=new List<int>();for(int k=1;k<=left.Length;k++)ring.Add(left[(e+k)%left.Length]);
                            for(int k=2;k<right.Length;k++)ring.Add(right[(f+k)%right.Length]);
                            bool convex=true;for(int k=0;k<ring.Count;k++)if(Cross(p[ring[k]],p[ring[(k+1)%ring.Count]],p[ring[(k+2)%ring.Count]])<=0){convex=false;break;}
                            if(!convex)continue;
                            result[a]=ring.ToArray();result.RemoveAt(b);merged=true;
                        }
                    }
                }
                return result.ToArray();
            }
        }
        // Fixed-size spatial index also bounds the per-pixel paint query. Lists
        // are private; callers receive references to immutable Pit values only.
        public sealed class Field
        {
            const float Cell=16;
            readonly List<Pit> pits=new List<Pit>();
            readonly Dictionary<long,List<int>> bins=new Dictionary<long,List<int>>();
            Rect bounds;
            public int Count=>pits.Count;
            public Pit this[int i]=>pits[i];
            static int Bin(float x)=>(int)Math.Floor(x/Cell);
            static long Key(int x,int z)=>((long)x<<32)|(uint)z;
            public bool Contains(float x,float z)
            {
                if(!bins.TryGetValue(Key(Bin(x),Bin(z)),out var ids))return false;
                foreach(int id in ids)if(pits[id].Contains(x,z))return true;
                return false;
            }
            public float AsphaltCoverage(float x,float z)
            {
                if(bins.TryGetValue(Key(Bin(x),Bin(z)),out var ids))
                    foreach(int id in ids)if(pits[id].Contains(x,z))return pits[id].asphaltCoverage;
                return 1;
            }
            public void Query(Rect area,List<Pit> result)
            {
                if(pits.Count==0||!Overlap(area,bounds))return;
                int x0=Bin(Math.Max(area.xMin,bounds.xMin)),x1=Bin(Math.Min(area.xMax,bounds.xMax));
                int z0=Bin(Math.Max(area.yMin,bounds.yMin)),z1=Bin(Math.Min(area.yMax,bounds.yMax));
                for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)
                    if(bins.TryGetValue(Key(x,z),out var ids))foreach(int id in ids)
                    {
                        var pit=pits[id];
                        // Emit once at the first shared bin; no per-triangle HashSet.
                        if(x==Math.Max(x0,Bin(pit.bounds.xMin))&&z==Math.Max(z0,Bin(pit.bounds.yMin))&&Overlap(pit.bounds,area))result.Add(pit);
                    }
            }
            bool Add(Pit pit,bool rejectOverlap)
            {
                var candidates=new List<Pit>();Query(pit.bounds,candidates);
                foreach(var other in candidates)if(Intersects(pit,other))
                {
                    if(rejectOverlap)throw new InvalidOperationException("Geometric Asphalt: overlapping damage from different roads is not supported yet. Move the roads or change their damage seed. Previous meshes are preserved.");
                    return false;
                }
                if(pits.Count>=8192)throw new ArgumentException("Geometric Asphalt: more than 8192 pits. Increase damage spacing or split the road.");
                int index=pits.Count;pits.Add(pit);
                bounds=index==0?pit.bounds:Rect.MinMaxRect(Math.Min(bounds.xMin,pit.bounds.xMin),Math.Min(bounds.yMin,pit.bounds.yMin),
                    Math.Max(bounds.xMax,pit.bounds.xMax),Math.Max(bounds.yMax,pit.bounds.yMax));
                for(int z=Bin(pit.bounds.yMin);z<=Bin(pit.bounds.yMax);z++)for(int x=Bin(pit.bounds.xMin);x<=Bin(pit.bounds.xMax);x++)
                {
                    long key=Key(x,z);if(!bins.TryGetValue(key,out var ids))bins[key]=ids=new List<int>();ids.Add(index);
                }
                return true;
            }
            public static Field Combine(IEnumerable<Pit> source)
            {var result=new Field();foreach(var pit in source)result.Add(pit,true);return result;}
            internal bool TryAdd(Pit pit)=>Add(pit,false);
        }
        public static bool Overlap(Rect a,Rect b)=>a.xMin<=b.xMax&&a.xMax>=b.xMin&&a.yMin<=b.yMax&&a.yMax>=b.yMin;
        static bool Finite(float f)=>!float.IsNaN(f)&&!float.IsInfinity(f);
        static double Cross(Vector2 a,Vector2 b,Vector2 c)=>((double)b.x-a.x)*(c.y-a.y)-((double)b.y-a.y)*(c.x-a.x);
        static bool Separated(Pit a,Pit b)
        {
            for(int i=0;i<a.Count;i++)
            {
                bool outside=true;
                for(int j=0;j<b.Count;j++)if(Cross(a[i],a[(i+1)%a.Count],b[j])>0){outside=false;break;}
                if(outside)return true;
            }
            return false;
        }
        public static bool Intersects(Pit a,Pit b)
        {
            if(!Overlap(a.bounds,b.bounds))return false;
            if(!a.Concave&&!b.Concave)return !Separated(a,b)&&!Separated(b,a);
            bool Apart(Pit p,int part,Pit q,int other)
            {
                for(int e=0;e<p.PartSize(part);e++)
                {
                    bool outside=true;for(int i=0;i<q.PartSize(other);i++)
                        if(Cross(p.PartPoint(part,e),p.PartPoint(part,(e+1)%p.PartSize(part)),q.PartPoint(other,i))>0){outside=false;break;}
                    if(outside)return true;
                }
                return false;
            }
            for(int i=0;i<a.PartCount;i++)for(int j=0;j<b.PartCount;j++)if(!Apart(a,i,b,j)&&!Apart(b,j,a,i))return true;
            return false;
        }
        static float Hash(int x,int y,int seed)
        {unchecked{uint h=(uint)((seed*397^x)*397^y);h^=h>>16;h*=0x7feb352d;h^=h>>15;h*=0x846ca68b;h^=h>>16;return(h>>8)*(1f/16777216);}}
        public static Field Build(LTRoadMath.Snapshot road,LTRoadAsphaltDamage damage,float bevelWidth=0,float bevelDepth=0,float shapeVariation=0,bool connected=false,float floorAsphalt=0)
        {
            if(connected)return BuildConnected(road,damage,floorAsphalt);
            var result=new Field();if(damage.strength<=0||damage.depth<=0)return result;
            float spacing=damage.spacing;
            int rows=(int)Math.Ceiling(road.length/spacing),columns=Math.Max(1,(int)Math.Floor(road.width/spacing));
            if((long)rows*(columns+2)>100000)throw new ArgumentException("Geometric Asphalt: damage distribution exceeds 100000 candidates. Increase spacing.");
            for(int row=0;row<rows;row++)
            {
                for(int column=0;column<columns;column++)
                    if(Hash(row,column,damage.seed)<damage.potholes)
                        Add(row,column,(row+.3f+.4f*Hash(row,column,damage.seed+13))*spacing,
                            ((column+.3f+.4f*Hash(row,column,damage.seed+37))/columns-.5f)*road.width,false);
                // Edge chips are open bites into the paved edge, not a second
                // coincident ribbon. Their exposed floor remains terrain.
                for(int side=-1;side<=1;side+=2)if(Hash(row,side*7919,damage.seed)<damage.brokenEdges)
                    Add(row,side*7919,(row+.3f+.4f*Hash(row,side,damage.seed+71))*spacing,side*road.width*.5f,true);
            }
            return result;
            void Add(int row,int column,float distance,float lateral,bool edge)
            {
                float radius=damage.size*.5f*(.75f+.25f*Hash(row,column,damage.seed+113));
                if(!edge)radius=Math.Min(radius,road.width*.4f);
                if(distance<radius+.1f||distance>road.length-radius-.1f||!edge&&Math.Abs(lateral)+radius*(shapeVariation>0?1:.75f)>road.width*.5f)return;
                var samples=road.samples;int lo=0,hi=samples.Count-1;
                while(hi-lo>1){int mid=(lo+hi)/2;if(samples[mid].distance<distance)lo=mid;else hi=mid;}
                var a=samples[lo];var b=samples[hi];float t=(distance-a.distance)/Math.Max(.000001f,b.distance-a.distance);
                var centre=Vector3.Lerp(a.position,b.position,t);var right=Vector3.Lerp(a.right,b.right,t).normalized;
                right.y=0;right=right.normalized;var forward=new Vector3(-right.z,0,right.x);
                float amount=damage.strength*Mathf.Lerp(a.variationStrength,b.variationStrength,t);
                if(amount<=.001f)return;
                radius*=Mathf.Sqrt(amount);if(edge)radius*=.8f;
                centre+=right*lateral;
                var outline=new Vector2[8];
                float size=shapeVariation>0?Mathf.Lerp(1,.5f+.5f*Hash(row,column,damage.seed+157),shapeVariation):1;
                float aspect=shapeVariation>0?Mathf.Lerp(.75f,.4f+.6f*Hash(row,column,damage.seed+179),shapeVariation):.75f;
                double rotation=shapeVariation*(Hash(row,column,damage.seed+191)-.5)*Math.PI;
                for(int i=0;i<outline.Length;i++)
                {
                    double angle=(i+.35*Hash(row,column,unchecked(damage.seed+i*397)))*Math.PI*2/outline.Length;
                    Vector3 p;
                    if(shapeVariation<=0)p=centre+right*((float)Math.Cos(angle)*radius*.75f)+forward*((float)Math.Sin(angle)*radius);
                    else
                    {
                        // An affine ellipse with uneven angular spacing stays convex:
                        // varied length, width, direction and facets without extra edges.
                        double u=Math.Cos(angle)*radius*size*aspect,v=Math.Sin(angle)*radius*size;
                        p=centre+right*(float)(u*Math.Cos(rotation)-v*Math.Sin(rotation))+forward*(float)(u*Math.Sin(rotation)+v*Math.Cos(rotation));
                    }
                    outline[i]=new Vector2(p.x,p.z);
                }
                // right x forward has positive XZ determinant by construction.
                float bevelVariation=.7f+.6f*Hash(row,column,damage.seed+211);
                result.TryAdd(new Pit(outline,damage.depth*amount*(edge?.65f:1),bevelWidth*bevelVariation,bevelDepth));
            }
        }

        static Field BuildConnected(LTRoadMath.Snapshot road,LTRoadAsphaltDamage damage,float floorAsphalt)
        {
            var result=new Field();if(damage.strength<=0||damage.depth<=0)return result;
            int rows=(int)Math.Ceiling(road.length/damage.spacing);
            if((long)rows*4>100000)throw new ArgumentException("Connected damage exceeds 100000 candidates. Increase spacing.");
            for(int row=0;row<rows;row++)for(int lane=0;lane<4;lane++)
            {
                bool edge=lane>=2;float chance=edge?damage.brokenEdges:damage.potholes;
                // Coherent worn stretches alternate with intact road. At 1 the
                // author can still request a continuous run of damage zones.
                float activity=.3f+.7f*Hash(row/3,0,damage.seed+317);
                if(Hash(row,lane,damage.seed+331)>=chance*(chance+(1-chance)*activity))continue;
                float radius=Math.Min(damage.size*.5f,road.width*.4f)*(.75f+.25f*Hash(row,lane,damage.seed+347));
                float distance=(row+.25f+.5f*Hash(row,lane,damage.seed+359))*damage.spacing;
                if(distance<radius+.1f||distance>road.length-radius-.1f||radius<.01f)continue;
                var samples=road.samples;int lo=0,hi=samples.Count-1;
                while(hi-lo>1){int mid=(lo+hi)/2;if(samples[mid].distance<distance)lo=mid;else hi=mid;}
                var a=samples[lo];var b=samples[hi];float t=(distance-a.distance)/Math.Max(.000001f,b.distance-a.distance);
                float amount=damage.strength*Mathf.Lerp(a.variationStrength,b.variationStrength,t);if(amount<=.001f)continue;
                radius*=Mathf.Sqrt(amount);if(edge)radius*=.8f;
                var centre=Vector3.Lerp(a.position,b.position,t);var right=Vector3.Lerp(a.right,b.right,t);right.y=0;right=right.normalized;
                var forward=new Vector3(-right.z,0,right.x);
                float side=(lane%2)==0?-1:1;
                float lateral=side*road.width*(edge?.5f:.2f)+(edge?0:(Hash(row,lane,damage.seed+373)-.5f)*road.width*.08f);
                centre+=right*lateral;
                // A monotone ribbon of three lobes, joined by narrow necks.
                // It is one concave footprint, not overlapping pit meshes.
                var outline=new Vector2[12];
                Vector2 Point(float u,float v){var p=centre+right*(u*radius)+forward*(v*radius);return new Vector2(p.x,p.z);}
                outline[0]=Point(0,-.95f);outline[6]=Point(.04f,.95f);
                for(int i=0;i<5;i++)
                {
                    float v=-.65f+i*.325f,bulge=i%2==0?.48f:.2f;
                    outline[i+1]=Point(bulge*(.75f+.25f*Hash(row,lane,damage.seed+397+i*17)),v);
                    outline[11-i]=Point(-bulge*(.7f+.3f*Hash(row,lane,damage.seed+491+i*23)),v+.035f);
                }
                float depth=damage.depth*amount*(.35f+.65f*Hash(row,lane,damage.seed+613));
                float coverage=edge?0:floorAsphalt*(.55f+.45f*Hash(row,lane,damage.seed+631));
                result.TryAdd(new Pit(outline,depth,allowConcave:true,asphaltCoverage:coverage));
            }
            return result;
        }
    }
}
