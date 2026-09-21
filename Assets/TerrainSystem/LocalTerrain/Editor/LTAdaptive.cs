using System.Collections.Generic;
using UnityEngine;
namespace LocalTerrainPrototype
{
    public static class LTAdaptive
    {
        // Recursive rectangles; every boundary retains the original grid samples.
        // All adjacent leaves/chunks therefore share matching edge vertices, without T-junctions.
        sealed class Builder
        {
            public int n; public Vector3[] source, sourceNormals; public Vector2[] sourceUV; public float tolerance;
            public readonly List<Vector3> v=new List<Vector3>(), normals=new List<Vector3>();
            public readonly List<Vector2> uv=new List<Vector2>();public readonly List<int> tris=new List<int>();
            readonly Dictionary<Vector2Int,int> lookup=new Dictionary<Vector2Int,int>();
            Vector3 Sample(Vector3[] a,float x,float z)
            {
                x=Mathf.Clamp(x,0,n);z=Mathf.Clamp(z,0,n);
                int ix=Mathf.Min(Mathf.FloorToInt(x),n-1),iz=Mathf.Min(Mathf.FloorToInt(z),n-1);
                return Vector3.Lerp(Vector3.Lerp(a[iz*(n+1)+ix],a[iz*(n+1)+ix+1],x-ix),
                    Vector3.Lerp(a[(iz+1)*(n+1)+ix],a[(iz+1)*(n+1)+ix+1],x-ix),z-iz);
            }
            int Vertex(float x,float z)
            {
                var key=new Vector2Int(Mathf.RoundToInt(x*2),Mathf.RoundToInt(z*2));
                if(lookup.TryGetValue(key,out int id))return id;
                id=v.Count;lookup[key]=id;v.Add(Sample(source,x,z));normals.Add(Sample(sourceNormals,x,z).normalized);
                // Global UVs are affine, so corner interpolation is exact.
                uv.Add(Vector2.Lerp(Vector2.Lerp(sourceUV[0],sourceUV[n],x/n),
                    Vector2.Lerp(sourceUV[n*(n+1)],sourceUV[(n+1)*(n+1)-1],x/n),z/n));return id;
            }
            float Height(float x,float z)=>Sample(source,x,z).y;
            public void Patch(int x,int z,int w,int h)
            {
                if(w==1&&h==1)
                {
                    int a=Vertex(x,z),b=Vertex(x+1,z),c=Vertex(x,z+1),d=Vertex(x+1,z+1);
                    tris.AddRange(new[]{a,c,b,b,c,d});return;
                }
                float cx=x+w*.5f,cz=z+h*.5f,centre=Height(cx,cz);
                bool fits=true;
                for(int j=z;j<=z+h && fits;j++)for(int i=x;i<=x+w;i++)
                {
                    float dx=i-cx,dz=j-cz;
                    float t=Mathf.Max(Mathf.Abs(dx)/(w*.5f),Mathf.Abs(dz)/(h*.5f));
                    float predicted=t<1e-7f?centre:Mathf.Lerp(centre,Height(cx+dx/t,cz+dz/t),t);
                    if(Mathf.Abs(Height(i,j)-predicted)>tolerance){fits=false;break;}
                }
                if(!fits)
                {
                    int wx=w/2,hz=h/2;
                    if(w>1 && h>1){Patch(x,z,wx,hz);Patch(x+wx,z,w-wx,hz);Patch(x,z+hz,wx,h-hz);Patch(x+wx,z+hz,w-wx,h-hz);}
                    else if(w>1){Patch(x,z,wx,h);Patch(x+wx,z,w-wx,h);}
                    else {Patch(x,z,w,hz);Patch(x,z+hz,w,h-hz);}
                    return;
                }
                if(2*(w+h)>=2*w*h)
                {
                    for(int j=z;j<z+h;j++)for(int i=x;i<x+w;i++)
                    {
                        int a=Vertex(i,j),b=Vertex(i+1,j),c=Vertex(i,j+1),d=Vertex(i+1,j+1);
                        tris.AddRange(new[]{a,c,b,b,c,d});
                    }
                    return;
                }
                // Clockwise in XZ -> upward facing fan.
                var edge=new List<int>();
                for(int j=z;j<z+h;j++)edge.Add(Vertex(x,j));
                for(int i=x;i<x+w;i++)edge.Add(Vertex(i,z+h));
                for(int j=z+h;j>z;j--)edge.Add(Vertex(x+w,j));
                for(int i=x+w;i>x;i--)edge.Add(Vertex(i,z));
                int mid=Vertex(cx,cz);
                for(int i=0;i<edge.Count;i++){tris.Add(mid);tris.Add(edge[i]);tris.Add(edge[(i+1)%edge.Count]);}
            }
        }
        public static void Build(int n,Vector3[] input,Vector3[] normals,Vector2[] uv,float tolerance,
            out Vector3[] output,out Vector3[] outputNormals,out Vector2[] outputUV,out int[] indices)
        {
            var b=new Builder{n=n,source=input,sourceNormals=normals,sourceUV=uv,tolerance=tolerance};b.Patch(0,0,n,n);
            output=b.v.ToArray();outputNormals=b.normals.ToArray();outputUV=b.uv.ToArray();indices=b.tris.ToArray();
        }
    }
}
