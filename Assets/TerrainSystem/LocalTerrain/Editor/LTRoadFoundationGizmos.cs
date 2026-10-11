using System;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTRoadFoundationGizmos
    {
        const string Preference="LocalTerrain.Road.FoundationGizmos";
        internal static bool Visible
        {
            get=>SessionState.GetBool(Preference,true);
            set{SessionState.SetBool(Preference,value);SceneView.RepaintAll();}
        }
        sealed class Cache {public LTRoadMath.Snapshot source;public LTRoadFoundationOutline.Lines lines;public bool cutStart,cutEnd;}
        static readonly ConditionalWeakTable<LTRoad,Cache> caches=new ConditionalWeakTable<LTRoad,Cache>();
        // Deliberately not Pickable: the broad ground zone must not intercept clicks
        // intended for spline points, sockets or other scene objects.
        [DrawGizmo(GizmoType.Selected|GizmoType.NonSelected)]
        static void Draw(LTRoad road,GizmoType type)
        {
            if(!Visible||!road||!road.isActiveAndEnabled||!road.World||!SceneView.currentDrawingSceneView)return;
            bool selected=false;
            bool ConnectedNodeSelected(LTRoadJunction node,GameObject go)=>node&&node.CoordinateShoulders&&
                (go==node.gameObject||go.transform.IsChildOf(node.transform));
            foreach(var go in Selection.gameObjects)
                if(go&&(go==road.gameObject||go.transform.IsChildOf(road.transform)||
                    ConnectedNodeSelected(road.startJunction,go)||ConnectedNodeSelected(road.endJunction,go))){selected=true;break;}
            if(!selected)return;
            var color=Gizmos.color;var matrix=Gizmos.matrix;
            try
            {
                var snapshot=LTRoadDimensionHandles.DisplaySnapshot(road);var cache=caches.GetValue(road,r=>new Cache());
                bool start=road.startJunction&&road.startJunction.isActiveAndEnabled,end=road.endJunction&&road.endJunction.isActiveAndEnabled;
                if(!ReferenceEquals(cache.source,snapshot)||cache.cutStart!=start||cache.cutEnd!=end)
                {
                    cache.lines=LTRoadFoundationOutline.Build(snapshot,start,end);
                    cache.source=snapshot;cache.cutStart=start;cache.cutEnd=end;
                }
                Gizmos.matrix=road.World.transform.localToWorldMatrix;
                void Lines(Vector3[] points){if(points.Length>0)Gizmos.DrawLineList(points);}
                Gizmos.color=new Color(.1f,.9f,1);Lines(cache.lines.shoulder);
                Gizmos.color=new Color(.1f,.9f,1,.35f);Lines(cache.lines.transition);
            }
            catch(ArgumentException){ } // Existing road inspector reports invalid authoring.
            finally{Gizmos.color=color;Gizmos.matrix=matrix;}
        }
    }
}
