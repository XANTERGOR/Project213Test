using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using LocalTerrainPrototype;
using UnityEngine;

static class NextOptimizationChecks
{
    static int checks;
    static void Require(bool ok,string message)
    {checks++;if(!ok)throw new Exception("Next LT optimizations: "+message);}
    public static void Run()
    {
        checks=0;Fingerprint();SparseRoadMaps();Suppression();Contracts();
        Console.WriteLine($"PASS next LT optimizations: {checks} fingerprint / sparse UV footprint / suppression checks; CPU and source contracts only.");
    }
    static void Fingerprint()
    {
        var random=new System.Random(84217);
        var points=new Vector3[20000];var normals=new Vector3[points.Length];var indices=new int[points.Length*3];
        for(int i=0;i<points.Length;i++)
        {points[i]=new Vector3(i%257*.5f,(float)random.NextDouble()*14,i/257*.5f);normals[i]=new Vector3(.2f,.9f,.1f);}
        for(int i=0;i<indices.Length;i++)indices[i]=i%points.Length;
        LTPaintMath.GeometryFingerprint Hash()
        {
            var hash=new LTPaintMath.GeometryFingerprint();hash.Add(points.Length);hash.Add(1);hash.Add(indices.Length);
            for(int i=0;i<points.Length;i++){hash.Add(points[i]);hash.Add(normals[i]);}
            foreach(int index in indices)hash.Add(index);
            return hash;
        }
        string Text()
        {
            var text=new StringBuilder();
            void Add(Vector3 v)
            {text.Append(v.x.ToString("R",CultureInfo.InvariantCulture)).Append(',');text.Append(v.y.ToString("R",CultureInfo.InvariantCulture)).Append(',');text.Append(v.z.ToString("R",CultureInfo.InvariantCulture)).Append(',');}
            for(int i=0;i<points.Length;i++){Add(points[i]);Add(normals[i]);}
            foreach(int index in indices)text.Append(index).Append(',');
            return text.ToString();
        }
        string baseline=Hash().ToString();Require(baseline==Hash().ToString(),"stable content fingerprint");
        var oldCulture=CultureInfo.CurrentCulture;
        try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("tr-TR");Require(Hash().ToString()==baseline,"culture-independent key");}
        finally{CultureInfo.CurrentCulture=oldCulture;}
        points[997].y+=.0001f;Require(Hash().ToString()!=baseline,"vertex changes key");points[997].y-=.0001f;
        baseline=Hash().ToString();normals[120].z+=.001f;Require(Hash().ToString()!=baseline,"normal-only changes key");normals[120].z-=.001f;
        baseline=Hash().ToString();indices[993]++;Require(Hash().ToString()!=baseline,"triangle changes key");indices[993]--;
        var unique=new HashSet<string>();
        for(int i=0;i<20000;i++)
        {
            var hash=new LTPaintMath.GeometryFingerprint();hash.Add(771);hash.Add(i);hash.Add(882);
            Require(unique.Add(hash.ToString()),"single-word fixture keys remain distinct");
        }
        string Words(params int[] words){var hash=new LTPaintMath.GeometryFingerprint();foreach(int word in words)hash.Add(word);return hash.ToString();}
        Require(Words(1,2,3)!=Words(3,2,1)&&Words(1,2,3)!=Words(1,2,3,0),"order and trailing words affect fingerprint");
        var a=new LTPaintMath.GeometryFingerprint();a.Add(1f);
        var b=new LTPaintMath.GeometryFingerprint();b.Add(BitConverter.SingleToInt32Bits(1f));
        Require(a.ToString()==b.ToString(),"float uses exact IEEE bits");
        for(int i=0;i<4;i++){Hash();Text();}
        long allocated=GC.GetAllocatedBytesForCurrentThread();var value=Hash();
        Require(GC.GetAllocatedBytesForCurrentThread()-allocated==0,"streaming hash has no per-word allocations");
        GC.KeepAlive(value);
        var textTimes=new double[5];var hashTimes=new double[5];long textBytes=0,hashBytes=0;
        for(int i=0;i<5;i++)for(int order=0;order<2;order++)
        {
            bool optimized=(i+order)%2==0;var timer=new Stopwatch();
            long start=GC.GetAllocatedBytesForCurrentThread();timer.Start();
            string result=optimized?Hash().ToString():Text();timer.Stop();
            long bytes=GC.GetAllocatedBytesForCurrentThread()-start;
            (optimized?hashTimes:textTimes)[i]=timer.Elapsed.TotalMilliseconds;
            if(optimized)hashBytes=bytes;else textBytes=bytes;GC.KeepAlive(result);
        }
        Array.Sort(textTimes);Array.Sort(hashTimes);
        Console.WriteLine($"Geometry key synthetic CPU ({points.Length} vertices): old text serialization alone {textTimes[2]:F2} ms/{textBytes} B -> streaming hash {hashTimes[2]:F2} ms/{hashBytes} B. Old native Hash128 and mesh reads excluded; not Unity timings.");
    }
    static LTRoadPoint[] Path()=>new[]{
        new LTRoadPoint(new Vector3(10,2,10),7),new LTRoadPoint(new Vector3(180,6,240),-5),
        new LTRoadPoint(new Vector3(450,3,370),12),new LTRoadPoint(new Vector3(710,9,740),-8),
        new LTRoadPoint(new Vector3(990,2,990),2)};
    static void SparseRoadMaps()
    {
        long fullQueries=0,sparseQueries=0;
        foreach(var pattern in new[]{LTRoadPattern.Solid,LTRoadPattern.Tracks})
        foreach(int resolution in new[]{33,65,257})
        foreach(var chunk in new[]{new Rect(0,0,1024,1024),new Rect(140,170,180,110),new Rect(-8,-8,35,50),new Rect(974,980,40,28)})
        {
            var s=LTRoadMath.Settings.Default;s.pattern=pattern;s.projection=LTRoadProjection.Spline;s.blendWidth=0;s.shoulderWidth=0;
            var road=LTRoadMath.Build(Path(),Matrix4x4.identity,s);
            var region=road.TextureBakeRegion(chunk,resolution);int step=LTRoadMath.TextureBakeBlockSize;
            var filled=new bool[resolution*resolution];var values=new Vector4[filled.Length];var paint=new float[filled.Length];
            for(int bz=region.yMin;bz<region.yMax;bz+=step)for(int bx=region.xMin;bx<region.xMax;bx+=step)
            {
                var block=new RectInt(bx,bz,Math.Min(step,region.xMax-bx),Math.Min(step,region.yMax-bz));
                if(!road.TextureBakeBlockIntersects(chunk,resolution,block))continue;
                for(int z=block.yMin;z<block.yMax;z++)for(int x=block.xMin;x<block.xMax;x++)filled[z*resolution+x]=true;
            }
            for(int z=0;z<resolution;z++)for(int x=0;x<resolution;x++)
            {
                float px=chunk.xMin+chunk.width*x/(resolution-1),pz=chunk.yMin+chunk.height*z/(resolution-1);
                Require(road.TryTextureCoordinates(px,pz,out var uv,out var right),"full UV query succeeds");
                values[z*resolution+x]=new Vector4(uv.x,uv.y,right.x,right.y);
                paint[z*resolution+x]=road.PaintWeight(px,pz);
                if(chunk.width==1024&&resolution==257){fullQueries++;if(filled[z*resolution+x])sparseQueries++;}
            }
            // Any bilinear cell with a nonzero paint corner can contribute road pixels,
            // even where the analytic mask is zero. All four UV/Jacobian corners must
            // match the unbounded full map; this is stronger than checking paint centres.
            for(int z=0;z<resolution-1;z++)for(int x=0;x<resolution-1;x++)
            {
                int p=z*resolution+x;
                if(paint[p]<=0&&paint[p+1]<=0&&paint[p+resolution]<=0&&paint[p+resolution+1]<=0)continue;
                foreach(int at in new[]{p,p+1,p+resolution,p+resolution+1})
                {
                    Require(filled[at],"sparse UV must retain every visible bilinear/Jacobian corner at bends/caps/seams");
                    int ix=at%resolution,iz=at/resolution;
                    road.TryTextureCoordinates(chunk.xMin+chunk.width*ix/(resolution-1),chunk.yMin+chunk.height*iz/(resolution-1),out var uv,out var right);
                    Require(values[at].Equals(new Vector4(uv.x,uv.y,right.x,right.y)),"sparse and full UV values exactly match");
                }
            }
        }
        Require(sparseQueries<fullQueries/3,"sparse diagonal fixture skips most UV queries");
        Console.WriteLine($"Sparse road UV fixture: {fullQueries} full-grid queries -> {sparseQueries} retained queries; resolution/visible UV footprint unchanged.");
    }
    static void Suppression()
    {
        var random=new System.Random(682);
        foreach(float shoulder in new[]{0f,.0001f,.25f,3f})
        foreach(float padding in new[]{0f,.71f,7f})
        {
            var s=LTRoadMath.Settings.Default;s.mode=LTRoadMode.Asphalt;s.shoulderWidth=shoulder;s.blendWidth=0;
            var r=LTRoadMath.Build(Path(),Matrix4x4.identity,s);
            void Compare(float x,float z)
            {
                r.TrySample(x,z,out var hit);
                float expected=1-Mathf.SmoothStep(0,1,(hit.radialDistance-r.width*.5f-padding)/Mathf.Max(.0001f,r.shoulderWidth));
                Require(r.DisplacementSuppression(x,z,padding)==expected,"bounded asphalt suppression differs from original formula");
            }
            for(int i=0;i<5000;i++)
            {
                Compare((float)random.NextDouble()*1100-50,(float)random.NextDouble()*1100-50);
                var p=r.samples[i%r.samples.Count];float side=((float)random.NextDouble()*2-1)*(r.width+shoulder+padding);
                Compare(p.position.x+p.right.x*side,p.position.z+p.right.z*side);
            }
            foreach(var index in new[]{0,r.samples.Count/2,r.samples.Count-1})
            foreach(float epsilon in new[]{-.001f,0,.001f})
            {
                var p=r.samples[index];float d=r.width*.5f+padding+shoulder+epsilon;
                Compare(p.position.x+p.right.x*d,p.position.z+p.right.z*d);
            }
            for(int warm=0;warm<200;warm++)r.DisplacementSuppression(warm,5,padding);
            long allocated=GC.GetAllocatedBytesForCurrentThread();float sum=0;
            for(int i=0;i<1000;i++)sum+=r.DisplacementSuppression(i,5,padding);
            Require(GC.GetAllocatedBytesForCurrentThread()-allocated==0,"suppression queries allocate no memory");GC.KeepAlive(sum);
        }
    }
    static void Contracts()
    {
        const string root="Assets/TerrainSystem/LocalTerrain/";
        string runtime=File.ReadAllText(root+"LTPaintRuntime.cs");
        var tile=runtime.Substring(runtime.IndexOf("tile=new Tile{"));tile=tile.Substring(0,tile.IndexOf("cache[chunk]=tile;"));
        Require(!tile.Contains("StringBuilder")&&!tile.Contains("ToString(\"R\"")&&tile.Contains("fingerprint.Add(tile.vertices[i])")&&
            tile.Contains("fingerprint.Add(tile.normals[i])")&&tile.Contains("fingerprint.Add(index)"),"terrain fingerprints retain all transformed geometry without per-number strings");
        Require(runtime.Contains("const long Budget=64L*1024*1024")&&runtime.Contains("size<=Budget-bytes")&&
            runtime.Contains("var displacementMaskReadbacks=new MaskReadbackCache()")&&
            runtime.Contains("maskPixels[i]=maskReadbacks.Read(maps[i])"),"readback reuse is bounded and tick-local");
        int gate=runtime.IndexOf("if(maskPixels[i]!=null&&weights[i]>.00001f)");
        Require(gate>=0&&runtime.IndexOf("if(TryRoadProjectionUV(state,i,px,pz,out var roadUV))textureUV=roadUV;",gate)>gate,
            "zero-weight and texture-free slots skip unused road UV work");
        string mapping=File.ReadAllText(root+"LTPaintRoadProjection.cs");
        Require(mapping.Contains("road.DisplacementSuppression(px, pz, guard)")&&
            mapping.Contains("LTRoadProjectionMath.Bake(sources[slot],rect,RoadProjectionSize)")&&
            File.ReadAllText(root+"LTRoadProjectionMath.cs").Contains("TextureBakeBlockIntersects(rect,size,block)"),"tested road math is wired into the production baker");
        var capture=File.ReadAllText(root+"LTPaintCpuCapture.cs");
        Require(runtime.Contains("Measure(LTPaintCpuCapture.Stage.DisplacementBake)")&&
            capture.Contains("new double[StageCount]")&&capture.Contains("stage<report.stages.Length")&&
            capture.Contains("Logs/LocalTerrainCpuBenchmarks"),
            "displacement timing is included in the variable-length opt-in CPU report");
    }
}
