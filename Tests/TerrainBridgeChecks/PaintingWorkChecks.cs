using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void PaintingWorkChecks()
    {
        int checkedCount=0,queries=0;
        void Check(bool ok,string why){checkedCount++;Require(ok,"painting work: "+why);}
        bool Terrain(float x,float z,out float h,out float slope)
        {queries++;h=.1f*x*x+.03f*z*z+.05f*x*z;slope=x+z;return !(x>29&&x<31&&z>20&&z<40);}
        var cache=new LTPaintMath.PixelTerrainCache(Terrain,64,64,8);
        bool Reference(float x,float z,float radius,out float value)
        {
            value=0;if(!Terrain(x,z,out float h,out _))return false;
            radius=Mathf.Min(Mathf.Max(.1f,radius),Mathf.Min(Mathf.Min(x,64-x),Mathf.Min(z,64-z)));
            if(radius<.1f)return true;
            if(!Terrain(x-radius,z,out float l,out _)||!Terrain(x+radius,z,out float r,out _)||
                !Terrain(x,z-radius,out float b,out _)||!Terrain(x,z+radius,out float f,out _))return false;
            value=LTPaintMath.Curvature(h,l,r,b,f,radius);return true;
        }
        foreach(float x in new[]{0f,.01f,.1f,1,15,25,30,31,32,59,63.99f,64})
        foreach(float z in new[]{0f,.05f,5,23,32,59,64})
        {
            cache.Begin(x,z);
            foreach(float radius in new[]{5f,5f,5f,5f,1,10,0,100})
            {
                bool a=cache.Curvature(radius,out float av),b=Reference(x,z,radius,out float bv);
                Check(a==b&&(!a||av.Equals(bv)),"cached curvature equals original at borders, holes and multiple radii");
            }
        }
        cache.Begin(16,16);queries=0;
        Check(cache.Surface(out _,out _),"centre query");
        for(int i=0;i<4;i++)Check(cache.Curvature(5,out _),"shared radius");
        Check(queries==5,"four curvature layers need five surface queries, not seventeen");
        cache.Begin(25,30);queries=0;
        for(int i=0;i<4;i++)Check(!cache.Curvature(5,out _),"failed probe is cached without painting a hole");
        Check(queries==3,"failed right probe does not get retried by every layer");
        cache.Begin(12,12);cache.Curvature(5,out _);queries=0;
        long allocationBefore=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)cache.Curvature(5,out _);
        long allocations=GC.GetAllocatedBytesForCurrentThread()-allocationBefore;
        Check(queries==0&&allocations==0,"warm same-pixel cache is allocation/query free");

        const int cells=128;
        var rect=new Rect(64,32,64,37);
        var vertices=new List<Vector3>();var triangles=new List<int>();
        for(int z=0;z<=cells;z++)for(int x=0;x<=cells;x++)
        {float px=rect.xMin+rect.width*x/cells,pz=rect.yMin+rect.height*z/cells;vertices.Add(new Vector3(px,.002f*px*px+.07f*pz,pz));}
        for(int z=0;z<cells;z++)for(int x=0;x<cells;x++)
        {
            if(x>50&&x<60&&z>30&&z<42)continue;
            int a=z*(cells+1)+x;triangles.AddRange(new[]{a,a+cells+1,a+1,a+1,a+cells+1,a+cells+2});
            if((x+z)%137==0)triangles.AddRange(new[]{a,a+cells+1,a+1}); // equal-height ties
        }
        int axis=LTPaintMath.TerrainBinAxis(triangles.Count/3);
        var oldBins=new List<int>[256];var bins=new List<int>[axis*axis];
        void Put(List<int>[] into,int n,int x0,int z0,int x1,int z1,int triangle)
        {for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++){int i=z*n+x;(into[i]??(into[i]=new List<int>())).Add(triangle);}}
        for(int i=0;i<triangles.Count;i+=3)
        {
            var a=vertices[triangles[i]];var b=vertices[triangles[i+1]];var c=vertices[triangles[i+2]];
            int x0=LTPaintMath.TerrainBin(Mathf.Min(a.x,Mathf.Min(b.x,c.x)),rect.xMin,rect.width,16);
            int x1=LTPaintMath.TerrainBin(Mathf.Max(a.x,Mathf.Max(b.x,c.x)),rect.xMin,rect.width,16);
            int z0=LTPaintMath.TerrainBin(Mathf.Min(a.z,Mathf.Min(b.z,c.z)),rect.yMin,rect.height,16);
            int z1=LTPaintMath.TerrainBin(Mathf.Max(a.z,Mathf.Max(b.z,c.z)),rect.yMin,rect.height,16);
            Put(oldBins,16,x0,z0,x1,z1,i);
            var r=LTPaintMath.TerrainTriangleBins(rect,a,b,c,axis);
            Put(bins,axis,r.xMin,r.yMin,r.xMax-1,r.yMax-1,i);
        }
        long oldCandidates=0,newCandidates=0;
        (int triangle,float height,Vector3 weights) Sample(List<int>[] index,int n,float x,float z,ref long candidates)
        {
            var list=index[LTPaintMath.TerrainBin(z,rect.yMin,rect.height,n)*n+LTPaintMath.TerrainBin(x,rect.xMin,rect.width,n)];
            int best=-1;float top=float.NegativeInfinity;Vector3 weight=default;
            if(list!=null)foreach(int t in list)
            {
                candidates++;var a=vertices[triangles[t]];var b=vertices[triangles[t+1]];var c=vertices[triangles[t+2]];
                if(!LTPaintMath.TriangleWeights(new Vector2(x,z),a,b,c,out var w))continue;
                float h=w.x*a.y+w.y*b.y+w.z*c.y;if(h<top)continue;
                best=t;top=h;weight=w;
            }
            return(best,top,weight);
        }
        var random=new System.Random(17863);
        for(int i=0;i<6000;i++)
        {
            float x=rect.xMin+rect.width*(float)random.NextDouble(),z=rect.yMin+rect.height*(float)random.NextDouble();
            if(i<3000)
            {
                x=rect.xMin+rect.width*(i%129)/128;
                z=rect.yMin+rect.height*((i/129)%129)/128;
                x+=new[]{0f,.000001f,-.000001f,.00001f,-.00001f}[i%5];
            }
            var old=Sample(oldBins,16,x,z,ref oldCandidates);var next=Sample(bins,axis,x,z,ref newCandidates);
            Check(old.Equals(next),"adaptive bins preserve hit, height, barycentrics and equal-height winner order");
        }
        Check(newCandidates<oldCandidates/3,"dense fixture reduces triangle candidates substantially");
        Check(LTPaintMath.TerrainBinAxis(1)==16&&LTPaintMath.TerrainBinAxis(int.MaxValue)==128,"sparse/dense memory bounds");
        // Irregular/skinny triangles and barycentric tolerance near new bin edges.
        foreach(int n in new[]{16,32,64,128})for(int fixture=0;fixture<200;fixture++)
        {
            Vector3 Vertex()=>new Vector3(rect.xMin+rect.width*(float)random.NextDouble(),0,rect.yMin+rect.height*(float)random.NextDouble());
            var a=Vertex();var b=Vertex();var c=Vertex();if(fixture%5==0)c=Vector3.Lerp(a,b,.7f)+new Vector3(.0001f,0,.0001f);
            var bounds=LTPaintMath.TerrainTriangleBins(rect,a,b,c,n);
            float xmin=Mathf.Min(a.x,Mathf.Min(b.x,c.x)),xmax=Mathf.Max(a.x,Mathf.Max(b.x,c.x));
            float zmin=Mathf.Min(a.z,Mathf.Min(b.z,c.z)),zmax=Mathf.Max(a.z,Mathf.Max(b.z,c.z));
            for(int probe=0;probe<100;probe++)
            {
                float u=(float)random.NextDouble(),v=(float)random.NextDouble()*(1-u);
                if(probe%3==0){u=-.000009f;v=(float)random.NextDouble();}
                var p=u*a+v*b+(1-u-v)*c;
                int ox=LTPaintMath.TerrainBin(p.x,rect.xMin,rect.width,16),oz=LTPaintMath.TerrainBin(p.z,rect.yMin,rect.height,16);
                bool oldIncluded=ox>=LTPaintMath.TerrainBin(xmin,rect.xMin,rect.width,16)&&ox<=LTPaintMath.TerrainBin(xmax,rect.xMin,rect.width,16)&&
                    oz>=LTPaintMath.TerrainBin(zmin,rect.yMin,rect.height,16)&&oz<=LTPaintMath.TerrainBin(zmax,rect.yMin,rect.height,16);
                if(!oldIncluded||!LTPaintMath.TriangleWeights(new Vector2(p.x,p.z),a,b,c,out _))continue;
                int bx=LTPaintMath.TerrainBin(p.x,rect.xMin,rect.width,n),bz=LTPaintMath.TerrainBin(p.z,rect.yMin,rect.height,n);
                Check(bx>=bounds.xMin&&bx<bounds.xMax&&bz>=bounds.yMin&&bz<bounds.yMax,"adaptive range retains every accepted legacy irregular/tolerance probe");
            }
        }
        for(int id=0;id<256;id++)
        {
            var neighbours=LTPaintMath.TerrainNeighbours(new[]{id},16,16);
            for(int other=0;other<256;other++)Check(neighbours.Contains(other)==
                (Math.Abs(id%16-other%16)<=1&&Math.Abs(id/16-other/16)<=1),"normal update halo includes diagonal corners without row wrap");
        }
        const string root="Assets/TerrainSystem/LocalTerrain/";
        string runtime=File.ReadAllText(root+"LTPaintRuntime.cs"),editor=File.ReadAllText(root+"Editor/LTEditor.cs");
        Check(runtime.Contains("samples.Curvature(stamp.curveRadius")&&runtime.Contains("samples.Begin(point.x,point.z)"),"production paint uses pixel-lifetime shared curvature");
        Check(runtime.Contains("TerrainTriangleBins(rect,a,b,c,tile.binAxis)"),"production index uses tested bin support");
        Check(runtime.Contains("influence.Intersects(paintGuard)"),"paint selection rejects empty road AABB space with footprint guard");
        Check(editor.Contains("new Dictionary<int,SeamChunkCache>(state.seamCache)")&&editor.Contains("if(validateOnly)"),"seam cache is private until validated publication");
        Check(editor.Contains("cached.version!=version")&&editor.Contains("cached.matrix!=matrix"),"seam cache validates mesh edits and transform");
        Check(editor.Contains("TerrainNeighbours(seamChanged")&&editor.Contains("if(!affected.Contains(chunk.z*world.chunksX+chunk.x))continue"),"normal writes use changed chunks plus neighbours");
        Check(editor.Contains("validateOnly?null:state.rockOutputs")&&editor.Contains("old.outputVersion!=EditorUtility.GetDirtyCount(old.output)"),"rock reuse never bypasses explicit validation or output edits");
        Check(editor.Contains("!pending.Any(p=>Overlap(region,ChunkRect(world,p.id)))")&&editor.Contains("!changedSurface.Any"),"rock reuse checks proposed and externally changed neighbour geometry");
        Check(runtime.Contains("BeginTick(force)")&&runtime.Contains("Stage.RockMaterials")&&editor.Contains("lastEditorPaintStages"),
            "coordinated editor cycles retain detailed paint timing without enabling a runtime report");
        Console.WriteLine($"PASS painting work: {checkedCount} checks; shared curvature 17 -> 5 queries/pixel; dense index {oldCandidates} -> {newCandidates} triangle candidates, exact legacy results. Managed/source contracts only, not a Unity timing.");
    }
}
