using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
partial class Checks
{
    static IEqualityComparer<long> GeometryEdgeComparer=EdgeKeyComparer.Instance;
    static bool captureGeometry;
    static string lastGeometry;
    static void GeometryHashChecks()
    {
        try
        {
            captureGeometry=true;int comparisons=0;
            foreach(bool cave in new[]{false,true})
            foreach(float density in new[]{.7f,2f})
            foreach(float weld in new[]{0f,.2f,.5f})
            {
                GeometryEdgeComparer=EqualityComparer<long>.Default;
                Case(2,1,density,3,.1f,false,weld,cave);string baseline=lastGeometry;
                GeometryEdgeComparer=EdgeKeyComparer.Instance;
                Case(2,1,density,3,.1f,false,weld,cave);
                Require(lastGeometry==baseline,"edge hash changed geometry/UV/normals/tangents/indices");comparisons++;
            }
            captureGeometry=false;
            void Run(bool mixed)
            {
                GeometryEdgeComparer=mixed?EdgeKeyComparer.Instance:EqualityComparer<long>.Default;
                Case(2,1,2,3,.1f,false,.5f,false);
            }
            for(int i=0;i<3;i++){Run(false);Run(true);}
            var oldTimes=new List<double>();var newTimes=new List<double>();
            for(int i=0;i<10;i++)for(int j=0;j<2;j++)
            {
                bool mixed=((i+j)&1)==0;var timer=Stopwatch.StartNew();Run(mixed);timer.Stop();
                (mixed?newTimes:oldTimes).Add(timer.Elapsed.TotalMilliseconds);
            }
            oldTimes.Sort();newTimes.Sort();
            Console.WriteLine($"PASS geometry hash: {comparisons} exact geometry/attribute comparisons (Union/Cave, density, weld). Full fixture median: default {oldTimes[5]:F2} ms, mixed {newTimes[5]:F2} ms; includes fixture assertions, outside Unity.");
        }
        finally{GeometryEdgeComparer=EdgeKeyComparer.Instance;captureGeometry=false;lastGeometry=null;}
    }
}
