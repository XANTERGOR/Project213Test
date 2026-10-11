using System;
using System.Collections.Generic;

// Managed behavior checks, not native Unity Material/Undo or IMGUI validation.
namespace UnityEngine
{
    public class Object
    {
        public bool destroyed;
        public static implicit operator bool(Object value)=>value!=null&&!value.destroyed;
    }
    public readonly struct Color : IEquatable<Color>
    {
        public readonly float r,g,b,a;
        public Color(float r,float g,float b,float a=1){this.r=r;this.g=g;this.b=b;this.a=a;}
        public bool Equals(Color other)=>r.Equals(other.r)&&g.Equals(other.g)&&b.Equals(other.b)&&a.Equals(other.a);
        public override bool Equals(object other)=>other is Color c&&Equals(c);
        public override int GetHashCode()=>HashCode.Combine(r,g,b,a);
    }
    public sealed class Material : Object
    {
        public readonly Dictionary<string,float> floats=new();
        public readonly Dictionary<string,Color> colors=new();
        public int writes;
        public bool HasProperty(string property)=>floats.ContainsKey(property)||colors.ContainsKey(property);
        public float GetFloat(string property)=>floats[property];
        public Color GetColor(string property)=>colors[property];
        public void SetFloat(string property,float value){floats[property]=value;writes++;}
        public void SetColor(string property,Color value){colors[property]=value;writes++;}
    }
    public sealed class MeshRenderer : Object
    {
        public Material[] sharedMaterials=Array.Empty<Material>();
    }
    public sealed class GameObject : Object
    {
        public MeshRenderer[] renderers=Array.Empty<MeshRenderer>();
        public bool includedInactive;
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T:class
        {
            includedInactive=includeInactive;
            return Array.ConvertAll(renderers,r=>r as T);
        }
    }
}
namespace UnityEditor
{
    public static class Undo
    {
        public static readonly List<UnityEngine.Object[]> records=new();
        public static void RecordObjects(UnityEngine.Object[] objects,string name)=>records.Add((UnityEngine.Object[])objects.Clone());
    }
    public static class EditorUtility
    {
        public static readonly List<UnityEngine.Object> dirty=new();
        public static void SetDirty(UnityEngine.Object obj)=>dirty.Add(obj);
    }
}
