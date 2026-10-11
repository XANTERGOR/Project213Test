// Test-only scope recorder. No native Unity profiler or timing simulation.
namespace Unity.Profiling
{
    public readonly struct ProfilerMarker
    {
        readonly string name;
        public ProfilerMarker(string name){this.name=name;}
        public AutoScope Auto()=>new(name);
        public readonly struct AutoScope:System.IDisposable
        {
            readonly string name;
            public AutoScope(string name){this.name=name;ProfileTrace.Begin(name);}
            public void Dispose()=>ProfileTrace.End(name);
        }
    }
    static class ProfileTrace
    {
        static readonly Stack<string> stack=new();
        static readonly Dictionary<string,int> calls=new();
        internal static readonly HashSet<string> seen=new(),nesting=new();
        internal static int Depth=>stack.Count;
        internal static int Calls(string stage)=>calls.GetValueOrDefault("LT.Details.GPU.Stage."+stage+"CPU");
        internal static void Reset()
        {if(stack.Count!=0)throw new Exception("Unclosed profiler scopes before next pass.");calls.Clear();}
        internal static void Begin(string name)
        {
            if(!name.StartsWith("LT.Details.GPU.Stage.")||!name.EndsWith("CPU"))throw new Exception("Ambiguous CPU marker name.");
            if(stack.Count>0)nesting.Add(stack.Peek()+"/"+name);
            stack.Push(name);seen.Add(name);calls[name]=calls.GetValueOrDefault(name)+1;
        }
        internal static void End(string name)
        {if(stack.Count==0||stack.Pop()!=name)throw new Exception("Unbalanced profiler scope: "+name);}
    }
}
