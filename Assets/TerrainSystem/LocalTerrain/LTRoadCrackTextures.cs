using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    // Opt-in, world-owned crack detail atlas, independent of saved terrain layer arrays.
    // Null source is the built-in tileable test pattern. No source assets are modified.
    public sealed class LTRoadCrackTextures : IDisposable
    {
        public Texture2DArray Texture {get;private set;}
        public int Revision {get;private set;}
        readonly List<Texture2D> sources=new List<Texture2D>();
        string key;
        public int Slice(Texture2D source)=>sources.IndexOf(source);
        public bool Ensure(LTWorld world)
        {
            var wanted=new List<Texture2D>();
            foreach(var road in world.GetComponentsInChildren<LTRoad>())
                if(road.isActiveAndEnabled&&road.World==world&&LTRoadMath.IsTerrainAsphalt(road.mode)&&road.groundLayer&&
                    road.asphaltDamage.strength>0&&(road.asphaltDamage.cracks>0||
                    road.mode==LTRoadMode.GeometricAsphalt&&road.connectedDamage&&road.repairPatchStrength>0)&&!wanted.Contains(road.asphaltCrackMask))wanted.Add(road.asphaltCrackMask);
            if(wanted.Count==0){if(Texture){Dispose();Revision++;return true;}return false;}
            string next=string.Join("|",wanted.Select(t=>t?t.GetInstanceID()+":"+LTTextureContentHash.Get(t)+":"+t.updateCount:"builtin-v1"));
            if(next==key&&Texture)return false;
            if(wanted.Count>32)throw new InvalidOperationException("Terrain Asphalt: максимум 32 разных текстуры трещин на мир.");
            if(!SystemInfo.supports2DArrayTextures||
                (SystemInfo.copyTextureSupport&(CopyTextureSupport.DifferentTypes|CopyTextureSupport.RTToTexture))!=(CopyTextureSupport.DifferentTypes|CopyTextureSupport.RTToTexture))
                throw new InvalidOperationException("Terrain Asphalt: GPU texture arrays/copy not supported.");
            var shader=Resources.Load<Shader>("LTRoadCrackPack");
            if(!shader||!shader.isSupported)throw new InvalidOperationException("Terrain Asphalt: crack packing shader unavailable.");
            var previous=RenderTexture.active;bool srgb=GL.sRGBWrite;
            Texture2DArray built=null;RenderTexture rt=null;Material material=null;
            try
            {
                material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                built=new Texture2DArray(256,256,wanted.Count,TextureFormat.RGBA32,true,true)
                    {name="Asphalt crack detail (runtime)",hideFlags=HideFlags.HideAndDontSave,wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Trilinear,anisoLevel=4};
                built.Apply(false,true);
                rt=new RenderTexture(256,256,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear)
                    {hideFlags=HideFlags.HideAndDontSave,useMipMap=true,autoGenerateMips=false};
                if(!rt.Create()||rt.graphicsFormat!=built.graphicsFormat)throw new InvalidOperationException("Terrain Asphalt: texture allocation/format failed.");
                GL.sRGBWrite=false;
                for(int slice=0;slice<wanted.Count;slice++)
                {
                    material.SetFloat("_HasSource",wanted[slice]?1:0);
                    Graphics.Blit(wanted[slice]?wanted[slice]:Texture2D.blackTexture,rt,material,0);rt.GenerateMips();
                    for(int mip=0;mip<built.mipmapCount;mip++)Graphics.CopyTexture(rt,0,mip,built,slice,mip);
                }
                Destroy(Texture);Texture=built;built=null;sources.Clear();sources.AddRange(wanted);key=next;Revision++;return true;
            }
            finally{RenderTexture.active=previous;GL.sRGBWrite=srgb;if(rt)rt.Release();Destroy(rt);Destroy(material);Destroy(built);}
        }
        static void Destroy(UnityEngine.Object obj)
        {if(obj){if(Application.isPlaying)UnityEngine.Object.Destroy(obj);else UnityEngine.Object.DestroyImmediate(obj);}}
        public void Dispose(){Destroy(Texture);Texture=null;sources.Clear();key=null;}
    }
}
