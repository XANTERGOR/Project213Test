using System;

namespace LocalTerrainPrototype
{
    // Road LODs are distance-based, not the source prefab's screen-relative LODGroup.
    public static class LTRoadLODPolicy
    {
        public const int MaxAdditionalLevels=7;

        public static int AutomaticCount(int configured,int[] prefabLevelCounts)
        {
            if(configured<0||configured>MaxAdditionalLevels)throw new InvalidOperationException("Асфальт поддерживает 0–7 дополнительных LOD.");
            int count=0;
            foreach(int levels in prefabLevelCounts)
            {
                if(levels<0||levels>MaxAdditionalLevels+1)throw new InvalidOperationException("В модуле поддерживается до 8 уровней LOD.");
                count=Math.Max(count,levels==0?configured:levels-1);
            }
            return count;
        }

        // Clamp a common road level to this module's own last available level.
        public static int SourceLevel(int level,int prefabLevelCount)=>Math.Min(level,Math.Max(0,prefabLevelCount-1));

        public static float DefaultDistance(int level)=>level==1?50:level==2?120:250*(1<<(Math.Max(3,level)-3));
        public static float DefaultError(int level)=>.05f*(1<<(Math.Max(1,level)-1));

        public static float Distance(LTLODSettings[] settings,int level)
        {
            float previous=0;
            for(int i=0;i<level;i++)
            {
                if(settings!=null&&i<settings.Length)
                {
                    var s=settings[i];
                    if(s==null||float.IsNaN(s.startDistance)||float.IsInfinity(s.startDistance)||s.startDistance<0||
                        (i>0&&s.startDistance<=previous))return float.NaN;
                    previous=s.startDistance;
                }
                else previous=Math.Max(DefaultDistance(i+1),previous*2);
                if(float.IsInfinity(previous))return float.NaN;
            }
            return previous;
        }

        public static int Select(float distance,int current,LTLODSettings[] settings,int availableLevels,float hysteresis)
        {
            int count=Math.Min(MaxAdditionalLevels,Math.Max(0,availableLevels-1)),level=0;
            current=Math.Min(count,Math.Max(0,current));
            for(int i=1;i<=count;i++)
            {
                float start=Distance(settings,i);
                if(float.IsNaN(start))return 0;
                float threshold=start+(current>=i?-Math.Max(0,hysteresis):Math.Max(0,hysteresis));
                if(distance>=threshold)level=i;else break;
            }
            return level;
        }
    }
}
