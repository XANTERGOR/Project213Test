using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LocalTerrainPrototype
{
    internal static class LTFastLayerBenchmarkPolicy
    {
        public const float MinimumWeight=.03f;
        public static bool BeforeHeight(int phase)=>phase==1||phase==2;
        public static string Mode(bool beforeHeight)=>beforeHeight?"Fast / top two BEFORE height":"Fast / top two AFTER height";
        public static bool FrameSuitable(int total,int ground,int overlap)=>
            total>0&&ground>=Math.Ceiling(total*.98)&&overlap>=Math.Ceiling(total*.80);
        public static int LayerCount(float[] weights)
        {
            float sum=0;foreach(float w in weights)if(w>0)sum+=w;
            if(sum<=0)return 0;
            int count=0;foreach(float w in weights)if(w>=sum*MinimumWeight)count++;
            return count;
        }
        internal readonly struct Patch
        {
            public readonly int x,y,size,layers;
            public Patch(int x,int y,int size,int layers){this.x=x;this.y=y;this.size=size;this.layers=layers;}
        }
        // A palette with five materials is insufficient: require a contiguous
        // square of texels with four/five significant weights at the same point.
        public static Patch[] FindPatches(byte[] counts,int width,int height)
        {
            if(width<3||height<3||counts.Length!=width*height)return Array.Empty<Patch>();
            var candidates=new List<Patch>();
            for(int minimum=4;minimum<=5;minimum++)
            {
                var sizes=new int[counts.Length];
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                {
                    int i=y*width+x;
                    if(counts[i]<minimum||counts[i]>5)continue;
                    int size=1;
                    if(x>0&&y>0)size+=Math.Min(sizes[i-1],Math.Min(sizes[i-width],sizes[i-width-1]));
                    sizes[i]=size;
                    if(size>=3)candidates.Add(new Patch(x-size+1,y-size+1,size,minimum));
                }
            }
            var chosen=new List<Patch>();
            foreach(var p in candidates.OrderByDescending(p=>p.size).ThenByDescending(p=>p.layers).ThenBy(p=>p.y).ThenBy(p=>p.x))
            {
                // Keep alternatives for holes/steep terrain, without trying many
                // almost identical views of the same square.
                if(chosen.Any(q=>Math.Abs((p.x+p.size*.5)-(q.x+q.size*.5))<Math.Max(3,Math.Min(16,q.size*.2))&&
                    Math.Abs((p.y+p.size*.5)-(q.y+q.size*.5))<Math.Max(3,Math.Min(16,q.size*.2))))continue;
                chosen.Add(p);if(chosen.Count==12)break;
            }
            return chosen.ToArray();
        }
        public static bool InRange(double distanceSquared,double radius)=>
            radius>0&&!double.IsNaN(distanceSquared)&&distanceSquared>=0&&distanceSquared<=radius*radius;
        public static bool Warm(double seconds,int frames)=>seconds>=2&&frames>60;
        public static string SelectionSummary(IEnumerable<(string reason,double distance)> chunks,double radius)
        {
            var all=chunks.ToArray();
            if(all.Length==0)return "Нет данных готовых чанков для этого LT World. Проверьте, что покраска включена и завершена.";
            string Label(string reason)=>reason switch
            {
                "selected"=>"Подходят", "not ready"=>"Ещё не готовы", "displacement"=>"Displacement/деформация",
                "road/asphalt"=>"Дороги/асфальт", "other shader"=>"Другой шейдер", "base only"=>"Только фоновый слой",
                "hidden"=>"Скрыты от камеры", "unsupported mesh/material layout"=>"Неподдерживаемая сетка/набор материалов",
                "property block"=>"Дополнительные параметры Renderer", "outside radius"=>"Вне радиуса", "outside frustum"=>"Вне кадра",
                _=>reason
            };
            string summary=$"Чанков: {all.Length}; радиус от камеры: {radius.ToString("0.#",CultureInfo.InvariantCulture)} м.\n"+
                string.Join("; ",all.GroupBy(c=>c.reason).Select(g=>$"{Label(g.Key)}: {g.Count()}"))+".";
            var candidates=all.Where(c=>c.reason=="selected"||c.reason=="outside radius"||c.reason=="outside frustum").ToArray();
            if(!all.Any(c=>c.reason=="selected"))
            {
                if(candidates.Length>0)
                {
                    double nearest=candidates.Min(c=>c.distance);
                    summary+=$"\nБлижайший допустимый чанк: {nearest.ToString("0.#",CultureInfo.InvariantCulture)} м.";
                    summary+=nearest>radius?" Приблизьте камеру Scene View к земле или увеличьте радиус.":" Наведите камеру Scene View на многослойную землю.";
                }
                else summary+="\nПеремещение камеры не снимет эти ограничения. Нужен многослойный чанк без указанных исключений.";
            }
            return summary;
        }
        // Restore only slots that still hold our temporary material. Preserve
        // external replacements, including array size/order changes.
        public static T[] Restore<T>(T[] current,T[] original,T owned) where T:class
        {
            var result=(T[])current.Clone();
            for(int i=0;i<result.Length;i++)
                if(ReferenceEquals(result[i],owned))result[i]=i<original.Length?original[i]:null;
            return result;
        }
    }
}
