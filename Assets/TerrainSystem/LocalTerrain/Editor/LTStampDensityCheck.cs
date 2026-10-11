using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public static class LTStampDensityCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Stamp Density Mask (no scene changes)
        public static void Run()
        {
            // Only an owned, unsaved texture is created. No scene objects,
            // imported textures, render settings or generated assets are edited.
            Texture2D texture=null;
            try
            {
                texture=new Texture2D(64,64,TextureFormat.RGBAFloat,false,true){hideFlags=HideFlags.HideAndDontSave};
                var pixels=new Color[64*64];pixels[21*64+13]=Color.white;
                texture.SetPixels(pixels);texture.Apply(false,false);
                var mask=LTStampDensityMask.Capture(texture,false,new Vector2(0,1),1);
                Require(mask.Maximum(new Rect(0,0,1,1))==1,"Native read lost the bright texel.");
                Require(mask.Sample(13.5f/64,21.5f/64)==1,"Texture row/UV orientation mismatch.");
                Require(mask.Sample(.5f,.5f)==0,"Black texels must stay coarse.");
                var inverse=Matrix4x4.Translate(new Vector3(-4,0,-4));var region=new Rect(0,0,8,8);
                var zone=mask.CreateZone(region,inverse,new Vector2(8,8),LTStampShape.Rectangle,.125f,2);
                var plan=LTStampMesh.Plan(region,4,true,.1f,100000,new List<LTStampMesh.Zone>{zone},(x,z)=>0,new LTLODMesh.BuildProgress(false));
                Require(plan.Count>16&&plan.Count<4096,"Expected local, not missing or uniform, mask refinement.");
                var inverted=LTStampDensityMask.Capture(texture,true,new Vector2(0,1),1);
                Require(inverted.Sample(.5f,.5f)==1&&inverted.Sample(13.5f/64,21.5f/64)==0,"Native invert mismatch.");
                pixels[21*64+13]=new Color(.5f,0,0,1);texture.SetPixels(pixels);texture.Apply(false,false);
                var remapped=LTStampDensityMask.Capture(texture,false,new Vector2(.25f,.75f),2);
                Require(Mathf.Abs(remapped.Sample(13.5f/64,21.5f/64)-.25f)<1e-6f,"Native remap mismatch.");
                Require(mask.Sample(13.5f/64,21.5f/64)==1,"Captured snapshot changed after source overwrite.");
                texture.Apply(false,true);bool rejected=false;
                try{LTStampDensityMask.Capture(texture,false,new Vector2(0,1),1);}catch(InvalidOperationException){rejected=true;}
                Require(rejected,"Unreadable source was accepted.");
                Debug.Log($"Stamp Density Mask PASS: native texture read, UVs, invert/remap, immutable snapshot, unreadable guard, local refinement ({plan.Count} cells). No scene/assets/settings changed.");
            }
            finally{if(texture)UnityEngine.Object.DestroyImmediate(texture);}
        }
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Stamp Density Mask FAIL: "+message);}
    }
}
