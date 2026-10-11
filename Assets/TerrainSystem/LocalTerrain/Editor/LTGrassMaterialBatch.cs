using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    [Serializable]
    internal sealed class LTGrassMaterialEntry
    {
        public Material material;
        public bool included=true;
    }

    // Editor-only batch operations. Each field edit changes just that property;
    // mixed values in other fields are never copied from the first material.
    internal static class LTGrassMaterialBatch
    {
        internal static Material[] Gather(IEnumerable<Object> objects)
        {
            var result=new List<Material>();
            foreach(var obj in objects)
            {
                if(obj is Material material)result.Add(material);
                else if(obj is GameObject prefab)
                    foreach(var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))result.AddRange(renderer.sharedMaterials);
            }
            return result.Where(m=>m).Distinct().ToArray();
        }
        internal static int Add(List<LTGrassMaterialEntry> entries,IEnumerable<Material> materials,Func<Material,bool> supported)
        {
            int count=0;
            foreach(var material in materials)
                if(material&&supported(material)&&!entries.Any(e=>e!=null&&e.material==material))
                {entries.Add(new LTGrassMaterialEntry{material=material});count++;}
            return count;
        }
        internal static Material[] Selected(IEnumerable<LTGrassMaterialEntry> entries,Func<Material,bool> supported)
            =>entries.Where(e=>e!=null&&e.included&&e.material&&supported(e.material)).Select(e=>e.material).Distinct().ToArray();
        internal static bool FloatMixed(Material[] materials,string property)
            =>materials.Skip(1).Any(m=>!m.GetFloat(property).Equals(materials[0].GetFloat(property)));
        internal static bool ColorMixed(Material[] materials,string property)
            =>materials.Skip(1).Any(m=>!m.GetColor(property).Equals(materials[0].GetColor(property)));
        internal static bool SetFloat(IEnumerable<Material> materials,string property,float value)
        {
            if(float.IsNaN(value)||float.IsInfinity(value))return false;
            return Change(materials.Where(m=>m&&m.HasProperty(property)&&!m.GetFloat(property).Equals(value)),m=>m.SetFloat(property,value));
        }
        internal static bool SetColor(IEnumerable<Material> materials,string property,Color value)
            =>Change(materials.Where(m=>m&&m.HasProperty(property)&&!m.GetColor(property).Equals(value)),m=>m.SetColor(property,value));
        internal static bool Change(IEnumerable<Material> materials,Action<Material> change)
        {
            var targets=materials.Where(m=>m).Distinct().ToArray();if(targets.Length==0)return false;
            Undo.RecordObjects(targets,"Grass color ("+targets.Length+" materials)");
            foreach(var material in targets){change(material);EditorUtility.SetDirty(material);}
            return true;
        }
    }
}
