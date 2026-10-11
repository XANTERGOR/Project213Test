using System;
using System.Linq;
using System.Collections.Generic;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadJunctionFitChecks()
    {
        int checks=0,seams=0;
        void Check(bool ok,string message){Require(ok,message);checks++;}
        void Reject(Action action,string message){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Check(rejected,message);}
        void SameTopology(LTRoadMesh.Chunk a,LTRoadMesh.Chunk b)
        {
            Check(a.vertices.Length==b.vertices.Length&&a.triangles.SequenceEqual(b.triangles),"Fit topology unchanged");
            Check(a.submeshes.Length==b.submeshes.Length&&a.submeshes.Zip(b.submeshes,(x,y)=>x.SequenceEqual(y)).All(x=>x),"Fit submeshes unchanged");
            Check(a.uv.SequenceEqual(b.uv)&&a.uv2.SequenceEqual(b.uv2)&&a.uv3.SequenceEqual(b.uv3)&&a.uv4.SequenceEqual(b.uv4)&&a.colors.SequenceEqual(b.colors),"Fit UV/colors unchanged");
            Check(b.normals.All(LTRoadMesh.Finite)&&b.tangents.All(t=>LTRoadMesh.Finite(t.x)&&LTRoadMesh.Finite(t.y)&&LTRoadMesh.Finite(t.z)),"Fit finite shading");
        }
        Matrix4x4 Port(int i)
        {
            var m=Matrix4x4.identity;m.m23=-9.003334f;
            if(i==0){m.m00=0;m.m02=-1;m.m20=1;m.m22=0;}
            if(i==2){m.m00=-1;m.m22=-1;}
            return m;
        }
        var junction=Enumerable.Range(0,3).Select(l=>ReadRoadFbx("Assets/Data/Models/Environment/Roads/Roads_1/Road_Perek_1.fbx","*_LOD"+l)).ToArray();
        var originals=junction.Select(LTRoadMesh.ContentHash).ToArray();
        for(int i=0;i<3;i++)Console.WriteLine("Actual port "+i+": "+string.Join("; ",LTRoadJunctionProfileFit.Read(junction[0],Port(i),.005f).points.Select(p=>$"({p.x:R}, {p.y:R})")));
        var measured=LTRoadJunctionProfileFit.Read(junction[0],Port(0),.005f);
        float width=(float)Math.Round((measured.points.Max(p=>p.x)-measured.points.Min(p=>p.x))*10)*.1f;
        var targets=Enumerable.Range(0,3).Select(i=>LTRoadJunctionProfileFit.Create(junction[0],Port(i),.005f,width)).ToArray();
        Check(targets.All(t=>t.points.Length==7),"All three real entrances have seven visible anchors despite different underside triangles");
        for(int l=0;l<junction.Length;l++)
        {
            var output=junction[l];
            for(int i=0;i<3;i++)output=LTRoadJunctionProfileFit.Apply(output,Port(i),targets[i],1,.1f,-1);
            SameTopology(junction[l],output);
            for(int i=0;i<3;i++)
            {
                var rim=LTRoadJunctionProfileFit.Read(output,Port(i),.005f);
                Check(rim.points.Zip(targets[i].points,(a,b)=>(a-b).sqrMagnitude<1e-10f).All(x=>x),"Real junction mixed LOD seam exact");
            }
        }
        for(int module=1;module<=5;module++)for(int lod=0;lod<3;lod++)foreach(float baseY in new[]{0,.05f})foreach(bool start in new[]{true,false})
        foreach(float depth in new[]{0,.05f,.12f}.Where(d=>Math.Abs(d-baseY)<=.1f))
        {
            var raw=ReadRoadFbx($"Assets/Data/Models/Environment/Roads/Roads_1/Road_{module}.fbx","*_LOD"+lod);
            string before=LTRoadMesh.ContentHash(raw);var input=EndFixture((Vector3[])raw.vertices.Clone(),(int[])raw.triangles.Clone());
            float min=input.vertices.Min(v=>v.z),max=input.vertices.Max(v=>v.z);
            // The source pool uses LOD0's full mesh width, not its port-only width.
            var reference=ReadRoadFbx($"Assets/Data/Models/Environment/Roads/Roads_1/Road_{module}.fbx","*_LOD0");
            float xmin=reference.vertices.Min(v=>v.x),xmax=reference.vertices.Max(v=>v.x);
            for(int v=0;v<input.vertices.Length;v++)
            {var p=input.vertices[v];input.vertices[v]=new Vector3((p.x-(xmin+xmax)*.5f)*width/(xmax-xmin),p.y-baseY,p.z);}
            // Lowering the target mesh moves the seam frame, not the spline or terrain.
            var frame=Matrix4x4.identity;frame.m13=depth;frame.m23=start?-min:max;if(!start){frame.m00=-1;frame.m22=-1;}
            for(int i=0;i<3;i++)
            {
                string hash=LTRoadMesh.ContentHash(input);var output=LTRoadJunctionProfileFit.Apply(input,frame,targets[i],1,.1f,1);
                SameTopology(input,output);Check(hash==LTRoadMesh.ContentHash(input),"Prepared source immutable");
                var rim=LTRoadJunctionProfileFit.Read(output,frame,.005f);
                Check(rim.points.Zip(targets[i].points,(a,b)=>(a-b).sqrMagnitude<1e-9f).All(x=>x),"Road matches all three junction entrances");
                for(int v=0;v<input.vertices.Length;v++)
                {
                    if(frame.MultiplyPoint3x4(input.vertices[v]).z>=1)Check(output.vertices[v].Equals(input.vertices[v]),"Interior outside transition exact");
                    Check((output.vertices[v]-input.vertices[v]).magnitude<=.10001f,"Fit vertex movement bounded");
                }
                Check(output.hash==LTRoadJunctionProfileFit.Apply(input,frame,targets[i],1,.1f,1).hash,"Fit deterministic");
                seams++;
            }
            Check(before==LTRoadMesh.ContentHash(raw),"Raw asset copy unchanged");
        }
        Check(originals.SequenceEqual(junction.Select(LTRoadMesh.ContentHash)),"All original junction LODs unchanged");
        var test=ReadRoadFbx("Assets/Data/Models/Environment/Roads/Roads_1/Road_1.fbx","*_LOD0");
        var socket=Matrix4x4.identity;socket.m23=9;
        for(int i=0;i<test.vertices.Length;i++)test.vertices[i]+=Vector3.up*.02f;
        Reject(()=>LTRoadJunctionProfileFit.Apply(test,socket,targets[0],1,.005f,1),"Too-large mismatch rejected");
        Reject(()=>LTRoadJunctionProfileFit.Apply(test,socket,targets[0],.001f,.1f,1),"Tiny transition rejected");
        Reject(()=>LTRoadJunctionProfileFit.Apply(test,socket,targets[0],1,float.NaN,1),"Invalid shift rejected");
        var bad=EndFixture((Vector3[])test.vertices.Clone(),(int[])test.triangles.Clone());bad.vertices[0]=new Vector3(float.NaN,0,0);
        Reject(()=>LTRoadJunctionProfileFit.Read(bad,socket,.005f),"NaN rejected");
        // Small independent fixtures keep former cap-tail/height bugs covered even
        // when the user re-exports the authored assets during development.
        LTRoadMesh.Chunk Slab(float sign,bool tail,float rise=0,int rows=3)
        {
            var v=new List<Vector3>();var tri=new List<int>();
            var cross=new[]{new Vector2(-2,0),new Vector2(-2,.08f),new Vector2(0,.14f+rise),new Vector2(2,.08f),new Vector2(2,0)};
            for(int z=0;z<=rows;z++)foreach(var q in cross)v.Add(new Vector3(q.x,q.y,sign*z));
            for(int z=0;z<rows;z++)for(int x=0;x<4;x++){int a=z*5+x,b=a+5;tri.AddRange(new[]{a,b,a+1,a+1,b,b+1});}
            // Unrelated bottom cap triangulation including a longitudinal underside tail.
            int mid=v.Count;v.Add(new Vector3(0,0,0));
            for(int x=0;x<4;x++)tri.AddRange(new[]{mid,x,x+1});
            if(tail){int b=v.Count;v.Add(new Vector3(-1,-.02f,sign));tri.AddRange(new[]{0,mid,b});}
            return EndFixture(v.ToArray(),tri.ToArray());
        }
        var slab=Slab(-1,true);var canonical=LTRoadJunctionProfileFit.Create(slab,Matrix4x4.identity,.005f,4);
        Check(canonical.points.Length==5,"Bottom tail excluded from visible profile");
        var approach=Slab(1,false,.03f);for(int i=0;i<approach.vertices.Length;i++)approach.vertices[i]-=Vector3.up*.05f;
        var fitted=LTRoadJunctionProfileFit.Apply(approach,Matrix4x4.identity,canonical,1,.1f,1);SameTopology(approach,fitted);
        var split=SplitCollarFixture(approach);var splitFit=LTRoadJunctionProfileFit.Apply(split,Matrix4x4.identity,canonical,1,.1f,1);SameTopology(split,splitFit);
        for(int i=0;i<split.vertices.Length;i++)for(int j=0;j<i;j++)if(split.vertices[i].Equals(split.vertices[j]))
            Check(splitFit.vertices[i].Equals(splitFit.vertices[j]),"Split vertices remain coincident");
        var flat=EndFixture(new[]{new Vector3(-2,0,0),new Vector3(2,0,0),new Vector3(-2,0,-2),new Vector3(2,0,-2)},new[]{0,2,1,1,2,3});
        var flatTarget=LTRoadJunctionProfileFit.Create(flat,Matrix4x4.identity,.005f,4);
        var thin=EndFixture(new[]{new Vector3(-2,0,0),new Vector3(2,0,0),new Vector3(-2,0,.0001f),new Vector3(2,0,.0001f),new Vector3(-2,0,2),new Vector3(2,0,2)},new[]{0,2,1,1,2,3,2,4,3,3,4,5});
        Reject(()=>LTRoadJunctionProfileFit.Apply(thin,Matrix4x4.identity,flatTarget,1,.1f,1),"Thin row must not collapse");
        Reject(()=>LTRoadJunctionProfileFit.Apply(approach,Matrix4x4.identity,flatTarget,1,.1f,1),"Different visible corner counts rejected");
        var broken=Slab(1,false);broken.triangles=broken.triangles.Concat(broken.triangles.Take(3)).ToArray();
        Reject(()=>LTRoadJunctionProfileFit.Apply(broken,Matrix4x4.identity,canonical,1,.1f,1),"Duplicate seam faces rejected");
        foreach(float angle in new[]{0,17,90,180,277})
        {
            float c=(float)Math.Cos(angle*Math.PI/180),s=(float)Math.Sin(angle*Math.PI/180);
            var m=Matrix4x4.identity;m.m00=c;m.m02=s;m.m20=-s;m.m22=c;m.m03=902;m.m13=4;m.m23=137;
            var inv=Matrix4x4.identity;inv.m00=c;inv.m02=-s;inv.m20=s;inv.m22=c;inv.m03=-902*c+137*s;inv.m13=-4;inv.m23=-902*s-137*c;
            var transformed=Slab(1,false,.03f);transformed.vertices=transformed.vertices.Select(m.MultiplyPoint3x4).ToArray();
            var output=LTRoadJunctionProfileFit.Apply(transformed,inv,canonical,1,.1f,1);SameTopology(transformed,output);
        }
        foreach(bool reverse in new[]{false,true})for(int level=0;level<3;level++)
        {
            var module=ReadRoadFbx("Assets/Data/Models/Environment/Roads/Roads_1/Road_1.fbx","*_LOD"+level);
            var reference=ReadRoadFbx("Assets/Data/Models/Environment/Roads/Roads_1/Road_1.fbx","*_LOD0");
            var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.Asphalt;settings.width=width;settings.sampleSpacing=.5f;
            var control=new[]{new LTRoadPoint(Vector3.zero),new LTRoadPoint(new Vector3(0,0,3)),new LTRoadPoint(new Vector3(0,0,20)),new LTRoadPoint(new Vector3(5,2,45),3)};
            if(reverse)Array.Reverse(control);
            var path=LTRoadMath.Build(control,Matrix4x4.identity,settings);
            var chunks=LTRoadModuleMath.Bend(module,path,reference.vertices.Aggregate(Vector3.Min),reference.vertices.Aggregate(Vector3.Max),11,true,.05f,0,32,Matrix4x4.identity);
            var input=chunks[reverse?chunks.Count-1:0];var frame=Matrix4x4.identity;
            var output=LTRoadJunctionProfileFit.Apply(input,frame,targets[0],1,.1f,1);SameTopology(input,output);
            Check(output.hash==LTRoadJunctionProfileFit.Apply(input,frame,targets[0],1,.1f,1).hash,"Bent approach deterministic");
        }
        Console.WriteLine($"PASS junction profile fit: {checks} assertions, {seams} raw FBX combinations (5 modules, 3 LOD, start/end, 2 road base heights, 3 ports, 0/5/12 cm junction depths within shift budget); unchanged topology/UV/interior/source, bounded deterministic deformation, mixed junction LOD seams. Not native Unity import/render.");
    }
}
