using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    // Runtime-only, reversible shadow scope. Camera flags skip entire HDRP passes;
    // light flags also cover reflection/probe cameras generated inside HDRP later.
    // No renderer/material/shader or pipeline/Volume asset is modified.
    internal sealed class LTTerrainFrameShadows : IDisposable
    {
        static readonly FrameSettingsField[] Fields={FrameSettingsField.ShadowMaps,FrameSettingsField.ContactShadows,
            FrameSettingsField.ScreenSpaceShadows,FrameSettingsField.Shadowmask};
        sealed class CameraState
        {
            public HDAdditionalCameraData data;
            public bool custom,created;
            public FrameSettings settings;
            public FrameSettingsOverrideMask mask;
        }
        sealed class LightState
        {
            public Light light;
            public LightShadows shadows;
            public HDAdditionalLightData data;
            public bool contactOverride,contactValue;
        }
        readonly Dictionary<Camera,CameraState> cameras=new Dictionary<Camera,CameraState>();
        readonly Dictionary<Light,LightState> lights=new Dictionary<Light,LightState>();
        public bool Enabled {get;private set;}
        public int Revision {get;private set;}
        public int CameraCount=>cameras.Count;
        public int LightCount=>lights.Count;

        public void Enable()
        {
            if(!Application.isPlaying)return;
            if(!Enabled)
            {
                Enabled=true;
                // beginCameraRendering is too late: HDRP already aggregated frame
                // settings. beginContextRendering runs BEFORE that aggregation.
                RenderPipelineManager.beginContextRendering+=BeforeContext;
                SceneManager.sceneLoaded+=SceneLoaded;
            }
            Refresh();
        }
        public void Refresh()
        {
            if(!Enabled)return;
            // Only on mode entry, capture start and scene load, never every frame.
            try
            {
                foreach(var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include,FindObjectsSortMode.None))Capture(light);
                foreach(var camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include,FindObjectsSortMode.None))Capture(camera);
                EnforceLights();
            }
            catch(Exception error){Recover(error);}
        }
        void SceneLoaded(Scene scene,LoadSceneMode mode)=>Refresh();
        void Capture(Light light)
        {
            if(!light||lights.ContainsKey(light))return;
            var data=light.GetComponent<HDAdditionalLightData>();
            lights.Add(light,new LightState{light=light,shadows=light.shadows,data=data,
                contactOverride=data&&data.useContactShadow.useOverride,contactValue=data&&data.useContactShadow.@override});
            Revision++;
        }
        void Capture(Camera camera)
        {
            if(!camera||camera.cameraType==CameraType.Preview)return;
            if(!cameras.TryGetValue(camera,out var state)||!state.data)
            {
                var data=camera.GetComponent<HDAdditionalCameraData>();bool created=!data;
                if(created)
                {
                    data=camera.gameObject.AddComponent<HDAdditionalCameraData>();
                    data.hideFlags=HideFlags.HideAndDontSave;
                }
                state=new CameraState{data=data,created=created,custom=data.customRenderingSettings,
                    settings=data.renderingPathCustomFrameSettings,mask=data.renderingPathCustomFrameSettingsOverrideMask};
                cameras[camera]=state;Revision++;
            }
            var current=state.data.renderingPathCustomFrameSettings;
            // Dormant non-shadow overrides must NOT become active when enabling
            // custom settings on a camera that previously used pipeline defaults.
            var mask=state.custom?state.data.renderingPathCustomFrameSettingsOverrideMask:default;
            foreach(var field in Fields){current.SetEnabled(field,false);mask.mask[(uint)field]=true;}
            state.data.renderingPathCustomFrameSettings=current;
            state.data.renderingPathCustomFrameSettingsOverrideMask=mask;
            state.data.customRenderingSettings=true;
        }
        void EnforceLights()
        {
            foreach(var state in lights.Values)
            {
                if(state.light&&state.light.shadows!=LightShadows.None)state.light.shadows=LightShadows.None;
                if(state.data)
                {
                    state.data.useContactShadow.useOverride=true;
                    state.data.useContactShadow.@override=false;
                }
            }
        }
        void BeforeContext(ScriptableRenderContext context,List<Camera> renderingCameras)
        {
            if(!Enabled)return;
            try
            {
                EnforceLights();
                foreach(var camera in renderingCameras)Capture(camera);
            }
            catch(Exception error){Recover(error);}
        }
        void Recover(Exception error)
        {
            Dispose();
            Debug.LogWarning("[Local Terrain A/B] Shadow override failed; previous settings restored: "+error);
        }
        public void Dispose()
        {
            RenderPipelineManager.beginContextRendering-=BeforeContext;
            SceneManager.sceneLoaded-=SceneLoaded;
            Enabled=false;
            foreach(var state in cameras.Values)
            {
                if(!state.data)continue;
                var current=state.data.renderingPathCustomFrameSettings;
                var mask=state.data.renderingPathCustomFrameSettingsOverrideMask;
                // Restore only the four fields we owned; retain unrelated runtime
                // changes. If custom settings were off, restore the dormant mask.
                foreach(var field in Fields)
                {current.SetEnabled(field,state.settings.IsEnabled(field));mask.mask[(uint)field]=state.mask.mask[(uint)field];}
                state.data.renderingPathCustomFrameSettings=current;
                state.data.renderingPathCustomFrameSettingsOverrideMask=state.custom?mask:state.mask;
                state.data.customRenderingSettings=state.custom;
                if(state.created)
                {
                    if(Application.isPlaying)UnityEngine.Object.Destroy(state.data);
                    else UnityEngine.Object.DestroyImmediate(state.data);
                }
            }
            foreach(var state in lights.Values)
            {
                if(state.light)state.light.shadows=state.shadows;
                if(state.data)
                {
                    state.data.useContactShadow.useOverride=state.contactOverride;
                    state.data.useContactShadow.@override=state.contactValue;
                }
            }
            if(cameras.Count>0||lights.Count>0)Revision++;
            cameras.Clear();lights.Clear();
        }
    }
}
