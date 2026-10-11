using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;

partial class Checks
{
    static void EditorTickChecks()
    {
        int checks=0;
        void Check(bool ok,string why){checks++;Require(ok,"editor tick: "+why);}
        var probe=new LTChunkLayoutProbe();var random=new Random(76231);string previous=null;
        var entries=new Dictionary<int,int>();
        int ox=0,oz=0,columns=16,rows=16;
        // Same decision as the old sorted layout string, including random order,
        // identity replacement, removal, restoration and grid metadata changes.
        for(int trial=0;trial<3000;trial++)
        {
            int slot=random.Next(256);
            switch(trial%9)
            {
                case 0: entries[slot]=random.Next(1,100000);break;
                case 1: entries.Remove(slot);break;
                case 2: ox=random.Next(-5,5);break;
                case 3: oz=random.Next(-5,5);break;
                case 4: columns=random.Next(16,33);break;
                case 5: rows=random.Next(16,33);break;
            }
            var order=entries.OrderBy(_=>random.Next()).ToArray();
            string legacy=ox+":"+oz+":"+columns+":"+rows+":"+string.Join(";",entries.OrderBy(p=>p.Key).Select(p=>p.Key+":"+p.Value));
            probe.Begin(ox,oz,columns,rows);
            foreach(var p in order)Check(probe.Add(p.Key,p.Value),"unique coordinate accepted");
            Check(probe.Unchanged==(previous==legacy),"exact legacy layout parity");
            if(previous!=legacy){probe.Accept();previous=legacy;}
        }
        probe.Begin(0,0,1,1);Check(probe.Add(0,10)&&!probe.Add(0,11),"duplicate rejected");
        // Do not Accept a failed collection: the last accepted baseline survives.
        probe.Begin(ox,oz,columns,rows);foreach(var p in entries)probe.Add(p.Key,p.Value);
        Check(probe.Unchanged,"failed poll did not overwrite accepted data");
        probe=new LTChunkLayoutProbe();probe.Begin(0,0,1,1);Check(!probe.Unchanged,"initial empty layout not silently skipped");
        probe.Accept();probe.Begin(0,0,1,1);Check(probe.Unchanged,"empty layout stable");
        for(int pass=0;pass<3;pass++)
        {probe.Begin(0,0,16,16);for(int i=0;i<256;i++)probe.Add(i,i+1);probe.Accept();}
        long before=GC.GetAllocatedBytesForCurrentThread();bool allStable=true;
        for(int poll=0;poll<1000;poll++)
        {probe.Begin(0,0,16,16);for(int i=255;i>=0;i--)allStable&=probe.Add(i,i+1);allStable&=probe.Unchanged;}
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(allStable&&allocated==0,"warm exact-layout polling allocated managed memory");
        const string root="Assets/TerrainSystem/LocalTerrain/";
        string chunk=File.ReadAllText(root+"Editor/LTChunkManagement.cs");
        Check(chunk.Contains("GetComponentsInChildren(false,chunks)")&&chunk.Contains("id<0")&&chunk.Contains("!probe.Add(id,chunk.GetInstanceID())"),"live polling and coordinate errors retained");
        Check(chunk.Contains("state.chunkLayout!=null&&probe.Unchanged")&&chunk.Contains("probe.Accept()"),"explicit invalidation cannot be skipped");
        string engine=File.ReadAllText(root+"Editor/LTEditor.cs");
        int start=engine.IndexOf("static void Walk(Transform",StringComparison.Ordinal),end=engine.IndexOf("static float MeshContactCell",start,StringComparison.Ordinal);
        string walk=engine.Substring(start,end-start);
        Check(!walk.Contains(".GetComponent<")&&!walk.Contains("foreach (Transform")&&walk.Contains("TryGetComponent<MeshFilter>"),"stamp traversal uses optional lookup and indexed siblings");
        Check(engine.Contains("nextPoll = now + .1;")&&engine.Contains("now-state.lastChange>=.15")&&engine.Contains("stageTimer.Elapsed.TotalMilliseconds<4"),"poll and build budgets unchanged");
        string paint=File.ReadAllText(root+"LTPaintStamp.cs");
        Check(paint.Contains("TryGetComponent<LTRoad>(out var road);return road;")&&paint.Contains("TryGetComponent<LTRoadJunction>(out var node);return node;"),"optional paint companions queried live");
        string markers=File.ReadAllText(root+"Editor/LTEditorTickProfiling.cs");
        foreach(string marker in new[]{"Poll","WorldScan","PollLOD","ReloadResume","ChunkLayout","BuildAdvance","CoarseAdvance","PaintPoll","Colliders","SeamBake"})
            Check(markers.Contains("LT.Editor."+marker+"\""),"coarse marker "+marker);
        Console.WriteLine($"PASS editor tick: {checks} assertions, 3000 legacy layout decisions, 1000 warm 256-chunk polls / {allocated} bytes in pure bookkeeping. Native lookup/allocation/scene behavior and real spike timing remain separate checks.");
    }
}
