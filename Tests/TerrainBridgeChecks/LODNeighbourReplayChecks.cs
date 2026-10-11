using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    // Read-only replay of recorded camera positions against CURRENT saved assets.
    // Does not load Unity, move its camera or measure GPU/SetIndices/Editor cost.
    static void LODNeighbourReplayChecks(string[] args)
    {
        int arg=Array.IndexOf(args,"--lod-neighbour-replay");
        Require(args.Length==arg+6,"Usage: --lod-neighbour-replay assetFolder report.json chunkWidth hysteresis distancesCSV (world origin 0, identity transform)");
        string folder=args[arg+1];
        var report=JsonSerializer.Deserialize<LTLODCpuCapture.Report>(File.ReadAllText(args[arg+2]),new JsonSerializerOptions{IncludeFields=true});
        float F(string value)=>float.Parse(value,CultureInfo.InvariantCulture);
        float width=F(args[arg+3]),hysteresis=F(args[arg+4]);var distances=args[arg+5].Split(',').Select(F).ToArray();
        var grid=Regex.Match(report.settings,@"chunks=(\d+)x(\d+)");
        int columns=int.Parse(grid.Groups[1].Value),rows=int.Parse(grid.Groups[2].Value),count=columns*rows;
        int divisions=int.Parse(Regex.Match(report.settings,@"patches=(\d+)").Groups[1].Value);
        Require(report.settings.Contains("mode=WithinChunk")&&report.samples.Length>0,"spatial flight capture");
        var inputs=new LTSpatialLODTopology.Chunk[count];
        int renamed=0,absent=0;
        for(int c=0;c<count;c++)
        {
            string stem=$"Chunk_{c%columns}_{c/columns}";
            // AssetDatabase can append a numeric suffix after delete/recreate.
            // Never choose among duplicate banks or modify the user's files.
            var candidates=Directory.GetFiles(folder,stem+"*_SpatialLOD.asset")
                .Where(path=>Regex.IsMatch(Path.GetFileName(path),"^"+stem+@"( \d+)?_SpatialLOD\.asset$")).ToArray();
            Require(candidates.Length<=1,"ambiguous saved banks for "+stem);
            if(candidates.Length==0){absent++;continue;}
            if(Path.GetFileName(candidates[0])!=stem+"_SpatialLOD.asset")renamed++;
            var saved=ReadSavedHoleChunk(candidates[0],false);
            Require(saved.divisions==divisions,"saved divisions match capture");inputs[c]=saved.topology;
            foreach(var p in inputs[c].patches)Require(Math.Abs(p.bounds.size.x-width/divisions)<.001,"saved patch width matches replay");
        }
        var oldInputs=inputs.Select(c=>c==null?null:new SpatialNeighbourReference.Chunk{cells=c.cells,patches=c.patches}).ToArray();
        Console.WriteLine($"READ-ONLY MANAGED REPLAY: {count-absent} saved banks, {renamed} uniquely suffixed filenames, {absent} absent slots; {report.samples.Length} recorded positions; width {width}, distances {string.Join('/',distances)}, hysteresis {hysteresis}. Uses current banks by filename, not live scene bindings. Identity world transform; initial levels 0, initial selection/publication excluded. Not native Unity/frame/GPU timings or an exact reconstruction of pre-capture hysteresis history.");
        int Level(float distance,int current)
        {int l=0;for(int i=0;i<distances.Length;i++){if(distance<distances[i]+(current>i?-hysteresis:hysteresis))break;l=i+1;}return l;}
        double Ms(long start)=>(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;
        void Stats(string label,List<double> balance,List<double> write)
        {
            var total=balance.Zip(write,(a,b)=>a+b).OrderBy(x=>x).ToArray();
            Console.WriteLine($"{label}: balance mean/max {balance.Average():F3}/{balance.Max():F3} ms; indices mean/max {write.Average():F3}/{write.Max():F3} ms; combined mean/p95/max {total.Average():F3}/{total[(int)Math.Ceiling(total.Length*.95)-1]:F3}/{total.Last():F3} ms");
        }
        for(int run=0;run<3;run++)
        {
            var old=new SpatialNeighbourReference(columns,rows,divisions,oldInputs);
            long setupStart=Stopwatch.GetTimestamp(),setupBytes=GC.GetAllocatedBytesForCurrentThread();
            var current=new LTSpatialLODTopology(columns,rows,divisions,inputs);
            Console.WriteLine($"Topology setup {Ms(setupStart):F2} ms; allocated {(GC.GetAllocatedBytesForCurrentThread()-setupBytes)/1048576.0:F2} MiB; parent-link payload {current.NeighbourCacheBytes/1048576.0:F2} MiB (bounded at 16 MiB).");
            var oldBuffers=Enumerable.Range(0,count).Select(_=>new List<int>()).ToArray();
            var buffers=Enumerable.Range(0,count).Select(_=>new List<int>()).ToArray();
            void Select(LTLODCpuCapture.Sample sample)
            {
                for(int c=0;c<count;c++)
                {
                    if(inputs[c]==null)continue;
                    var local=new Vector3(sample.cameraX-c%columns*width,sample.cameraY,sample.cameraZ-c/columns*width);
                    for(int p=0;p<inputs[c].patches.Length;p++)
                    {
                        Require(old.Requested(c,p)==current.Requested(c,p),"same selection history");
                        // Bounds.SqrDistance is a native Unity ECall. Equivalent
                        // point/AABB arithmetic keeps this replay standalone.
                        var b=inputs[c].patches[p].bounds;var centre=b.center;var extent=b.extents;
                        float dx=Math.Max(0,Math.Abs(local.x-centre.x)-extent.x),dy=Math.Max(0,Math.Abs(local.y-centre.y)-extent.y),dz=Math.Max(0,Math.Abs(local.z-centre.z)-extent.z);
                        int level=sample.forceLOD>=0?sample.forceLOD:Level((float)Math.Sqrt(dx*dx+dy*dy+dz*dz),old.Requested(c,p));
                        old.SetLevel(c,p,level);current.SetLevel(c,p,level);
                    }
                }
            }
            Select(report.samples[0]);old.Update();current.Update();old.Validate();current.Validate();
            for(int c=0;c<count;c++)if(inputs[c]!=null){old.WriteIndices(c,oldBuffers[c]);current.WriteIndices(c,buffers[c]);Require(oldBuffers[c].SequenceEqual(buffers[c]),"initial output parity");}
            var oldBalance=new List<double>();var balance=new List<double>();var oldWrite=new List<double>();var write=new List<double>();
            long hits=0,builds=0,cells=0,processed=0,oldProcessed=0;int uploads=0;
            foreach(var sample in report.samples)
            {
                Select(sample);
                void OldStep()
                {
                    long t=Stopwatch.GetTimestamp();old.Update();oldBalance.Add(Ms(t));
                    t=Stopwatch.GetTimestamp();foreach(int c in old.DirtyChunks)old.WriteIndices(c,oldBuffers[c]);oldWrite.Add(Ms(t));
                }
                void Step()
                {
                    long t=Stopwatch.GetTimestamp();current.Update();balance.Add(Ms(t));
                    t=Stopwatch.GetTimestamp();foreach(int c in current.DirtyChunks)current.WriteIndices(c,buffers[c]);write.Add(Ms(t));
                }
                if(run%2==0){OldStep();Step();}else{Step();OldStep();}
                Require(old.DirtyChunks.SetEquals(current.DirtyChunks),"recorded route publishes same dirty chunks");
                Require(old.ProcessedCells==current.ProcessedCells&&old.ResetPatches==current.ResetPatches,"recorded route retains balance propagation");
                foreach(int c in current.DirtyChunks)Require(oldBuffers[c].SequenceEqual(buffers[c]),"recorded route index sequence parity");
                hits+=current.IndexCacheHits;builds+=current.IndexCacheBuilds;cells+=current.IndexCacheCells;
                processed+=current.ProcessedCells;oldProcessed+=old.ProcessedCells;uploads+=current.DirtyChunks.Count;
                Require(current.IndexCacheFallbacks==0,"saved route fits default index cache");
            }
            old.Validate();current.Validate();
            Stats($"Run {run+1} BEFORE",oldBalance,oldWrite);Stats($"Run {run+1} AFTER",balance,write);
            Console.WriteLine($"PASS replay run {run+1}: same dirty chunks and index sequences at every pose; publications {uploads}; cache reused/built patches {hits}/{builds}; rebuilt cells {cells}; balance cells before/after {oldProcessed}/{processed}; retained index capacity {current.IndexCacheBytes/1048576.0:F2} MiB.");
        }
    }
}


