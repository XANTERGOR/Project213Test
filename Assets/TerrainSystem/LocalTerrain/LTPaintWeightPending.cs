using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTPaintRuntime
    {
        sealed class PendingWeights:IDisposable
        {
            public LTPaintWeightWork work;
            public LTRoadRasterSet raster;
            public LTRoadMath.Snapshot[] roads;
            public int coverage,weight;
            public Rect rect;
            public bool Matches(Rect area,int coverageKey,int weightKey,List<LTRoadMath.Snapshot> current)
            {
                if(!work.IsValid||rect!=area||coverage!=coverageKey||weight!=weightKey||roads.Length!=current.Count)return false;
                for(int i=0;i<roads.Length;i++)if(!ReferenceEquals(roads[i],current[i]))return false;
                return true;
            }
            public void Dispose(){work.Dispose();}
        }
    }
}
