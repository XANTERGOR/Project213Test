using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LocalTerrainPrototype
{
    /// <summary>
    /// Main-thread contract for explicitly opted-in detail materials. All script
    /// writers must use Mutate, or call NotifyChanged after completing a write.
    /// This is NOT a detector of arbitrary third-party Material.Set* calls.
    /// </summary>
    public static class LTDetailMaterialNotifications
    {
        sealed class Identity : IEqualityComparer<Material>
        {
            public bool Equals(Material a,Material b)=>ReferenceEquals(a,b);
            public int GetHashCode(Material value)=>RuntimeHelpers.GetHashCode(value);
        }
        internal sealed class State
        {
            internal Material source;
            internal int users,writing;
            internal ulong revision=1;
            internal bool live=true,cacheable=true;
#if UNITY_EDITOR
            int materialDirty,shaderDirty;Shader shader;
            internal void RefreshEditor()
            {
                if(!source)return;
                var nextShader=source.shader;
                int nextMaterialDirty=UnityEditor.EditorUtility.GetDirtyCount(source);
                int nextShaderDirty=nextShader?UnityEditor.EditorUtility.GetDirtyCount(nextShader):0;
                if(shader!=nextShader||materialDirty!=nextMaterialDirty||shaderDirty!=nextShaderDirty)
                {
                    shader=nextShader;materialDirty=nextMaterialDirty;shaderDirty=nextShaderDirty;
                    Changed(this);
                }
            }
#endif
        }
        internal readonly struct Ticket
        {
            readonly State state;readonly ulong revision;readonly object generation;
            internal Ticket(State state){this.state=state;revision=state.revision;generation=epoch;}
            internal bool CanReuse(Ticket previous)=>state!=null&&ReferenceEquals(state,previous.state)&&
                state.live&&state.cacheable&&state.writing==0&&revision==state.revision&&revision==previous.revision&&
                ReferenceEquals(generation,epoch)&&ReferenceEquals(generation,previous.generation);
        }
        internal sealed class Registration : IDisposable
        {
            State state;
            internal Registration(State state){this.state=state;state.users++;}
            internal Ticket Capture()=>state!=null&&state.live&&state.cacheable&&state.writing==0?new Ticket(state):default;
            internal void RefreshEditor()
            {
#if UNITY_EDITOR
                state?.RefreshEditor();
#endif
            }
            public void Dispose()
            {
                var old=state;state=null;if(old==null)return;
                if(--old.users!=0)return;
                old.live=false;states.Remove(old.source);old.source=null;
            }
        }
        static readonly Dictionary<Material,State> states=new Dictionary<Material,State>(new Identity());
        static object epoch=new object();
        internal static ulong ChangeVersion {get;private set;}
        internal static ulong AdapterVersion {get;private set;}
        internal static Registration Register(Material source)
        {
            if(!source)throw new ArgumentException("A live material is required.",nameof(source));
            if(!states.TryGetValue(source,out var state))
            {state=new State{source=source};states.Add(source,state);}
            return new Registration(state);
        }
        static void Changed(State state)
        {
            if(state.revision==ulong.MaxValue)state.cacheable=false;
            else state.revision++;
            unchecked{ChangeVersion++;}
        }
        public static void NotifyChanged(Material source)
        {
            if(!ReferenceEquals(source,null)&&states.TryGetValue(source,out var state))Changed(state);
        }
        /// <summary>Invalidates before/after the write, including exceptions and nested writes.</summary>
        public static void Mutate(Material source,Action<Material> write)
        {
            if(write==null)throw new ArgumentNullException(nameof(write));
            // Temporary registration also covers a renderer opting in inside write.
            using var registration=Register(source);
            var state=states[source];Changed(state);state.writing++;
            try{write(source);}
            finally{state.writing--;Changed(state);}
        }
        /// <summary>Editor bridge / explicit broad invalidation; never edits source assets.</summary>
        public static void InvalidateAll(bool reloadAdapters=false)
        {
            epoch=new object();unchecked{ChangeVersion++;if(reloadAdapters)AdapterVersion++;}
        }
    }
}
