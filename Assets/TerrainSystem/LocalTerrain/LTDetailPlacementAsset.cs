using System;
using System.Collections.Generic;
using UnityEngine;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    // Stable numeric/content key. Never use InstanceID or string.GetHashCode on disk.
    public sealed class LTDetailPlacementFingerprint
    {
        LTPaintMath.GeometryFingerprint value;
        public void Add(int x)=>value.Add(x);
        public void Add(float x)=>value.Add(x);
        public void Add(bool x)=>Add(x?1:0);
        public void Add(string x){Add(x==null?-1:x.Length);if(x!=null)foreach(char c in x)Add((int)c);}
        public void Add(Vector2 x){Add(x.x);Add(x.y);}
        public void Add(Vector3 x)=>value.Add(x);
        public void Add(Quaternion x){Add(x.x);Add(x.y);Add(x.z);Add(x.w);}
        public void Add(Rect x){Add(x.x);Add(x.y);Add(x.width);Add(x.height);}
        public void Add(Matrix4x4 x){for(int i=0;i<16;i++)Add(x[i]);}
        public void Add(Color32[] pixels)
        {
            Add(pixels==null?-1:pixels.Length);
            if(pixels!=null)foreach(var p in pixels)Add(unchecked(p.r|(p.g<<8)|(p.b<<16)|(p.a<<24)));
        }
        public override string ToString()=>value.ToString();
    }

    [PreferBinarySerialization]
    public sealed class LTDetailPlacementAsset : ScriptableObject
    {
        // Bump when candidate/placement semantics or the persisted record changes.
        public const int CurrentVersion=1;
        [SerializeField,HideInInspector] int version=CurrentVersion;
        [SerializeField,HideInInspector] Cell[] cells=Array.Empty<Cell>();
        Dictionary<Vector2Int,Cell> index;
        public int CellCount=>cells?.Length??0;
        public bool Compatible=>version==CurrentVersion;
        public IEnumerable<Cell> Cells=>Compatible&&cells!=null?cells:Array.Empty<Cell>();
        [Serializable] public struct Item
        {
            public Vector3 position;
            public Quaternion rotation;
            public float scale;
            public Vector2 maskPosition;
            public uint id;
        }
        [Serializable] public sealed class Group
        {
            public int source;
            public Item[] items=Array.Empty<Item>();
        }
        [Serializable] public sealed class Cell
        {
            public Vector2Int coordinate;
            public string inputKey,payloadKey;
            public Object[] references=Array.Empty<Object>();
            public int sourceCount;
            public Group[] groups=Array.Empty<Group>();
        }
        public void Initialize(Cell[] data)
        {version=CurrentVersion;cells=data??Array.Empty<Cell>();index=null;}
        void OnEnable()=>index=null;
        void OnValidate()=>index=null;
        public Cell Find(Vector2Int coordinate)
        {
            if(!Compatible||cells==null)return null;
            if(index==null)
            {
                index=new Dictionary<Vector2Int,Cell>();
                foreach(var cell in cells)if(cell!=null)
                {
                    // Duplicate coordinates are ambiguous: fail closed for that cell.
                    if(index.ContainsKey(cell.coordinate))index[cell.coordinate]=null;
                    else index.Add(cell.coordinate,cell);
                }
            }
            return index.TryGetValue(coordinate,out var found)?found:null;
        }
        public static bool ReferencesMatch(Object[] a,Object[] b)
        {
            if(a==null||b==null||a.Length!=b.Length)return false;
            for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;
            return true;
        }
        public static string PayloadKey(Cell cell)
        {
            var hash=new LTDetailPlacementFingerprint();hash.Add(CurrentVersion);
            hash.Add(cell.coordinate.x);hash.Add(cell.coordinate.y);hash.Add(cell.inputKey);hash.Add(cell.sourceCount);
            hash.Add(cell.groups.Length);
            foreach(var group in cell.groups)
            {
                hash.Add(group.source);hash.Add(group.items.Length);
                foreach(var item in group.items)
                {hash.Add(item.position);hash.Add(item.rotation);hash.Add(item.scale);hash.Add(item.maskPosition);hash.Add(unchecked((int)item.id));}
            }
            return hash.ToString();
        }
        // Bound work before restoring or publishing any group. Malformed caches
        // fall back to generation; they never produce partially loaded cells.
        public static bool ValidPayload(Cell cell,int sourceCount,int maxInstances,Rect rect)
        {
            if(cell==null||cell.sourceCount!=sourceCount||cell.groups==null||cell.groups.Length>sourceCount||
                string.IsNullOrEmpty(cell.payloadKey))return false;
            bool Finite(float x)=>!float.IsNaN(x)&&!float.IsInfinity(x);
            bool Vector(Vector3 x)=>Finite(x.x)&&Finite(x.y)&&Finite(x.z);
            var seen=new HashSet<int>();long count=0;
            foreach(var group in cell.groups)
            {
                if(group==null||group.source<0||group.source>=sourceCount||!seen.Add(group.source)||group.items==null)return false;
                count+=group.items.Length;if(count>maxInstances)return false;
                foreach(var item in group.items)
                {
                    var q=item.rotation;float length=q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w;
                    if(!Vector(item.position)||!Finite(item.scale)||item.scale<=0||!Finite(length)||Math.Abs(length-1)>.002f||
                        !Finite(item.maskPosition.x)||!Finite(item.maskPosition.y)||!rect.Contains(item.maskPosition))return false;
                }
            }
            return cell.payloadKey==PayloadKey(cell);
        }
    }
}
