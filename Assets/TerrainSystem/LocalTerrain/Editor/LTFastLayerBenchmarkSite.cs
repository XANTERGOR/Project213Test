using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal sealed class LTFastLayerBenchmarkView
    {
        readonly SceneView view;
        readonly Vector3 pivot;
        readonly Quaternion rotation,last3DRotation;
        readonly float size;
        readonly bool ortho,in2D,rotationLocked;
        public LTFastLayerBenchmarkView(SceneView view)
        {
            this.view=view;pivot=view.pivot;rotation=view.rotation;size=view.size;
            ortho=view.orthographic;in2D=view.in2DMode;rotationLocked=view.isRotationLocked;last3DRotation=view.lastSceneViewRotation;
        }
        public void Frame(Vector3 focus,Quaternion direction,float zoom)
        {view.isRotationLocked=false;view.in2DMode=false;view.LookAt(focus,direction,zoom,true,true);view.Repaint();}
        public bool Restore()
        {
            if(!view)return false;
            view.in2DMode=in2D;view.LookAt(pivot,rotation,size,ortho,true);
            view.lastSceneViewRotation=last3DRotation;view.isRotationLocked=rotationLocked;view.Repaint();
            return Vector3.Distance(view.pivot,pivot)<.0001f&&Quaternion.Angle(view.rotation,rotation)<.01f&&
                Mathf.Abs(view.size-size)<.0001f&&view.orthographic==ortho&&view.in2DMode==in2D;
        }
    }
    // Editor-only, read-only snapshots. Search coverage maps, then validate the
    // actual rendered mesh (not a potentially stale or distance-disabled collider).
    internal sealed class LTFastLayerBenchmarkSite
    {
        internal sealed class Surface
        {
            public LTFastLayerBenchmarkChunk chunk;
            public Mesh mesh;
            public Matrix4x4 localToWorld,worldToLocal,meshMatrix;
            public double revision;
            public Rect rect;
            public int width,height;
            public Color32[][] pixels;
            public LTPaintWeightTerrainCache.Source geometry;
            readonly float[] weights=new float[12];
            public bool GeometryCurrent=>chunk.chunk&&chunk.renderer&&chunk.chunk.updatedAt==revision&&
                chunk.renderer.GetComponent<MeshFilter>()?.sharedMesh==mesh&&
                chunk.liveMaterial.GetMatrix("_LTSurfaceWorldToLocal")==worldToLocal&&
                worldToLocal*chunk.renderer.localToWorldMatrix==meshMatrix;
            public int Count(float u,float v)
            {
                // Same endpoint-to-texel-centre mapping as LTReadLayerCoverage.
                u=(Mathf.Clamp01(u)*(width-1)+.5f)/width;
                v=(Mathf.Clamp01(v)*(height-1)+.5f)/height;
                for(int map=0;map<3;map++)
                {
                    Color c=LTPaintMath.SampleGpuBilinear(pixels[map],width,height,u,v,false);
                    for(int channel=0;channel<4;channel++)weights[map*4+channel]=c[channel];
                }
                return LTFastLayerBenchmarkPolicy.LayerCount(weights);
            }
            public bool Sample(float x,float z,out Vector3 point)
            {
                point=default;
                if(x<rect.xMin||x>rect.xMax||z<rect.yMin||z>rect.yMax)return false;
                if(geometry==null)
                {
                    var vertices=mesh.vertices;
                    for(int i=0;i<vertices.Length;i++)vertices[i]=meshMatrix.MultiplyPoint3x4(vertices[i]);
                    geometry=LTPaintWeightTerrainCache.BuildSource(rect,vertices,Array.Empty<Vector3>(),mesh.triangles,null,out _,out _);
                }
                int bx=LTPaintMath.TerrainBin(x,rect.xMin,rect.width,geometry.axis);
                int bz=LTPaintMath.TerrainBin(z,rect.yMin,rect.height,geometry.axis);
                var bin=geometry.bins[bz*geometry.axis+bx];
                if(bin==null)return false;
                float top=float.NegativeInfinity;
                foreach(int t in bin)
                {
                    Vector3 a=geometry.vertices[geometry.indices[t]],b=geometry.vertices[geometry.indices[t+1]],c=geometry.vertices[geometry.indices[t+2]];
                    if(Vector3.Cross(b-a,c-a).y<=0||!LTPaintMath.TriangleWeights(new Vector2(x,z),a,b,c,out var w))continue;
                    top=Mathf.Max(top,w.x*a.y+w.y*b.y+w.z*c.y);
                }
                if(float.IsNegativeInfinity(top))return false;
                point=new Vector3(x,top,z);return true;
            }
        }
        public Surface surface;
        public Vector2 center;
        public Vector2 span;
        public int minimumLayers;
        public Vector3 focus;
        public float size;

        public static string Exclusion(LTFastLayerBenchmarkChunk item,Camera camera)
        {
            if(item.exclusion!=null)return item.exclusion;
            var r=item.renderer;var filter=r?r.GetComponent<MeshFilter>():null;var mesh=filter?filter.sharedMesh:null;
            if(!r||!r.enabled||!r.gameObject.activeInHierarchy||r.forceRenderingOff||(camera.cullingMask&(1<<r.gameObject.layer))==0)return "hidden";
            if(!mesh||mesh.subMeshCount!=1||r.sharedMaterials.Length!=1)return "unsupported mesh/material layout";
            if(r.HasPropertyBlock())return "property block";
            if(!mesh.isReadable)return "mesh not readable";
            return null;
        }
        public static List<LTFastLayerBenchmarkSite> Find(LTFastLayerBenchmarkChunk[] chunks,Camera camera,out int maximumLayers,out string exclusions)
        {
            maximumLayers=0;var sites=new List<LTFastLayerBenchmarkSite>();var rejected=new List<string>();
            foreach(var chunk in chunks.OrderBy(c=>c.chunk?c.chunk.x:0).ThenBy(c=>c.chunk?c.chunk.z:0))
            {
                string reason=Exclusion(chunk,camera);
                if(reason!=null){rejected.Add(reason);continue;}
                var material=chunk.liveMaterial;
                var maps=Enumerable.Range(0,3).Select(i=>material.GetTexture("_LTWeights"+i) as Texture2D).ToArray();
                if(maps.Any(m=>!m||!m.isReadable||m.width<2||m.height<2)||maps.Any(m=>m.width!=maps[0].width||m.height!=maps[0].height))
                {rejected.Add("weight maps unavailable");continue;}
                var rect=material.GetVector("_LTRect");
                if(rect.z<=0||rect.w<=0){rejected.Add("invalid weight rectangle");continue;}
                var toLocal=material.GetMatrix("_LTSurfaceWorldToLocal");
                var surface=new Surface{chunk=chunk,mesh=chunk.renderer.GetComponent<MeshFilter>().sharedMesh,
                    localToWorld=toLocal.inverse,worldToLocal=toLocal,meshMatrix=toLocal*chunk.renderer.localToWorldMatrix,revision=chunk.chunk.updatedAt,
                    rect=new Rect(rect.x,rect.y,rect.z,rect.w),width=maps[0].width,height=maps[0].height,pixels=maps.Select(m=>m.GetPixels32(0)).ToArray()};
                var counts=new byte[surface.width*surface.height];
                for(int y=0;y<surface.height;y++)for(int x=0;x<surface.width;x++)
                {
                    int n=surface.Count(x/(surface.width-1f),y/(surface.height-1f));
                    counts[y*surface.width+x]=(byte)n;maximumLayers=Math.Max(maximumLayers,n);
                }
                foreach(var patch in LTFastLayerBenchmarkPolicy.FindPatches(counts,surface.width,surface.height))
                {
                    var site=new LTFastLayerBenchmarkSite{surface=surface,minimumLayers=patch.layers,
                        center=new Vector2(rect.x+(patch.x+(patch.size-1f)*.5f)/(surface.width-1)*rect.z,rect.y+(patch.y+(patch.size-1f)*.5f)/(surface.height-1)*rect.w),
                        span=new Vector2((patch.size-1f)/(surface.width-1)*rect.z,(patch.size-1f)/(surface.height-1)*rect.w)};
                    float sx=surface.localToWorld.MultiplyVector(Vector3.right*site.span.x).magnitude;
                    float sz=surface.localToWorld.MultiplyVector(Vector3.forward*site.span.y).magnitude;
                    // A close overhead orthographic view fits inside the patch;
                    // the actual camera is checked again after SceneView updates.
                    site.size=Mathf.Min(10,Mathf.Min(sx,sz)*.35f);
                    if(site.size>=.25f)sites.Add(site);
                }
            }
            exclusions=string.Join("; ",rejected.GroupBy(r=>r).Select(g=>$"{g.Key}: {g.Count()}"));
            return sites.OrderByDescending(s=>s.size).ThenByDescending(s=>s.minimumLayers).ToList();
        }
        public bool Locate()
        {
            if(!surface.Sample(center.x,center.y,out var local))return false;
            focus=surface.localToWorld.MultiplyPoint3x4(local);return true;
        }
        public Quaternion Rotation=>Quaternion.LookRotation(-surface.localToWorld.MultiplyVector(Vector3.up),surface.localToWorld.MultiplyVector(Vector3.forward));
        public void Probe(Camera camera,out int ground,out int overlap,out int minimum,out int maximum)
        {
            ground=overlap=0;minimum=12;maximum=0;
            for(int y=0;y<21;y++)for(int x=0;x<21;x++)
            {
                // Include the frame edges; count only front-facing mesh hits
                // within the camera's clipping range, with real interpolated weights.
                var ray=camera.ViewportPointToRay(new Vector3(x/20f,y/20f,0));
                var origin=surface.worldToLocal.MultiplyPoint3x4(ray.origin);
                var direction=surface.worldToLocal.MultiplyVector(ray.direction).normalized;
                if(Mathf.Abs(direction.x)>.0001f||Mathf.Abs(direction.z)>.0001f||direction.y>=0)continue;
                if(!surface.Sample(origin.x,origin.z,out var hit))continue;
                float distance=Vector3.Dot(surface.localToWorld.MultiplyPoint3x4(hit)-ray.origin,ray.direction);
                if(distance<0||distance>camera.farClipPlane-camera.nearClipPlane)continue;
                ground++;
                int count=surface.Count((hit.x-surface.rect.xMin)/surface.rect.width,(hit.z-surface.rect.yMin)/surface.rect.height);
                minimum=Math.Min(minimum,count);maximum=Math.Max(maximum,count);
                if(count>=4&&count<=5)overlap++;
            }
            if(ground==0)minimum=0;
        }
    }
}
