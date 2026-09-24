# HDRP shader sources

`../Resources/LTEightLayers.shader` and `LTEightLayerData.hlsl` are modified
copies of Lit.shader and LitData.hlsl from Unity HDRP 17.3.0.
`LTEightLayerProperties.hlsl` is a modified copy of LitProperties.hlsl from the same package.
`../Resources/LTEightLayersTessellation.shader` is a modified copy of LitTessellation.shader
from that package, with custom terrain layer/domain displacement and differential normals.

com.unity.render-pipelines.high-definition copyright © 2020 Unity Technologies ApS

Licensed under the Unity Companion License for Unity-dependent projects:
https://www.unity3d.com/legal/licenses/Unity_Companion_License

Unless expressly provided otherwise, the Software under this license is made
available strictly on an “AS IS” BASIS WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED. Please review the license for details on these and other terms and conditions.

Local modifications: three world-shared Texture2DArray bindings for color, normal
and RGB AO/height/smoothness, twelve local layer slots mapped to palette slices,
three coverage maps, height-weighted blending and normal blending. Color arrays
are sRGB; normal/mask arrays are linear. Imported normals are decoded when packed
and stored as canonical RGB normals. Spline projection and displacement sample
the same arrays. Source assets and installed HDRP packages are not modified.
The terrain variants omit eight inherited Lit texture maps (tangent TS/OS,
anisotropy, iridescence thickness/mask, specular color, transmittance color and
coat mask) and their sampling variants. These are not LTSurfaceLayer inputs;
removing them alone did NOT resolve Unity's reported 70/64 texture-parameter
overflow. The array migration replaces 36 per-layer texture bindings with three,
without dropping terrain layers or deformation maps. Inherited scalar CBUFFER
layout is preserved; three LT palette-mapping vectors are added consistently.
Device limits must still be checked in Unity, including native warnings/asserts;
standalone HLSL compilation and atlas readback alone do not validate HDRP terrain.
Recheck these copies when upgrading HDRP. The installed package is not modified.
