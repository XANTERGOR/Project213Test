using UnityEngine;
namespace LocalTerrainPrototype
{
    public static class LTPaintMath
    {
        // A bake owns this cache. Values are reused only within the same pixel and
        // effective radius; failures are cached too (holes/absent terrain).
        public delegate bool HeightSampler(float x,float z,out float height,out float slope);
        public sealed class PixelTerrainCache
        {
            readonly HeightSampler sample;
            readonly float sizeX,sizeZ;
            readonly float[] radii,values;
            readonly bool[] valid;
            int count;
            float x,z,height,slope;
            bool sampled,found;
            public PixelTerrainCache(HeightSampler sample,float sizeX,float sizeZ,int capacity)
            {this.sample=sample;this.sizeX=sizeX;this.sizeZ=sizeZ;radii=new float[capacity];values=new float[capacity];valid=new bool[capacity];}
            public void Begin(float px,float pz){x=px;z=pz;count=0;sampled=false;}
            public bool Surface(out float h,out float s)
            {
                if(!sampled){found=sample(x,z,out height,out slope);sampled=true;}
                h=height;s=slope;return found;
            }
            public bool Curvature(float radius,out float value)
            {
                radius=Mathf.Max(.1f,radius);
                radius=Mathf.Min(radius,Mathf.Min(Mathf.Min(x,sizeX-x),Mathf.Min(z,sizeZ-z)));
                for(int i=0;i<count;i++)if(radii[i]==radius){value=values[i];return valid[i];}
                value=0;bool ok=Surface(out float center,out _);
                if(ok&&radius>=.1f)
                {
                    float left=0,right=0,back=0,front=0;
                    ok=sample(x-radius,z,out left,out _)&&sample(x+radius,z,out right,out _)&&
                        sample(x,z-radius,out back,out _)&&sample(x,z+radius,out front,out _);
                    if(ok)value=LTPaintMath.Curvature(center,left,right,back,front,radius);
                }
                if(count<radii.Length){radii[count]=radius;values[count]=value;valid[count]=ok;count++;}
                return ok;
            }
        }

        // Bounded adaptive bins: dense road tiles need finer lookup than empty
        // terrain tiles. The triangle order in each bin remains unchanged.
        public static int TerrainBinAxis(int triangleCount)
            =>Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(Mathf.Max(1,triangleCount)/8f))),16,128);
        public static int TerrainBin(float position,float min,float size,int axis)
            =>Mathf.Clamp(Mathf.FloorToInt((position-min)/size*axis),0,axis-1);
        public static RectInt TerrainTriangleBins(Rect rect,Vector3 a,Vector3 b,Vector3 c,int axis)
        {
            float x0=Mathf.Min(a.x,Mathf.Min(b.x,c.x)),x1=Mathf.Max(a.x,Mathf.Max(b.x,c.x));
            float z0=Mathf.Min(a.z,Mathf.Min(b.z,c.z)),z1=Mathf.Max(a.z,Mathf.Max(b.z,c.z));
            // TriangleWeights accepts a small negative barycentric coordinate.
            // Retain that support across NEW bin edges, but preserve the previous
            // 16x16 lookup footprint at old edges (including its tie ordering).
            float px=(x1-x0)*.00002f+.00001f,pz=(z1-z0)*.00002f+.00001f;
            int factor=axis/16;
            int loX=Mathf.Max(TerrainBin(x0-px,rect.xMin,rect.width,axis),TerrainBin(x0,rect.xMin,rect.width,16)*factor);
            int hiX=Mathf.Min(TerrainBin(x1+px,rect.xMin,rect.width,axis),(TerrainBin(x1,rect.xMin,rect.width,16)+1)*factor-1);
            int loZ=Mathf.Max(TerrainBin(z0-pz,rect.yMin,rect.height,axis),TerrainBin(z0,rect.yMin,rect.height,16)*factor);
            int hiZ=Mathf.Min(TerrainBin(z1+pz,rect.yMin,rect.height,axis),(TerrainBin(z1,rect.yMin,rect.height,16)+1)*factor-1);
            return new RectInt(loX,loZ,hiX-loX+1,hiZ-loZ+1);
        }

        public struct SeamContribution {public Vector3Int key;public Vector3 normal;}
        public static SeamContribution[] TerrainSeamContributions(Rect rect,Vector3[] points,int[] triangles)
        {
            var border=new bool[points.Length];
            for(int i=0;i<points.Length;i++)
            {
                var p=points[i];border[i]=Mathf.Abs(p.x-rect.xMin)<=.0001f||Mathf.Abs(p.x-rect.xMax)<=.0001f||
                    Mathf.Abs(p.z-rect.yMin)<=.0001f||Mathf.Abs(p.z-rect.yMax)<=.0001f;
            }
            var result=new System.Collections.Generic.List<SeamContribution>();
            for(int i=0;i+2<triangles.Length;i+=3)
            {
                int a=triangles[i],b=triangles[i+1],c=triangles[i+2];
                if(!border[a]&&!border[b]&&!border[c])continue;
                var normal=Vector3.Cross(points[b]-points[a],points[c]-points[a]);
                for(int k=0;k<3;k++)
                {
                    int vertex=triangles[i+k];if(!border[vertex])continue;var p=points[vertex];
                    result.Add(new SeamContribution{key=new Vector3Int(Mathf.RoundToInt(p.x*1000),Mathf.RoundToInt(p.y*1000),Mathf.RoundToInt(p.z*1000)),normal=normal});
                }
            }
            return result.ToArray();
        }
        public static System.Collections.Generic.HashSet<int> TerrainNeighbours(System.Collections.Generic.IEnumerable<int> ids,int countX,int countZ)
        {
            var result=new System.Collections.Generic.HashSet<int>();
            foreach(int id in ids)
                for(int z=Mathf.Max(0,id/countX-1);z<=Mathf.Min(countZ-1,id/countX+1);z++)
                for(int x=Mathf.Max(0,id%countX-1);x<=Mathf.Min(countX-1,id%countX+1);x++)result.Add(z*countX+x);
            return result;
        }

        // Authoring revisions are separate from generated mesh revisions. A density
        // refinement must not reset its own conservative coverage union. Old AND new
        // stamp footprints are touched by the editor, including on removal and Undo.
        public sealed class TerrainAuthoringRevisions
        {
            int[] tiles=System.Array.Empty<int>();
            int countX,countZ,epoch,revision;
            float width,depth;
            string config;
            public void Configure(int x,int z,float tileWidth,float tileDepth,string key)
            {
                if(x==countX&&z==countZ&&tileWidth==width&&tileDepth==depth&&config==key)return;
                countX=x;countZ=z;width=tileWidth;depth=tileDepth;config=key;
                tiles=new int[System.Math.Max(0,x)*System.Math.Max(0,z)];
                unchecked{epoch++;}revision=0;
            }
            public void Touch(int id)
            {
                if(id<0||id>=tiles.Length)throw new System.ArgumentOutOfRangeException(nameof(id));
                unchecked{tiles[id]=++revision;}
            }
            public int RegionSignature(Rect area,float radius)
            {
                var range=TerrainSampleTileRange(area,radius,width,depth,countX,countZ);
                int hash=epoch;
                unchecked
                {
                    for(int z=range.yMin;z<range.yMax;z++)for(int x=range.xMin;x<range.xMax;x++)
                        hash=hash*397^tiles[z*countX+x];
                }
                return hash;
            }
        }

        // Streaming content key for non-adversarial geometry caches. Two independent
        // 64-bit accumulators; no per-number strings, buffers, native calls or boxing.
        // Not a cryptographic hash. A new instance with the same words gives the same key.
        public struct GeometryFingerprint
        {
            ulong a,b,count;
            public void Add(int value)
            {
                unchecked
                {
                    if(count==0){a=14695981039346656037UL;b=0x9e3779b185ebca87UL;}
                    uint word=(uint)value;
                    a=(a^word)*1099511628211UL;
                    b^=word+0x9e3779b97f4a7c15UL;
                    b=((b<<27)|(b>>37))*0xc2b2ae3d27d4eb4fUL+0x165667b19e3779f9UL;
                    count++;
                }
            }
            public void Add(float value)=>Add(new FloatBits{value=value}.bits);
            public void Add(Vector3 value){Add(value.x);Add(value.y);Add(value.z);}
            static ulong Finish(ulong value)
            {
                unchecked
                {
                    value^=value>>33;value*=0xff51afd7ed558ccdUL;
                    value^=value>>33;value*=0xc4ceb9fe1a85ec53UL;return value^(value>>33);
                }
            }
            public override string ToString()=>"g2:"+
                Finish(a^count).ToString("x16",System.Globalization.CultureInfo.InvariantCulture)+
                Finish(b^count).ToString("x16",System.Globalization.CultureInfo.InvariantCulture);
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
            struct FloatBits
            {
                [System.Runtime.InteropServices.FieldOffset(0)]public float value;
                [System.Runtime.InteropServices.FieldOffset(0)]public int bits;
            }
        }

        // Conservative dependencies for height/slope and four curvature probes.
        // Sampling clamps to the world and chooses floor(position / tile size).
        // One extra tile protects endpoint rounding and shared-edge normal changes.
        public static RectInt TerrainSampleTileRange(Rect area,float radius,float width,float depth,int countX,int countZ)
        {
            if(countX<=0||countZ<=0)return new RectInt(0,0,0,0);
            bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
            if(!Finite(radius)||!Finite(width)||!Finite(depth)||width<=0||depth<=0||
                !Finite(area.xMin)||!Finite(area.xMax)||!Finite(area.yMin)||!Finite(area.yMax))
                return new RectInt(0,0,countX,countZ);
            radius=Mathf.Max(0,radius);
            int Index(float value,float size,int count)=>Mathf.FloorToInt(Mathf.Clamp(value/size,0,count-1));
            int x0=Mathf.Max(0,Index(Mathf.Min(area.xMin,area.xMax)-radius,width,countX)-1);
            int z0=Mathf.Max(0,Index(Mathf.Min(area.yMin,area.yMax)-radius,depth,countZ)-1);
            int x1=Mathf.Min(countX,Index(Mathf.Max(area.xMin,area.xMax)+radius,width,countX)+2);
            int z1=Mathf.Min(countZ,Index(Mathf.Max(area.yMin,area.yMax)+radius,depth,countZ)+2);
            return new RectInt(x0,z0,x1-x0,z1-z0);
        }

        // Transfer four subdivisions per axis from the GPU to the source mesh.
        // Approximate density only: quadtree rounding, odd partitioning and fades
        // mean this is not an equal-triangle-count or equal-cost guarantee.
        public static float DisplacementBaseStep(float step,bool fine)
            => Mathf.Max(.5f,step)/(fine?4f:1f);
        public static float DisplacementGpuFactor(float factor,bool fine)
            => Mathf.Max(1,Mathf.Clamp(factor,1,64)/(fine?4f:1f));
        // Consistent diagonals give six incident triangles at regular interior
        // vertices. Checkerboard diagonals instead alternate eight-way/four-way hubs.
        public static bool UseRegularCellDiagonal(int cellX,int cellZ,bool consistent)
            => consistent || ((cellX*73856093^cellZ*19349663)&1)==0;

        // Convex XZ cell boundary, optionally with collinear edge midpoints.
        // Maximise the worst triangle quality without adding a central fan vertex.
        public static int[] TriangulateTransition(Vector2[] polygon)
        {
            int n=polygon.Length;
            if(n<3)return new int[0];
            var quality=new float[n,n];var split=new int[n,n];
            for(int i=0;i<n;i++)for(int j=0;j<n;j++){quality[i,j]=-1;split[i,j]=-1;}
            for(int i=0;i+1<n;i++)quality[i,i+1]=float.PositiveInfinity;
            for(int span=2;span<n;span++)for(int i=0;i+span<n;i++)
            {
                int j=i+span;
                for(int k=i+1;k<j;k++)
                {
                    if(quality[i,k]<0||quality[k,j]<0)continue;
                    Vector2 a=polygon[k]-polygon[i],b=polygon[j]-polygon[i],c=polygon[j]-polygon[k];
                    float area=Mathf.Abs(a.x*b.y-a.y*b.x),sum=a.sqrMagnitude+b.sqrMagnitude+c.sqrMagnitude;
                    if(sum<=0||area<=sum*1e-7f)continue;
                    float q=Mathf.Min(area/sum,Mathf.Min(quality[i,k],quality[k,j]));
                    if(q>quality[i,j]){quality[i,j]=q;split[i,j]=k;}
                }
            }
            if(split[0,n-1]<0)return new int[0];
            var result=new System.Collections.Generic.List<int>((n-2)*3);
            void Emit(int i,int j)
            {
                if(j-i<2)return;
                int k=split[i,j];result.Add(i);result.Add(k);result.Add(j);
                Emit(i,k);Emit(k,j);
            }
            Emit(0,n-1);return result.ToArray();
        }
        // Two-pass 8-neighbour chamfer distance in metres to empty mask samples.
        // Outside the chunk is unknown, NOT empty (do not invent internal seams).
        public static float[] MaskInteriorDistance(Color32[] mask,int size,float stepX,float stepZ)
        {
            var result=new float[mask.Length];
            float far=size*(stepX+stepZ),diagonal=(float)System.Math.Sqrt(stepX*stepX+stepZ*stepZ);
            for(int i=0;i<result.Length;i++)result[i]=mask[i].r>0?far:0;
            for(int pass=0;pass<2;pass++)
            {
                int direction=pass==0?1:-1;
                for(int row=0;row<size;row++)for(int col=0;col<size;col++)
                {
                    int x=pass==0?col:size-1-col,z=pass==0?row:size-1-row,index=z*size+x;
                    int previousX=x-direction,previousZ=z-direction;
                    if(previousX>=0&&previousX<size)result[index]=Mathf.Min(result[index],result[z*size+previousX]+stepX);
                    if(previousZ<0||previousZ>=size)continue;
                    result[index]=Mathf.Min(result[index],result[previousZ*size+x]+stepZ);
                    if(x>0)result[index]=Mathf.Min(result[index],result[previousZ*size+x-1]+diagonal);
                    if(x+1<size)result[index]=Mathf.Min(result[index],result[previousZ*size+x+1]+diagonal);
                }
            }
            return result;
        }
        // GPU normalized bilinear coordinates: texel centres are (i + .5) / size.
        // Do not substitute Texture2D.GetPixelBilinear: its CPU convention differs.
        public static Color SampleGpuBilinear(Color32[] pixels,int width,int height,float u,float v,bool repeat)
        {
            u=repeat?u-Mathf.Floor(u):Mathf.Clamp01(u);
            v=repeat?v-Mathf.Floor(v):Mathf.Clamp01(v);
            float x=u*width-.5f,y=v*height-.5f;
            int ix=Mathf.FloorToInt(x),iy=Mathf.FloorToInt(y);
            float tx=x-ix,ty=y-iy;
            int x0=repeat?(ix+width)%width:Mathf.Clamp(ix,0,width-1);
            int x1=repeat?(ix+1+width)%width:Mathf.Clamp(ix+1,0,width-1);
            int y0=repeat?(iy+height)%height:Mathf.Clamp(iy,0,height-1);
            int y1=repeat?(iy+1+height)%height:Mathf.Clamp(iy+1,0,height-1);
            return Color.LerpUnclamped(Color.LerpUnclamped(pixels[y0*width+x0],pixels[y0*width+x1],tx),
                Color.LerpUnclamped(pixels[y1*width+x0],pixels[y1*width+x1],tx),ty);
        }
        // Refinement follows dominant visible displacement layers, not signed height.
        // Keep these thresholds in sync with LTLayerTessellation.hlsl.
        public const float DisplacementVisibilityStart=.5f;
        public const float DisplacementVisibilityFull=.65f;
        public static float DisplacementVisibility(float[] weights,float[] heights,float blend,int activeLayers)
        {
            float highest=-1,total=0,visible=0;
            for(int i=0;i<weights.Length;i++)if(weights[i]>.00001f)highest=Mathf.Max(highest,heights[i]+weights[i]);
            for(int i=0;i<weights.Length;i++)
            {
                float w=weights[i];
                if(blend>.0001f)w*=Mathf.Lerp(1,Mathf.Clamp01((heights[i]+w-highest+.2f)/.2f),blend);
                total+=w;if((activeLayers&(1<<i))!=0)visible+=w;
            }
            return visible/Mathf.Max(total,.00001f);
        }
        // Immutable summed-area coverage: constant-time conservative rectangle queries.
        public sealed class CoverageGrid
        {
            readonly Color32[] pixels;
            readonly int[] sums;
            public readonly int size,hash;
            public CoverageGrid(Color32[] cells,int size)
            {
                if(size<1||cells.Length!=size*size)throw new System.ArgumentException("Invalid coverage grid.");
                this.size=size;pixels=(Color32[])cells.Clone();sums=new int[(size+1)*(size+1)];
                int h=17;
                for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                {
                    int value=pixels[y*size+x].r>0?1:0,i=(y+1)*(size+1)+x+1;
                    sums[i]=value+sums[i-1]+sums[i-size-1]-sums[i-size-2];
                    h=unchecked(h*31+value);
                }
                hash=h;
            }
            public bool Any(float minX,float minY,float maxX,float maxY)
            {
                if(maxX<0||maxY<0||minX>1||minY>1)return false;
                // One-cell halo matches the hull shader and retains boundary islands.
                int x0=Mathf.Clamp(Mathf.FloorToInt(minX*size)-1,0,size),y0=Mathf.Clamp(Mathf.FloorToInt(minY*size)-1,0,size);
                int x1=Mathf.Clamp(Mathf.FloorToInt(maxX*size)+2,0,size),y1=Mathf.Clamp(Mathf.FloorToInt(maxY*size)+2,0,size);
                int stride=size+1;
                return sums[y1*stride+x1]-sums[y0*stride+x1]-sums[y1*stride+x0]+sums[y0*stride+x0]>0;
            }
            // Mixed rectangles include both occupied and empty cells. Outside the
            // crop is empty; use the same one-cell guard as the hull query.
            public bool Boundary(float minX,float minY,float maxX,float maxY)
            {
                if(maxX<0||maxY<0||minX>1||minY>1)return false;
                int ax=Mathf.FloorToInt(minX*size)-1,ay=Mathf.FloorToInt(minY*size)-1;
                int bx=Mathf.FloorToInt(maxX*size)+2,by=Mathf.FloorToInt(maxY*size)+2;
                int x0=Mathf.Clamp(ax,0,size),y0=Mathf.Clamp(ay,0,size);
                int x1=Mathf.Clamp(bx,0,size),y1=Mathf.Clamp(by,0,size),stride=size+1;
                int count=sums[y1*stride+x1]-sums[y0*stride+x1]-sums[y1*stride+x0]+sums[y0*stride+x0];
                return count>0&&(ax<0||ay<0||bx>size||by>size||count<(x1-x0)*(y1-y0));
            }
            public CoverageGrid Include(Color32[] cells)
            {
                if(cells.Length!=pixels.Length)throw new System.ArgumentException("Coverage dimensions changed.");
                Color32[] merged=null;
                for(int i=0;i<cells.Length;i++)if(cells[i].r>0&&pixels[i].r==0)
                {
                    if(merged==null)merged=(Color32[])pixels.Clone();
                    merged[i]=cells[i];
                }
                return merged==null?this:new CoverageGrid(merged,size);
            }
        }
        // Each cell covers four endpoint weight texels. Max-reduced mips retain even
        // a one-texel displacement island when a large triangle encloses it.
        public static System.Collections.Generic.List<Color32[]> DisplacementPyramid(Color32[] first,Color32[] second,int resolution,int activeLayers)
            =>DisplacementPyramid(first,second,null,resolution,activeLayers);
        public static System.Collections.Generic.List<Color32[]> DisplacementPyramid(Color32[] first,Color32[] second,Color32[] third,int resolution,int activeLayers)
        {
            int size=resolution-1;
            if(size<1||(size&(size-1))!=0||first.Length!=resolution*resolution||second.Length!=first.Length||(third!=null&&third.Length!=first.Length))
                throw new System.ArgumentException("Expected endpoint weight grid with power-of-two cell count.");
            bool Occupied(int index)
            {
                var a=first[index];var b=second[index];var c=third!=null?third[index]:default;
                return ((activeLayers&1)!=0&&a.r>0)||((activeLayers&2)!=0&&a.g>0)||((activeLayers&4)!=0&&a.b>0)||((activeLayers&8)!=0&&a.a>0)||
                    ((activeLayers&16)!=0&&b.r>0)||((activeLayers&32)!=0&&b.g>0)||((activeLayers&64)!=0&&b.b>0)||((activeLayers&128)!=0&&b.a>0)||
                    ((activeLayers&256)!=0&&c.r>0)||((activeLayers&512)!=0&&c.g>0)||((activeLayers&1024)!=0&&c.b>0)||((activeLayers&2048)!=0&&c.a>0);
            }
            var pixels=new Color32[size*size];
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)
            {
                int i=y*resolution+x;
                byte value=(byte)(Occupied(i)||Occupied(i+1)||Occupied(i+resolution)||Occupied(i+resolution+1)?255:0);
                // R: any occupied; G: all occupied. Uniform nodes can terminate
                // exact intersection traversal early without broadening coverage.
                pixels[y*size+x]=new Color32(value,value,0,255);
            }
            var result=new System.Collections.Generic.List<Color32[]>{pixels};
            while(size>1)
            {
                int nextSize=size/2;var next=new Color32[nextSize*nextSize];
                for(int y=0;y<nextSize;y++)for(int x=0;x<nextSize;x++)
                {
                    int i=2*y*size+2*x;
                    byte value=(byte)(pixels[i].r>0||pixels[i+1].r>0||pixels[i+size].r>0||pixels[i+size+1].r>0?255:0);
                    byte full=(byte)(pixels[i].g>0&&pixels[i+1].g>0&&pixels[i+size].g>0&&pixels[i+size+1].g>0?255:0);
                    next[y*nextSize+x]=new Color32(value,full,0,255);
                }
                result.Add(next);pixels=next;size=nextSize;
            }
            return result;
        }
        public static Vector2 DisplacementDistances(float start,float end,bool globals,float globalStart)
        {
            end=Mathf.Max(.02f,globals?Mathf.Min(end,globalStart):end);
            return new Vector2(Mathf.Clamp(start,0,end-.01f),end);
        }
        public static float DisplacementBound(float amplitude,float center)
            =>Mathf.Clamp(amplitude,0,2)*Mathf.Max(Mathf.Clamp01(center),1-Mathf.Clamp01(center));
        static float NoiseCorner(int x,int y,int seed)
        {
            unchecked
            {
                uint h=(uint)x*0x8da6b343u^(uint)y*0xd8163841u^(uint)seed*0xcb1ab31fu;
                h^=h>>16;h*=0x7feb352du;h^=h>>15;h*=0x846ca68bu;h^=h>>16;
                return (h&0x00ffffffu)/16777215f;
            }
        }
        public static float ValueNoise(Vector2 point,int seed)
        {
            int x=Mathf.FloorToInt(point.x),y=Mathf.FloorToInt(point.y);
            float u=point.x-x,v=point.y-y;
            u=u*u*u*(u*(u*6-15)+10);v=v*v*v*(v*(v*6-15)+10);
            return Mathf.Lerp(Mathf.Lerp(NoiseCorner(x,y,seed),NoiseCorner(x+1,y,seed),u),
                Mathf.Lerp(NoiseCorner(x,y+1,seed),NoiseCorner(x+1,y+1,seed),u),v);
        }
        public static float NoiseCoverage(Vector2 localPoint,float size,int seed,float strength,float threshold,float softness)
        {
            if(strength<=0)return 1;
            var p=localPoint/Mathf.Max(.1f,size);
            float noise=(ValueNoise(p,seed)+.35f*ValueNoise(p*2.03f,unchecked(seed+7919)))/1.35f;
            float cut=threshold<=0?1:threshold>=1?0:softness<=0?(noise>=threshold?1:0):
                Mathf.Clamp01((noise-threshold)/Mathf.Max(.00001f,softness)+.5f);
            cut=cut*cut*(3-2*cut);
            return Mathf.Lerp(1,cut,Mathf.Clamp01(strength));
        }
        public static float RangeWeight(float value,Vector2 range,float feather)
        {
            float min=Mathf.Min(range.x,range.y),max=Mathf.Max(range.x,range.y);
            if(feather<=0)return value>=min&&value<=max?1:0;
            float a=Mathf.Clamp01((value-min+feather)/feather);
            float b=Mathf.Clamp01((max+feather-value)/feather);
            return a*a*(3-2*a)*b*b*(3-2*b);
        }
        // Positive = convex ridge, negative = concave hollow. Radius normalizes scale.
        public static float Curvature(float center,float left,float right,float back,float front,float radius)
            =>Mathf.Clamp((4*center-left-right-back-front)/Mathf.Max(.1f,radius),-1,1);
        public static bool TriangleWeights(Vector2 point,Vector3 a,Vector3 b,Vector3 c,out Vector3 weights)
        {
            weights=Vector3.zero;
            float det=(b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z);
            if(Mathf.Abs(det)<1e-10f)return false;
            float u=((b.z-c.z)*(point.x-c.x)+(c.x-b.x)*(point.y-c.z))/det;
            float v=((c.z-a.z)*(point.x-c.x)+(a.x-c.x)*(point.y-c.z))/det;
            weights=new Vector3(u,v,1-u-v);
            return weights.x>=-.00001f&&weights.y>=-.00001f&&weights.z>=-.00001f;
        }
        public static Vector2Int AtlasRange(int index,int count,int resolution)
            =>new Vector2Int(Mathf.RoundToInt((float)index*resolution/count),Mathf.RoundToInt((float)(index+1)*resolution/count));
        public static float Coverage(Vector2 point,bool rectangle,float falloff,float strength)
        {
            float distance=rectangle?Mathf.Max(Mathf.Abs(point.x),Mathf.Abs(point.y)):point.magnitude;
            float t=Mathf.Clamp01((1-distance)/Mathf.Max(.001f,falloff));
            return t*t*(3-2*t)*Mathf.Clamp01(strength);
        }
        public static void Composite(float[] weights,int slot,float alpha)
        {
            alpha=Mathf.Clamp01(alpha);
            for(int j=0;j<weights.Length;j++)weights[j]*=1-alpha;
            weights[slot]+=alpha;
        }
    }
}
