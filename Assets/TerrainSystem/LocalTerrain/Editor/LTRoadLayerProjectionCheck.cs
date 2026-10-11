using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTRoadLayerProjectionCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Road Layer Projections (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var scene=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();
            T Own<T>(T value) where T:Object {owned.Add(value);return value;}
            GameObject Child(string name,Transform parent=null)
            {
                var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,scene);
                if(parent)go.transform.SetParent(parent,false);return go;
            }
            try
            {
                var root=Child("Independent road projection validation");var world=root.AddComponent<LTWorld>();
                world.autoUpdate=false;world.enableLayerPainting=false;world.previewLODs=false;
                var ground=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());
                var wheels=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());world.baseLayer=ground;
                var road=Child("Owned road",root.transform).AddComponent<LTRoad>();
                road.groundLayer=ground;road.wheelLayer=wheels;road.wheelTracks.enabled=true;
                var stamp=road.GetComponent<LTPaintStamp>();var original=road.Capture(world);
                Require(road.CapturePaintProjection(world,false)==null&&road.CapturePaintProjection(world,true)==null,"legacy World default");
                road.wheelTracks.projection=LTRoadWheelProjection.Spline;road.wheelTracks.tileSizeMetres=new Vector2(2,3);
                Require(road.CapturePaintProjection(world,false)==null,"World ground was assigned road UVs");
                var projected=road.CapturePaintProjection(world,true);
                Require(projected!=null&&projected.projection==LTRoadProjection.Spline,"wheel Spline route missing");
                Require(projected.textureAcrossMetres==2&&projected.textureRepeatMetres==3,"wheel mapping inherited hidden ground controls");
                Require(road.Capture(world).geometryHash==original.geometryHash&&road.Capture(world).detailHash==original.detailHash,"projection changed geometry/removal signatures");
                var palette=new List<LTSurfaceLayer>{world.baseLayer};stamp.AppendLayers(palette,true);
                Require(palette.Count==2&&palette[0]==ground&&palette[1]==wheels,"shared ground consumed an extra slot");
                var ordinary=Child("World biome stamp",root.transform).AddComponent<LTPaintStamp>();ordinary.layer=ground;
                ordinary.AppendLayers(palette);Require(palette.Count==2,"ordinary biome duplicated shared ground");
                road.wheelTracks.projection=LTRoadWheelProjection.World;road.projection=LTRoadProjection.Spline;
                Require(road.CapturePaintProjection(world,false)!=null&&road.CapturePaintProjection(world,true)==null,"inverse Spline/World routes");
                road.wheelTracks.projection=LTRoadWheelProjection.Inherit;
                Require(road.CapturePaintProjection(world,false).projectionHash==road.CapturePaintProjection(world,true).projectionHash,"legacy Spline inheritance");
                road.projection=LTRoadProjection.World;road.wheelTracks.projection=LTRoadWheelProjection.Spline;
                road.wheelTracks.enabled=false;Require(road.CapturePaintProjection(world,true)==null,"disabled wheels allocate a map");
                road.wheelTracks.enabled=true;road.wheelLayer=ground;
                Require(road.CapturePaintProjection(world,true)==null,"same-layer wheels allocate conflicting UVs");road.wheelLayer=wheels;
                Require(road.groundLayer==ground&&road.wheelLayer==wheels&&ground.tileSizeMetres==new Vector2(4,4),"source assets were replaced/edited");
                Debug.Log("[Road Layer Projections v1] PASS: shared ground occupies one slot; independent World/Spline routes, wheel UV settings, legacy defaults and unchanged geometry/removal signatures. Preview scene only; no scene/assets saved. Not a GPU rendering test.");
            }
            catch(Exception e){Debug.LogError("[Road Layer Projections v1] FAIL: "+e);}
            finally
            {
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
