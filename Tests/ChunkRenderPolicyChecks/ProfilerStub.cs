using System;
using System.Collections.Generic;

// Scope-order observer only. Native ProfilerMarker API is separately checked
// by compiling the production assembly against the installed Unity references.
namespace Unity.Profiling
{
    public readonly struct ProfilerMarker
    {
        readonly string name;
        public static readonly List<string> Entered=new();
        static readonly Stack<string> active=new();
        public static int ActiveCount=>active.Count;
        public ProfilerMarker(string name){this.name=name;}
        public Scope Auto(){Entered.Add(name);active.Push(name);return new Scope(name);}
        public readonly struct Scope:IDisposable
        {
            readonly string name;
            public Scope(string name){this.name=name;}
            public void Dispose()
            {
                if(active.Count==0||active.Pop()!=name)throw new InvalidOperationException("Unbalanced profiler scope: "+name);
            }
        }
    }
}
