using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal enum LTTerrainDiagnosticMode { Normal, BaseLayer, NoTriplanar, NoShadows, ForceFar, TerrainLOD2, TerrainLOD4, TopTwoAfterHeight, TopTwoBeforeHeight, LiveLayers, FastLayers, TopTwoColorRemainder }

    // One cache per actual terrain renderer. Never use property blocks here: that
    // would change SRP batching and invalidate the comparison. Even Normal uses a
    // clone so all modes have identical material ownership/batching.
    internal sealed class LTTerrainDiagnosticMaterials : IDisposable
    {
        sealed class Entry
        {
            public Material source,copy;
            public uint revision;
            public LTTerrainDiagnosticMode mode;
            public bool far;
            public Vector4 settings;
            public bool roads;
        }
        static readonly HashSet<LTTerrainDiagnosticMaterials> Live=new HashSet<LTTerrainDiagnosticMaterials>();
        static readonly int BaseOnly=Shader.PropertyToID("_LTBaseOnly");
        static readonly int Triplanar=Shader.PropertyToID("_LTTriplanar");
        static readonly int FastParams=Shader.PropertyToID("_LTFastLayerParams");
        public static bool IsLayerComparison(LTTerrainDiagnosticMode mode)=>
            mode==LTTerrainDiagnosticMode.LiveLayers||mode==LTTerrainDiagnosticMode.FastLayers||
            mode==LTTerrainDiagnosticMode.TopTwoAfterHeight||mode==LTTerrainDiagnosticMode.TopTwoBeforeHeight||mode==LTTerrainDiagnosticMode.TopTwoColorRemainder;
        public static bool CanCompareLayers(Material material)=>material&&material.HasProperty(FastParams)&&!material.IsKeywordEnabled("_LT_FAR_ONLY");
        static Vector4 LayerParameters(Vector4 p,LTTerrainDiagnosticMode mode)
        {
            p.z=mode==LTTerrainDiagnosticMode.LiveLayers||mode==LTTerrainDiagnosticMode.FastLayers?0:1;
            p.w=mode==LTTerrainDiagnosticMode.TopTwoColorRemainder?2:mode==LTTerrainDiagnosticMode.TopTwoBeforeHeight?1:0;
            return p;
        }
        public static bool LayerModeBound(Material material,LTTerrainDiagnosticMode mode)
        {
            if(!IsLayerComparison(mode)||!CanCompareLayers(material))return false;
            var p=material.GetVector(FastParams);
            var expected=LayerParameters(p,mode);
            return p.z==expected.z&&p.w==expected.w&&
                material.IsKeywordEnabled("_LT_FAST_LAYERS")== (mode!=LTTerrainDiagnosticMode.LiveLayers)&&
                material.IsKeywordEnabled("_LT_FAST_PREHEIGHT")== (expected.w>0)&&
                material.IsKeywordEnabled("_LT_FAST_COLOR_REMAINDER")== (expected.w==2);
        }
        readonly MeshRenderer renderer;
        Material restoreMaterial;
        Bounds referenceBounds;
        bool hasReferenceBounds;
        readonly List<Entry> entries=new List<Entry>(3);
        public static int Revision {get;private set;}
        public static int ContentRevision {get;private set;}

        public LTTerrainDiagnosticMaterials(MeshRenderer renderer){this.renderer=renderer;}

        // Keep material distance choice independent of the tested LOD mesh bounds.
        // One world-space reference for all cameras/modes until this scope closes.
        public float DistanceSquared(Vector3 position)
        {
            if(!hasReferenceBounds){referenceBounds=renderer.bounds;hasReferenceBounds=true;}
            return referenceBounds.SqrDistance(position);
        }

        // Natural per-camera selection, before ForceFar. Closing must not leave
        // a near chunk on its far material until the next render callback.
        public void SetRestoreMaterial(Material material){restoreMaterial=material;}

        public bool Owns(Material material)
        {
            if(!material)return false;
            foreach(var entry in entries)if(entry.copy==material)return true;
            return false;
        }

        public Material Select(Material source,uint revision,LTTerrainDiagnosticMode mode,bool far,out bool refreshed,Vector4? fastSettings=null,bool? roadSampling=null)
        {
            refreshed=false;
            if(!source)return source;
            Entry found=null;
            foreach(var entry in entries)if(entry.source==source){found=entry;break;}
            if(found==null)
            {
                found=new Entry{source=source};entries.Add(found);Live.Add(this);
            }
            bool created=!found.copy;
            if(created)found.copy=new Material(source){name=source.name+" [temporary terrain diagnostic]",hideFlags=HideFlags.HideAndDontSave};
            var settings=fastSettings??(source.HasProperty(FastParams)?source.GetVector(FastParams):default);
            bool roads=roadSampling??source.IsKeywordEnabled("_LT_FAST_ROADS");
            bool contentChanged=created||found.revision!=revision||found.copy.shader!=source.shader||!found.settings.Equals(settings)||found.roads!=roads;
            if(contentChanged||found.mode!=mode||found.far!=far)
            {
                found.copy.shader=source.shader;
                found.copy.CopyPropertiesFromMaterial(source);
                // Far atlases have no detailed layer sampling. Leave their keywords
                // and projection basis unchanged in every mode, including NoTriplanar.
                if(!far)
                {
                    if(mode==LTTerrainDiagnosticMode.BaseLayer)found.copy.SetFloat(BaseOnly,1);
                    if(mode==LTTerrainDiagnosticMode.NoTriplanar)found.copy.SetFloat(Triplanar,0);
                    // Only fragment layer evaluation changes. Authored tolerances
                    // and road requirements come from the world/chunk even when
                    // its source is ordinary (that source has fast keywords off).
                    if(IsLayerComparison(mode)&&CanCompareLayers(source))
                    {
                        var p=LayerParameters(settings,mode);
                        found.copy.SetVector(FastParams,p);
                        if(mode!=LTTerrainDiagnosticMode.LiveLayers)found.copy.EnableKeyword("_LT_FAST_LAYERS");else found.copy.DisableKeyword("_LT_FAST_LAYERS");
                        if(p.w>0)found.copy.EnableKeyword("_LT_FAST_PREHEIGHT");else found.copy.DisableKeyword("_LT_FAST_PREHEIGHT");
                        if(p.w==2)found.copy.EnableKeyword("_LT_FAST_COLOR_REMAINDER");else found.copy.DisableKeyword("_LT_FAST_COLOR_REMAINDER");
                        if(roads)found.copy.EnableKeyword("_LT_FAST_ROADS");else found.copy.DisableKeyword("_LT_FAST_ROADS");
                    }
                }
                found.revision=revision;found.mode=mode;found.far=far;found.settings=settings;found.roads=roads;
                refreshed=true;unchecked{Revision++;}
                if(contentChanged)unchecked{ContentRevision++;}
            }
            return found.copy;
        }

        public void Dispose()
        {
            hasReferenceBounds=false;
            if(entries.Count==0)return;
            // Restore only our own assignment. Never overwrite an external material change.
            if(renderer)
                foreach(var entry in entries)
                    if(entry.copy&&renderer.sharedMaterial==entry.copy){renderer.sharedMaterial=restoreMaterial?restoreMaterial:entry.source;break;}
            foreach(var entry in entries)
                if(entry.copy)
                {
                    if(Application.isPlaying)UnityEngine.Object.Destroy(entry.copy);
                    else UnityEngine.Object.DestroyImmediate(entry.copy);
                }
            entries.Clear();restoreMaterial=null;Live.Remove(this);unchecked{Revision++;ContentRevision++;}
        }

        public static void RestoreAll()
        {
            // Dispose removes itself from Live, so enumerate a snapshot only on close.
            foreach(var cache in new List<LTTerrainDiagnosticMaterials>(Live))cache.Dispose();
        }
    }
}
