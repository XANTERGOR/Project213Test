using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTStampPreviewCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Stamp Preview (no scene changes)
        public static void Run()
        {
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(7, 5, TextureFormat.RGBAFloat, false, true) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Repeat };
                var pixels = new Color[35];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color((i % 9) / 8f, 0, 0, 1);
                texture.SetPixels(pixels); texture.Apply(false, false);
                var range = new Vector2(.2f, .8f);
                var reference = LTStampDensityMask.Capture(texture, true, range, 2);
                for (int y = 0; y <= 20; y++) for (int x = 0; x <= 20; x++)
                {
                    var uv = new Vector2(x / 20f, y / 20f);
                    float sample = LTStampPreviewMath.DensitySample(texture.width, texture.height, (a, b) => texture.GetPixel(a, b).r, uv, true, range, 2);
                    Require(Mathf.Abs(sample - reference.Sample(uv.x, uv.y)) < 2e-6f, "Native mask sampling/remap differs.");
                }
                Debug.Log("Stamp Preview native sampling PASS: 441 samples, red channel, clamp, invert/range/power before bilinear. No scene/assets/settings changed.");
                CheckGraphics();
            }
            finally { if (texture) Object.DestroyImmediate(texture); }
        }

        static void CheckGraphics()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("Stamp Preview graphics NOT TESTED: needs a graphics device, not -nographics.");
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(LTStampPreview.ShaderPath);
            Require(shader && shader.isSupported && !ShaderUtil.ShaderHasError(shader), "Preview shader failed import/support check.");
            Texture2D mask = null, readback = null; Material material = null; Mesh mesh = null; RenderTexture rt = null;
            var previous = RenderTexture.active;
            bool previousWireframe = GL.wireframe, previousSrgb = GL.sRGBWrite;
            bool previousAsync = ShaderUtil.allowAsyncCompilation;
            try
            {
                GL.wireframe = false; GL.sRGBWrite = false; ShaderUtil.allowAsyncCompilation = false;
                mask = new Texture2D(2, 1, TextureFormat.RGBAFloat, false, true) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                mask.SetPixels(new[] { Color.black, Color.white }); mask.Apply(false, false);
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                material.SetTexture("_PreviewMask", mask); material.SetMatrix("_MeshToStamp", Matrix4x4.identity);
                material.SetVector("_StampSize", new Vector4(2, 2, 0, 0)); material.SetFloat("_Opacity", .8f);
                material.SetFloat("_Mode", 1); material.SetFloat("_Shape", 1);
                // A ring: the overlay must draw only real triangles, never fill its hole.
                mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                mesh.vertices = new[] { new Vector3(-1,0,-1), new Vector3(-1,0,1), new Vector3(1,0,1), new Vector3(1,0,-1),
                    new Vector3(-.2f,0,-.2f), new Vector3(-.2f,0,.2f), new Vector3(.2f,0,.2f), new Vector3(.2f,0,-.2f) };
                var indices = new int[24];
                for (int i = 0; i < 4; i++)
                { int j = (i + 1) % 4, k = i * 6; indices[k] = i; indices[k+1] = j; indices[k+2] = j+4; indices[k+3] = i; indices[k+4] = j+4; indices[k+5] = i+4; }
                mesh.triangles = indices;
                rt = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { hideFlags = HideFlags.HideAndDontSave };
                rt.Create(); Require(rt.IsCreated(), "Could not create graphics check target.");
                readback = new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true) { hideFlags = HideFlags.HideAndDontSave };
                var cameraToWorld = Matrix4x4.TRS(new Vector3(0, 2, 0), Quaternion.LookRotation(Vector3.down, Vector3.forward), Vector3.one);
                var view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * cameraToWorld.inverse;
                void Render()
                {
                    RenderTexture.active = rt;
                    GL.PushMatrix();
                    try
                    {
                        GL.LoadProjectionMatrix(Matrix4x4.Ortho(-1, 1, -1, 1, .1f, 10)); GL.modelview = view;
                        GL.Clear(true, true, Color.clear);
                        Require(material.SetPass(0), "Preview material pass failed.");
                        Graphics.DrawMeshNow(mesh, Matrix4x4.identity, 0);
                    }
                    finally { GL.PopMatrix(); }
                    readback.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); readback.Apply(false, false);
                }
                Render();
                Require(readback.GetPixel(32, 32).a < .001f, "Preview filled a real mesh hole.");
                var left = readback.GetPixel(4, 32); var right = readback.GetPixel(59, 32);
                Require(left.a > .1f && right.a > .1f && left.b > left.r && right.r > right.b, "Mask heatmap/UV direction did not render.");
                material.SetFloat("_Shape", 0); Render();
                Require(readback.GetPixel(2, 2).a < .001f && readback.GetPixel(32, 55).a > .1f, "Ellipse footprint clipping failed.");
                material.SetFloat("_Mode", 2); material.SetFloat("_Edge", .5f); material.SetFloat("_Strength", 1); material.SetFloat("_MaskIsHeight", 1); Render();
                left = readback.GetPixel(16, 32); right = readback.GetPixel(47, 32);
                Require(Mathf.Abs(left.r - right.r) < .005f && Mathf.Abs(left.g - right.g) < .005f, "Height mask incorrectly changed influence.");
                material.SetFloat("_MaskIsHeight", 0); Render();
                left = readback.GetPixel(16, 32); right = readback.GetPixel(47, 32);
                Require(left.b > left.r && right.r > right.b, "Influence mask was not applied.");
                material.SetFloat("_Mode", 3); material.SetFloat("_MaskDensity", 0); material.SetFloat("_Strength", 0); Render();
                Require(readback.GetPixel(16, 32).g > readback.GetPixel(4, 32).g + .1f, "Shape density or strength independence failed.");
                material.SetFloat("_MaskDensity", 1); Render();
                Require(readback.GetPixel(4, 32).b > readback.GetPixel(4, 32).r && readback.GetPixel(59, 32).r > readback.GetPixel(59, 32).b, "Mask density did not render.");
                Require(!ShaderUtil.ShaderHasError(shader), "Shader reported an error after graphics draw.");
                Debug.Log("Stamp Preview graphics PASS: DrawMeshNow/readback, mask UVs, height/influence distinction, shape/mask density, ellipse clipping, real hole preserved. Uses only owned temporary GPU resources; live Scene View still needs a visual check.");
            }
            finally
            {
                RenderTexture.active = previous; GL.wireframe = previousWireframe; GL.sRGBWrite = previousSrgb; ShaderUtil.allowAsyncCompilation = previousAsync;
                if (rt) { rt.Release(); Object.DestroyImmediate(rt); }
                if (readback) Object.DestroyImmediate(readback);
                if (mesh) Object.DestroyImmediate(mesh);
                if (material) Object.DestroyImmediate(material);
                if (mask) Object.DestroyImmediate(mask);
            }
        }
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Stamp Preview FAIL: " + message); }
    }
}
