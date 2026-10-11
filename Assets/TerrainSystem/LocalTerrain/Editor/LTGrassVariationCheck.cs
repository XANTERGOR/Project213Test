using System;
using UnityEngine;
using W=LocalTerrainPrototype.LTGrassRootTintWindow;

namespace LocalTerrainPrototype
{
    // Called by Validate Grass Color. Only transient materials/RTs from the caller.
    internal static class LTGrassVariationCheck
    {
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        internal static void Properties(Material defaults,Material live,Material indirect)
        {
            Require(W.SupportsVariation(defaults)&&W.SupportsVariation(indirect),"Variation properties missing; wait for graph/adapter import.");
            Require(defaults.GetFloat(W.Clump)==0&&defaults.GetFloat(W.Patch)==0,"Legacy variation must be disabled.");
            Require(defaults.GetVector(LTDetailPrefab.Part.GrassRootProperty)==Vector4.zero,"Standalone mesh pivot fallback must be zero.");
            live.SetColor(W.TintA,new Color(.8f,.9f,.7f));live.SetColor(W.TintB,new Color(.4f,.3f,.2f));
            live.SetFloat(W.Clump,.23f);live.SetFloat(W.Patch,.57f);live.SetFloat(W.PatchSize,17);live.SetFloat(W.Seed,123);
            indirect.CopyPropertiesFromMaterial(live);
            foreach(string p in new[]{W.Clump,W.Patch,W.PatchSize,W.Seed})Require(live.GetFloat(p)==indirect.GetFloat(p),"Indirect copy lost "+p);
            foreach(string p in new[]{W.TintA,W.TintB})Require(live.GetColor(p)==indirect.GetColor(p),"Indirect copy lost "+p);
        }
        static float Hash(int x,int z,uint seed)
        {
            unchecked
            {
                uint h=(uint)x*1597334677u^(uint)z*3812015801u^seed*2798796415u;
                h=(h^(h>>16))*2246822519u;h=(h^(h>>13))*3266489917u;h^=h>>16;
                return (h&16777215u)/16777215f;
            }
        }
        static float Noise(float x,float z,uint seed)
        {
            int ix=Mathf.FloorToInt(x),iz=Mathf.FloorToInt(z);float u=x-ix,v=z-iz;
            u=u*u*(3-2*u);v=v*v*(3-2*v);
            return Mathf.Lerp(Mathf.Lerp(Hash(ix,iz,seed),Hash(ix+1,iz,seed),u),
                Mathf.Lerp(Hash(ix,iz+1,seed),Hash(ix+1,iz+1,seed),u),v);
        }
        static Color Expected(Vector3 root,Color a,Color b,float clump,float patch,float size,float rawSeed)
        {
            clump=Mathf.Clamp01(clump);patch=Mathf.Clamp01(patch);uint seed=(uint)Mathf.Clamp(Mathf.Floor(rawSeed+.5f),0,65535);
            float sum=clump+patch;
            float blend=(Noise(root.x*8,root.z*8,seed)*clump+Noise(root.x/Mathf.Max(size,.1f),root.z/Mathf.Max(size,.1f),seed^1867u)*patch)/Mathf.Max(sum,.0001f);
            return Color.Lerp(Color.white,Color.Lerp(a,b,blend),Mathf.Clamp01(sum));
        }
        internal static int Pixels(Material probe,RenderTexture target,Texture2D read)
        {
            int count=0;var albedo=new Color(.7f,.45f,.18f,.37f);
            probe.SetFloat("_TestVariation",1);probe.SetVector("_TestBaseColor",albedo);
            probe.SetFloat("_TestStrength",0); // Test variation independently, then combined below.
            Color Pixel(Vector3 root,float clump,float patch,float size,float seed,Color a,Color b,bool roots=false)
            {
                probe.SetVector("_TestRoot",root);probe.SetVector("_TestTintA",a);probe.SetVector("_TestTintB",b);
                probe.SetFloat("_TestClump",clump);probe.SetFloat("_TestPatch",patch);probe.SetFloat("_TestSize",size);probe.SetFloat("_TestSeed",seed);
                probe.SetFloat("_TestStrength",roots?.65f:0);probe.SetFloat("_TestRestHeight",0);probe.SetFloat("_TestBase",0);probe.SetFloat("_TestHeight",.5f);
                var rootTint=new Color(.3f,.2f,.1f,1);probe.SetVector("_TestTint",rootTint);
                GL.sRGBWrite=false;Graphics.Blit(Texture2D.whiteTexture,target,probe,0);
                RenderTexture.active=target;read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();
                var expected=albedo*Expected(root,a,b,clump,patch,size,seed);
                if(roots)expected*=Color.Lerp(Color.white,rootTint,.65f);
                expected.a=albedo.a;var actual=read.GetPixel(0,0);
                Require(Mathf.Abs(expected.r-actual.r)<.0005f&&Mathf.Abs(expected.g-actual.g)<.0005f&&
                    Mathf.Abs(expected.b-actual.b)<.0005f&&Mathf.Abs(expected.a-actual.a)<.0005f,
                    $"Variation pixel mismatch: root={root}, strengths={clump}/{patch}, size={size}, seed={seed}: {actual} != {expected}");
                count++;return actual;
            }
            var tintA=new Color(.95f,.9f,.7f,1);var tintB=new Color(.4f,.5f,.3f,1);
            foreach(var root in new[]{Vector3.zero,new Vector3(-.125f,3,-7.25f),new Vector3(12.345f,0,89.23f),new Vector3(-2048.25f,40,4096.5f)})
            foreach(float seed in new[]{0,17,65535})
            {
                Pixel(root,0,0,12,seed,tintA,tintB);Pixel(root,.2f,.35f,12,seed,tintA,tintB);
                Pixel(root,1,0,12,seed,tintA,tintB);Pixel(root,0,1,12,seed,tintA,tintB);
                Pixel(root,1,1,12,seed,Color.white,Color.white);Pixel(root,.2f,.35f,12,seed,tintA,tintB,true);
            }
            foreach(float size in new[]{-1,0,.001f,.1f,100})Pixel(new Vector3(-1.75f,0,2.25f),.2f,.4f,size,7,tintA,tintB);
            Pixel(Vector3.one,-1,2,12,-2,tintA,tintB);Pixel(Vector3.one,1,1,12,100000,tintA,tintB);
            var stable=Pixel(new Vector3(2.1f,0,-4.2f),.5f,.5f,12,17,tintA,tintB);
            var elevation=Pixel(new Vector3(2.1f,100,-4.2f),.5f,.5f,12,17,tintA,tintB);
            Require(stable==elevation,"XZ variation must ignore root elevation.");
            probe.SetFloat("_TestVariation",0);return count;
        }
        internal static void PlacementRoots()
        {
            foreach(float mirror in new[]{-1f,1f})
            {
                var placement=Matrix4x4.TRS(new Vector3(153.7f,27.2f,-38.4f),Quaternion.Euler(17,38,4),new Vector3(mirror*1.4f,.8f,1.1f));
                foreach(var part in new[]{Matrix4x4.identity,Matrix4x4.TRS(new Vector3(2,1,-3),Quaternion.Euler(32,11,8),new Vector3(.7f,2,1.2f))})
                {
                    var offset=part.inverse.MultiplyPoint3x4(Vector3.zero);var draw=placement*part;
                    // Object.Position + 3x3 matrix * offset, as in the shader.
                    var actual=draw.MultiplyPoint3x4(Vector3.zero)+draw.MultiplyVector(offset);
                    Require((actual-placement.MultiplyPoint3x4(Vector3.zero)).sqrMagnitude<1e-8f,"LOD/child placement root changed.");
                }
            }
        }
    }
}
