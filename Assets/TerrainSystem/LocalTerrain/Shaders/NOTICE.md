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

Local modifications: independent texture bindings for eight layers, two coverage
maps, RGB AO/height/smoothness mapping, height-weighted blending, normal blending.
Recheck these copies when upgrading HDRP. The installed package is not modified.
