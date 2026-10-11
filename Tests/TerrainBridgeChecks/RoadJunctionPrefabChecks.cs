using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadJunctionPrefabChecks()
    {
        int checks=0;
        void Check(bool ok,string label){Require(ok,label);checks++;}
        void Reject(Action action,string label){bool failed=false;try{action();}catch(InvalidOperationException){failed=true;}Check(failed,label);}
        LTRoadMesh.Chunk Strip(float side,int interiorRows=2,bool extraEnd=false)
        {
            var v=new List<Vector3>();var tris=new List<int>();
            for(int z=0;z<=interiorRows;z++)for(int x=0;x<=4;x++)v.Add(new Vector3(x-2,(x==2?.2f:0),side*z*4/interiorRows));
            for(int z=0;z<interiorRows;z++)for(int x=0;x<4;x++)
            {int a=z*5+x,b=a+5;tris.AddRange(new[]{a,b,a+1,a+1,b,b+1});}
            if(extraEnd){v[2]=new Vector3(0,.21f,0);}
            return EndFixture(v.ToArray(),tris.ToArray());
        }
        var source=Strip(-1);string originalHash=LTRoadMesh.ContentHash(source);
        var port=LTRoadJunctionSeams.Create(source,Matrix4x4.identity,.0005f,4);Check(port.PointCount==5,"five canonical points");
        foreach(int rows in new[]{1,2,4,8})foreach(float side in new[]{-1f,1f})
        {
            var input=Strip(side,rows);string before=LTRoadMesh.ContentHash(input);
            // Real import noise at a seam, including its UV/normal split duplicates.
            for(int i=0;i<5;i++)input.vertices[i]+=new Vector3(.00004f,0,.00003f);
            var noisy=(Vector3[])input.vertices.Clone();var output=LTRoadJunctionSeams.Snap(input,Matrix4x4.identity,port);
            Check(input.vertices.SequenceEqual(noisy),"source not mutated");
            Check(output.triangles.SequenceEqual(input.triangles),"indices preserved");
            Check(output.submeshes[0].SequenceEqual(input.submeshes[0]),"submeshes preserved");
            Check(output.normals.SequenceEqual(input.normals)&&output.tangents.SequenceEqual(input.tangents)&&output.uv.SequenceEqual(input.uv),"authored shading/uv");
            Check(output.uv2.SequenceEqual(input.uv2)&&output.uv3.SequenceEqual(input.uv3)&&output.uv4.SequenceEqual(input.uv4)&&output.colors.SequenceEqual(input.colors),"extra channels");
            for(int i=0;i<5;i++)Check(output.vertices[i].Equals(source.vertices[i]),"canonical seam exact");
            Check(output.vertices.Skip(5).SequenceEqual(input.vertices.Skip(5)),"interior unchanged");
            Check(output.hash==LTRoadJunctionSeams.Snap(output,Matrix4x4.identity,port).hash,"idempotent");
            Check(output.hash==LTRoadJunctionSeams.Snap(input,Matrix4x4.identity,port).hash,"deterministic");
        }
        Check(originalHash==LTRoadMesh.ContentHash(source),"reference not mutated");
        Reject(()=>LTRoadJunctionSeams.Create(Strip(1),Matrix4x4.identity,.0005f,4),"outward direction guard");
        Reject(()=>LTRoadJunctionSeams.Create(source,Matrix4x4.identity,.0005f,5),"width guard");
        Reject(()=>LTRoadJunctionSeams.Snap(Strip(1,2,true),Matrix4x4.identity,port),"profile height mismatch");
        foreach(float depth in new[]{.05f,.12f})
        {
            var input=Strip(1);input.vertices=input.vertices.Select(p=>p-Vector3.up*depth).ToArray();
            var frame=Matrix4x4.identity;frame.m13=depth;
            var output=LTRoadJunctionSeams.Snap(input,frame,port);
            Check(output.vertices.SequenceEqual(input.vertices)&&output.triangles.SequenceEqual(input.triangles),"equal depth strict Fuse remains position-only");
            Reject(()=>LTRoadJunctionSeams.Snap(Strip(1),frame,port),"strict Fuse must not silently repair different burial heights");
        }
        var split=SplitCollarFixture(Strip(1));var splitResult=LTRoadJunctionSeams.Snap(split,Matrix4x4.identity,port);
        Check(splitResult.vertices.Length==split.vertices.Length&&splitResult.triangles.SequenceEqual(split.triangles),"UV splits not welded");
        var bad=Strip(1);bad.triangles=bad.triangles.Concat(bad.triangles.Take(3)).ToArray();
        Reject(()=>LTRoadJunctionSeams.Snap(bad,Matrix4x4.identity,port),"non-manifold seam guard");
        bad=Strip(1);bad.vertices[2]+=Vector3.forward;
        Reject(()=>LTRoadJunctionSeams.Snap(bad,Matrix4x4.identity,port),"missing rim point guard");
        foreach(float angle in new[]{0f,17f,90f,180f,277f})
        {
            // Build transforms explicitly: Unity's native TRS/inverse are unavailable here.
            double radians=angle*Math.PI/180;float cos=(float)Math.Cos(radians),sin=(float)Math.Sin(radians);
            var transform=Matrix4x4.identity;transform.m00=cos;transform.m02=sin;transform.m20=-sin;transform.m22=cos;
            transform.m03=902;transform.m13=4;transform.m23=137;
            var inverse=Matrix4x4.identity;inverse.m00=cos;inverse.m02=-sin;inverse.m20=sin;inverse.m22=cos;
            inverse.m03=-cos*902+sin*137;inverse.m13=-4;inverse.m23=-sin*902-cos*137;
            var a=Strip(-1);a.vertices=a.vertices.Select(transform.MultiplyPoint3x4).ToArray();
            var transformedPort=LTRoadJunctionSeams.Create(a,inverse,.001f,4);
            var b=Strip(1);b.vertices=b.vertices.Select(transform.MultiplyPoint3x4).ToArray();
            var result=LTRoadJunctionSeams.Snap(b,inverse,transformedPort);
            for(int i=0;i<5;i++)Check((result.vertices[i]-a.vertices[i]).magnitude<.001f,"world-space/yaw seam");
            Check(result.vertices.Skip(5).SequenceEqual(b.vertices.Skip(5)),"transformed interior untouched");
        }
        var narrow=EndFixture(new[]{new Vector3(-2,0,0),new Vector3(2,0,0),new Vector3(-2,0,.0001f),new Vector3(2,0,.0001f),new Vector3(-2,0,2),new Vector3(2,0,2)},new[]{0,2,1,1,2,3,2,4,3,3,4,5});
        var flat=EndFixture(new[]{new Vector3(-2,0,0),new Vector3(2,0,0),new Vector3(-2,0,-2),new Vector3(2,0,-2)},new[]{0,2,1,1,2,3});
        var flatPort=LTRoadJunctionSeams.Create(flat,Matrix4x4.identity,.0005f,4);
        Reject(()=>LTRoadJunctionSeams.Snap(narrow,Matrix4x4.identity,flatPort),"thin row collapse guard");
        bad=Strip(1);bad.vertices[2]=new Vector3(float.NaN,0,0);
        Reject(()=>LTRoadJunctionSeams.Snap(bad,Matrix4x4.identity,port),"nonfinite guard");
        var foundation=LTRoadJunctionMath.Foundation(new Vector3(10,3,20),8,3,.5f,7);
        Check(foundation.Weight(10,20)==1&&foundation.Weight(50,50)==0,"foundation bounds");
        Check(foundation.hash!=LTRoadJunctionMath.Foundation(new Vector3(10,3,20),8,3,.5f,8).hash,"dependency invalidation");
        RoadNetworkChecks();
        Console.WriteLine($"PASS authored junction seams: {checks} assertions; compatible LODs, topology/attributes, strict rejection, foundation + legacy junction regression. Native prefab import is a separate Unity menu check.");
    }
}
