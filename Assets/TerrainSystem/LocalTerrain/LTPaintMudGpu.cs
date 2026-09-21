using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTPaintRuntime
    {
        [StructLayout(LayoutKind.Sequential)]
        struct GpuContact
        {
            public int index;
            public float dose,limit,recovery,duration;
        }
        sealed class GpuMud : IDisposable
        {
            public RenderTexture depth,state;
            public readonly Dictionary<int,GpuContact> contacts=new Dictionary<int,GpuContact>();
            // Conservative tile lifetime; no synchronous GPU readback.
            public readonly Dictionary<int,double> tiles=new Dictionary<int,double>();
            public readonly List<int> expired=new List<int>();
            public readonly List<GpuContact> contactData=new List<GpuContact>();
            public readonly List<int> tileData=new List<int>();
            public ComputeBuffer contactBuffer,tileBuffer;
            public double updated;
            public void Dispose()
            {
                contactBuffer?.Release();tileBuffer?.Release();
                if(depth){depth.Release();DestroyOwned(depth);}
                if(state){state.Release();DestroyOwned(state);}
            }
        }
        ComputeShader mudCompute;
        bool mudGpuFailed;
        static readonly Unity.Profiling.ProfilerMarker GpuMudMarker=new Unity.Profiling.ProfilerMarker("LT.Mud.GpuDispatch");
        void PredictMud(LTWorld world,Bounds bounds,Vector3 velocity,float seconds,float tolerance)
        {
            var localCenter=world.transform.InverseTransformPoint(bounds.center);
            if(!deformationTerrain.Sample(localCenter.x,localCenter.z,out float h,out _))return;
            float surfaceY=world.transform.TransformPoint(new Vector3(localCenter.x,h,localCenter.z)).y;
            if(bounds.min.y>surfaceY+tolerance||bounds.max.y<surfaceY-tolerance)return;
            var offset=LTDeformationMath.Prediction(velocity,seconds);
            var inverse=world.transform.worldToLocalMatrix;
            Vector3 low=new Vector3(float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity),high=-low;
            for(int i=0;i<8;i++)
            {
                var corner=bounds.center+Vector3.Scale(bounds.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1));
                var a=inverse.MultiplyPoint3x4(corner);var b=inverse.MultiplyPoint3x4(corner+offset);
                low=Vector3.Min(low,Vector3.Min(a,b));high=Vector3.Max(high,Vector3.Max(a,b));
            }
            predictedMudAreas.Add(Rect.MinMaxRect(low.x-.1f,low.z-.1f,high.x+.1f,high.z+.1f));
        }
        int mudInit,mudContact,mudRecover;
        bool ResolveMudCompute()
        {
            if(mudGpuFailed||!SystemInfo.supportsComputeShaders||
                !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RFloat)||
                !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RGFloat))return false;
            if(!mudCompute)mudCompute=Resources.Load<ComputeShader>("LTMudSimulation");
            if(!mudCompute)return false;
            if(!mudCompute.HasKernel("Initialize")||!mudCompute.HasKernel("Contact")||!mudCompute.HasKernel("Recover"))return false;
            mudInit=mudCompute.FindKernel("Initialize");mudContact=mudCompute.FindKernel("Contact");
            mudRecover=mudCompute.FindKernel("Recover");return true;
        }
        static RenderTexture MudTexture(int width,int height,RenderTextureFormat format,string name)
        {
            var rt=new RenderTexture(width,height,0,format,RenderTextureReadWrite.Linear)
            {
                name=name,enableRandomWrite=true,useMipMap=false,autoGenerateMips=false,
                filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp,
                hideFlags=HideFlags.HideAndDontSave
            };
            if(!rt.Create()){DestroyOwned(rt);throw new InvalidOperationException("Mud RenderTexture allocation failed");}
            return rt;
        }
        void SetMudKernel(DeformationState d,int kernel,float now)
        {
            mudCompute.SetInt("_Width",d.size);mudCompute.SetInt("_Height",d.rows);
            mudCompute.SetInt("_TileColumns",(d.size+ActivityBlock-1)/ActivityBlock);
            mudCompute.SetFloat("_Now",now);
            mudCompute.SetTexture(kernel,"_Depth",d.gpu.depth);mudCompute.SetTexture(kernel,"_State",d.gpu.state);
        }
        void AllocateGpuMud(DeformationState d,double now)
        {
            d.gpu=new GpuMud{updated=now};
            try
            {
                d.gpu.depth=MudTexture(d.size,d.rows,RenderTextureFormat.RFloat,"Mud GPU depth");
                d.gpu.state=MudTexture(d.size,d.rows,RenderTextureFormat.RGFloat,"Mud GPU recovery/time");
                d.depthMap=d.gpu.depth;
                SetMudKernel(d,mudInit,(float)(now-gpuEpoch));
                mudCompute.Dispatch(mudInit,(d.size+7)/8,(d.rows+7)/8,1);
            }
            catch{ReleaseDepth(d);throw;}
            deformationBindingsDirty=true;
        }
        bool AddGpuContact(DeformationState d,int index,Vector4 control,float dose,float duration,double now)
        {
            if(d.gpu==null)
            {
                try{AllocateGpuMud(d,now);}
                catch(Exception e)
                {
                    mudGpuFailed=true;
                    Debug.LogWarning("Mud GPU allocation failed; switching to CPU and resetting tracks: "+e.Message);
                    return false;
                }
            }
            d.gpu.contacts.TryGetValue(index,out var c);
            c.index=index;c.dose+=dose;c.limit=control.x;c.recovery=control.y;c.duration+=duration;
            d.gpu.contacts[index]=c;
            int tile=LTDeformationMath.BlockIndex(index%d.size,index/d.size,d.size,ActivityBlock);
            double until=control.y>0?now+control.x/control.y:double.PositiveInfinity;
            d.gpu.tiles.TryGetValue(tile,out double old);
            d.gpu.tiles[tile]=Math.Max(old,until);
            return true;
        }
        static void EnsureMudBuffer(ref ComputeBuffer buffer,int count,int stride)
        {
            if(buffer!=null&&buffer.count>=count)return;
            buffer?.Release();buffer=new ComputeBuffer(Mathf.NextPowerOfTwo(Mathf.Max(1,count)),stride);
        }
        void TickGpuMud(LTWorld world,DeformationState d,double now,float dt)
        {
            using var profiling=GpuMudMarker.Auto();
            var g=d.gpu;if(g==null)return;
            bool contact=g.contacts.Count>0;
            if(!contact&&now-g.updated<MudUpdateInterval(world,d))return;
            SetMudKernel(d,mudContact,(float)(now-gpuEpoch));
            if(contact)
            {
                g.contactData.Clear();
                foreach(var pair in g.contacts)
                {
                    var c=pair.Value;c.duration=Mathf.Min(dt,c.duration);g.contactData.Add(c);
                }
                EnsureMudBuffer(ref g.contactBuffer,g.contactData.Count,20);
                g.contactBuffer.SetData(g.contactData);
                mudCompute.SetInt("_Count",g.contactData.Count);
                mudCompute.SetBuffer(mudContact,"_Contacts",g.contactBuffer);
                int groups=(g.contactData.Count+63)/64;
                mudCompute.Dispatch(mudContact,Mathf.Min(32768,groups),(groups+32767)/32768,1);
                g.contacts.Clear();
            }
            // Contact first: touched texels now have time=now and do not recover.
            g.tileData.Clear();g.expired.Clear();
            foreach(var pair in g.tiles)
            {
                g.tileData.Add(pair.Key);
                if(pair.Value<=now)g.expired.Add(pair.Key);
            }
            if(g.tileData.Count>0)
            {
                EnsureMudBuffer(ref g.tileBuffer,g.tileData.Count,4);
                g.tileBuffer.SetData(g.tileData);
                SetMudKernel(d,mudRecover,(float)(now-gpuEpoch));
                mudCompute.SetInt("_Count",g.tileData.Count);
                mudCompute.SetBuffer(mudRecover,"_Tiles",g.tileBuffer);
                mudCompute.Dispatch(mudRecover,Mathf.Min(32768,g.tileData.Count),(g.tileData.Count+32767)/32768,1);
            }
            foreach(int tile in g.expired)g.tiles.Remove(tile);
            g.updated=now;
            if(g.tiles.Count==0){ReleaseDepth(d);deformationBindingsDirty=true;}
        }
        static float MudUpdateInterval(LTWorld world,DeformationState d)
        {
            Camera camera=Camera.main;
#if UNITY_EDITOR
            if(!Application.isPlaying&&UnityEditor.SceneView.lastActiveSceneView)
                camera=UnityEditor.SceneView.lastActiveSceneView.camera;
#endif
            if(!camera)return 1f/30;
            var local=world.transform.InverseTransformPoint(camera.transform.position);
            var nearest=new Vector3(Mathf.Clamp(local.x,d.rect.xMin,d.rect.xMax),local.y,
                Mathf.Clamp(local.z,d.rect.yMin,d.rect.yMax));
            float distance=Vector3.Distance(camera.transform.position,world.transform.TransformPoint(nearest));
            return LTDeformationMath.UpdateInterval(distance,world.mudNearDistance,world.mudFarDistance);
        }
    }
}
