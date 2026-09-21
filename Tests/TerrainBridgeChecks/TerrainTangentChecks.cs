using System;
using System.IO;
using UnityEngine;
using LocalTerrainPrototype;
partial class Checks
{
    static void TerrainTangentChecks()
    {
        var random=new System.Random(91725);
        var normals=new Vector3[10003];
        normals[0]=Vector3.up;normals[1]=Vector3.zero;normals[2]=Vector3.forward;
        for(int i=3;i<normals.Length;i++)
            normals[i]=new Vector3((float)random.NextDouble()*80-40,1,(float)random.NextDouble()*80-40).normalized;
        var original=(Vector3[])normals.Clone();
        var tangents=LTStampMesh.TerrainTangents(normals);
        for(int i=0;i<normals.Length;i++)
        {
            Require(normals[i].Equals(original[i]),"tangent calculation changed terrain normal");
            var t=(Vector3)tangents[i];
            Require(Math.Abs(t.magnitude-1)<1e-5 && tangents[i].w==-1,"terrain tangent must have stable negative handedness");
            if(i<3)continue;
            Require(Math.Abs(Vector3.Dot(normals[i],t))<1e-5,"tangent not perpendicular to source normal");
            Require(t.x>0 && t.z==0,"terrain U direction must follow +X at constant Z");
            var dv=new Vector3(0,-normals[i].z/normals[i].y,1);
            var b=Vector3.Cross(normals[i],t)*tangents[i].w;
            Require(Vector3.Dot(b,dv)>0,"bitangent points against increasing terrain V");
        }
        // Reproduce the sign threshold: interpolating inconsistent +/- signs
        // creates a discontinuity inside a patch, even when N and T are constant.
        Require(Mathf.Lerp(-1,1,.49f)<0 && Mathf.Lerp(-1,1,.51f)>0,"mixed-sign regression fixture");
        for(int i=0;i<=100;i++)
        {
            float alpha=i/100f;
            var n=Vector3.Lerp(normals[15],normals[516],alpha).normalized;
            var t=Vector4.Lerp(tangents[15],tangents[516],alpha);
            Require(t.w==-1 && Vector3.Dot(Vector3.Cross(n,(Vector3)t)*t.w,Vector3.forward)>0,
                "tessellation interpolation flipped terrain bitangent");
        }
        var seam=LTStampMesh.TerrainTangents(new[]{normals[20],normals[20]});
        Require(seam[0].Equals(seam[1]),"shared chunk/LOD normals must produce identical tangents");
        var down=LTStampMesh.TerrainTangents(new[]{Vector3.down})[0];
        Require(down.w==1 && ((Vector3)down).Equals(Vector3.right),"opposite-facing plane must retain UV orientation");
        var editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        // Production mesh emission at three LOD densities, before/after moving
        // a height feature and cut boundary. Normal accumulation is a CPU reference
        // for the editor upload; this does not drive a live Unity rock GameObject.
        foreach(int cells in new[]{4,8,16})
        foreach(float centre in new[]{5f,7.25f,5f})
        {
            var rect=new Rect(0,0,16,16);
            float Height(float x,float z)=>(float)Math.Exp(-((x-centre)*(x-centre)+(z-8)*(z-8))*.1)*3;
            var plan=LTStampMesh.Plan(rect,cells,false,0,100000,new System.Collections.Generic.List<LTStampMesh.Zone>(),Height);
            var forest=new LTBalancedForest(1,1,100000,new System.Collections.Generic.Dictionary<int,System.Collections.Generic.List<Vector3Int>>{{0,plan}});
            forest.Balance();
            LTStampMesh.Emit(rect,new Vector2(16,16),100000,forest,0,forest.Plan(0),Height,
                (x,z)=>(x-centre)*(x-centre)+(z-8)*(z-8)<2,
                out var vertices,out var emittedNormals,out var uv,out var indices);
            var finalNormals=new Vector3[vertices.Length];
            for(int k=0;k<indices.Length;k+=3)
            {
                int a=indices[k],b=indices[k+1],c=indices[k+2];
                var face=Vector3.Cross(vertices[b]-vertices[a],vertices[c]-vertices[a]);
                finalNormals[a]+=face;finalNormals[b]+=face;finalNormals[c]+=face;
            }
            for(int k=0;k<finalNormals.Length;k++)finalNormals[k]=finalNormals[k].normalized;
            var rebuilt=LTStampMesh.TerrainTangents(finalNormals);
            for(int k=0;k<vertices.Length;k++)
            {
                Require((uv[k]-new Vector2(vertices[k].x/16,vertices[k].z/16)).magnitude<1e-5,"emitted UV is no longer XZ");
                Require(rebuilt[k].w==-1,"rebuild/LOD/cut changed terrain handedness");
                Require(Math.Abs(Vector3.Dot(finalNormals[k],(Vector3)rebuilt[k]))<1e-5,"rebuilt basis not tangent to final normal");
            }
        }
        Console.WriteLine("PASS production terrain emission: 3 LOD densities x 3 moving height/cut configurations, stable UV orientation and tangent signs; not a live Unity scene test.");
        Require(editor.Contains("mesh.normals=normals;mesh.tangents=LTStampMesh.TerrainTangents(normals)"),"shared normals must refresh analytic tangents");
        Require(editor.Contains("TerrainTangents(c.mesh.normals)")&&editor.Contains("TerrainTangents(mesh.normals)"),"all terrain LOD builds must use analytic tangents");
        Require(editor.Contains("generated.SetNormals(finalNormals);generated.RecalculateTangents();"),"arbitrary rock UV tangents must remain unchanged");
        Require(editor.Contains("EditorApplication.delayCall += RepairLoadedTerrainTangents")&&editor.Contains("Undo.RegisterCompleteObjectUndo(mesh"),"loaded mesh upgrade must be available and undoable");
        Console.WriteLine("PASS terrain XZ tangents: 10000 slopes, UV orientation, mixed-sign regression, tessellation interpolation, shared seams/LODs, unchanged source normals and rock path; CPU/source checks only.");
    }
}
