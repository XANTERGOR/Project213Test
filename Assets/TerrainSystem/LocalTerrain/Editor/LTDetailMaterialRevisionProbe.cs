using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LocalTerrainPrototype
{
    // DIAGNOSTIC ONLY. Not referenced by a runtime renderer. Main-thread contract:
    // claim only exclusively controlled temporary materials; every writer must
    // notify, and unknown writers/ownership loss must revoke the claim. No scan,
    // CRC, Inspector polling, Unity event hooks or implicit asset registration.
    internal sealed class LTDetailMaterialRevisionProbe<T> : IDisposable where T : class
    {
        internal sealed class State
        {
            internal T source;
            internal ulong revision=1;
            internal int writing;
            internal bool live=true;
        }
        sealed class Identity : IEqualityComparer<T>
        {
            public bool Equals(T a,T b)=>ReferenceEquals(a,b);
            public int GetHashCode(T value)=>RuntimeHelpers.GetHashCode(value);
        }
        internal readonly struct Ticket
        {
            readonly State state;
            readonly ulong revision;
            internal Ticket(State value){state=value;revision=value.revision;}
            // A default/unowned/revoked/in-progress ticket NEVER permits reuse.
            internal bool CanReuse(Ticket previous)=>state!=null&&ReferenceEquals(state,previous.state)&&
                state.live&&state.writing==0&&revision==state.revision&&revision==previous.revision;
        }
        internal sealed class Owner : IDisposable
        {
            readonly LTDetailMaterialRevisionProbe<T> registry;
            readonly State state;
            internal Owner(LTDetailMaterialRevisionProbe<T> registry,State state){this.registry=registry;this.state=state;}
            // Use after a completed external write BEFORE any consumer can render.
            // Mutate is preferred: re-entrant reads and throwing writes fail closed.
            internal void NotifyChanged()
            {
                if(!state.live)throw new ObjectDisposedException(nameof(Owner));
                if(state.revision==ulong.MaxValue)
                {Dispose();throw new InvalidOperationException("Revision exhausted; ownership revoked.");}
                state.revision++;
            }
            internal void Mutate(Action write)
            {
                if(write==null)throw new ArgumentNullException(nameof(write));
                NotifyChanged();state.writing++;
                try {write();}
                finally {state.writing--;if(state.live)NotifyChanged();}
            }
            public void Dispose()=>registry.Revoke(state);
        }
        readonly Dictionary<T,State> owned=new Dictionary<T,State>(new Identity());
        bool disposed;
        internal Owner ClaimExclusive(T source)
        {
            if(disposed)throw new ObjectDisposedException(nameof(LTDetailMaterialRevisionProbe<T>));
            if(source==null)throw new ArgumentNullException(nameof(source));
            if(owned.ContainsKey(source))throw new InvalidOperationException("Already exclusively claimed.");
            var state=new State{source=source};owned.Add(source,state);return new Owner(this,state);
        }
        internal Ticket Capture(T source)=>!disposed&&source!=null&&owned.TryGetValue(source,out var state)&&
            state.live&&state.writing==0?new Ticket(state):default;
        void Revoke(State state)
        {
            if(!state.live)return;
            state.live=false;owned.Remove(state.source);state.source=null;
        }
        // Use when a reload/reimport/Undo or external writer makes ownership uncertain.
        // Does not silently re-register anything afterward.
        internal void RevokeAll()
        {
            foreach(var state in owned.Values){state.live=false;state.source=null;}
            owned.Clear();
        }
        public void Dispose(){if(disposed)return;RevokeAll();disposed=true;}
    }
}
