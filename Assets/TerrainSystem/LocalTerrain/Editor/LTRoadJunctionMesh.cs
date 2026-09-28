using System;
using System.Collections.Generic;
using UnityEngine;
namespace LocalTerrainPrototype
{
    public static class LTRoadJunctionMesh
    {
        // Identical outer boundary at every level; only coplanar interior rings collapse.
        public static LTRoadMesh.Chunk Build(LTRoadJunctionMath.Snapshot data,int rings,float repeat,float offset,Matrix4x4 local)
        {
            if(data==null||data.outline.Length<3||rings<1||rings>32||repeat<=0||float.IsNaN(repeat)||float.IsInfinity(repeat))
                throw new ArgumentException("Invalid junction mesh parameters.");
            int n=data.outline.Length;var vertices=new Vector3[1+n*rings];var uv=new Vector2[vertices.Length];
            var normals=new Vector3[vertices.Length];var tangents=new Vector4[vertices.Length];
            var centre=data.centre+Vector3.up*offset;vertices[0]=centre;
            for(int ring=1;ring<=rings;ring++)for(int i=0;i<n;i++)
                vertices[1+(ring-1)*n+i]=Vector3.Lerp(centre,new Vector3(data.outline[i].x,centre.y,data.outline[i].y),ring/(float)rings);
            var triangles=new List<int>();
            for(int i=0;i<n;i++){triangles.Add(0);triangles.Add(1+(i+1)%n);triangles.Add(1+i);}
            for(int ring=1;ring<rings;ring++)for(int i=0;i<n;i++)
            {
                int a=1+(ring-1)*n+i,b=1+(ring-1)*n+(i+1)%n,c=a+n,d=b+n;
                triangles.Add(a);triangles.Add(b);triangles.Add(c);triangles.Add(b);triangles.Add(d);triangles.Add(c);
            }
            var up=local.MultiplyVector(Vector3.up).normalized;var right=local.MultiplyVector(Vector3.right).normalized;
            for(int i=0;i<vertices.Length;i++)
            {
                uv[i]=new Vector2(vertices[i].x-data.centre.x,vertices[i].z-data.centre.z)/repeat;
                vertices[i]=local.MultiplyPoint3x4(vertices[i]);normals[i]=up;tangents[i]=new Vector4(right.x,right.y,right.z,-1);
            }
            var mesh=new LTRoadMesh.Chunk{vertices=vertices,normals=normals,tangents=tangents,uv=uv,triangles=triangles.ToArray()};
            mesh.hash=LTRoadMesh.ContentHash(mesh);return mesh;
        }
#if !ROAD_MATH_CHECKS
        public static void ValidateRoadEnds(LTRoad road,LTRoadModuleSource.Prepared prepared,LTRoadJunction collectNode=null,List<Vector3> boundary=null)
        {
            void Check(LTRoadJunction node,bool start)
            {
                if(!node||!node.isActiveAndEnabled)return;
                if(node.surface!=LTRoadMode.Asphalt)throw new ArgumentException("Асфальтовому въезду нужен узел Asphalt.");
                var port=node.Port(road,start);float y=port.centre.y+node.surfaceOffset;
                if(Mathf.Abs(road.surfaceOffset-node.surfaceOffset)>.0001f)throw new ArgumentException("Surface Offset дороги и перекрёстка должен совпадать.");
                var outward=new Vector3(-port.right.z,0,port.right.x);
                var matrix=node.World.transform.worldToLocalMatrix*road.transform.localToWorldMatrix;
                foreach(var level in prepared.levels)
                {
                    var mesh=level[start?0:level.Count-1];float min=float.PositiveInfinity,max=float.NegativeInfinity;int count=0;
                    foreach(var v in mesh.vertices)
                    {
                        var p=matrix.MultiplyPoint3x4(v);var delta=p-port.centre;
                        if(Mathf.Abs(Vector3.Dot(delta,outward))>.003f)continue;
                        if(Mathf.Abs(p.y-y)>.003f)throw new ArgumentException("Торец модуля у перекрёстка должен быть плоским, без толщины/бордюров. Используйте отдельную дорогу-переходник с плоским торцом.");
                        float lateral=Vector3.Dot(delta,port.right);min=Math.Min(min,lateral);max=Math.Max(max,lateral);count++;
                        if(node==collectNode&&boundary!=null)boundary.Add(p);
                    }
                    if(count<2||Mathf.Abs(min+road.width*.5f)>.003f||Mathf.Abs(max-road.width*.5f)>.003f)
                        throw new ArgumentException("Торец модуля/LOD не совпадает с шириной въезда. Включите подгонку ширины и проверьте торцы всех LOD.");
                }
            }
            Check(road.startJunction,true);Check(road.endJunction,false);
        }
#endif
    }
}
