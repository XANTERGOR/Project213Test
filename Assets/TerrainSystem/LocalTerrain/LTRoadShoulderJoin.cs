using System;
using System.Globalization;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Immutable terrain-only handoff. Never supplies vertices, UVs or road width.
    public sealed class LTRoadShoulderJoin
    {
        readonly LTRoadFootprint footprint;
        readonly Vector3 centre,right,forward;
        readonly float nodeShoulder,nodeBlend,roadShoulder,roadBlend,half;
        public readonly float length,reach,radius;
        public readonly Rect bounds;
        public readonly string ContentKey;
        public readonly bool sharedPaint;
        readonly LTDetailCategory clearCategories;
        public LTRoadShoulderJoin(LTRoadJunctionMath.Snapshot node,LTRoadJunctionMath.Port port,
            float shoulder,float blend,float length,bool sharedPaint,LTDetailCategory clearCategories=0)
        {
            if(node?.footprint==null||!Finite(shoulder)||shoulder<0||!Finite(blend)||blend<0||
                !Finite(length)||length<=0||!Finite(port.width)||port.width<=0||
                !Finite(port.centre.x)||!Finite(port.centre.y)||!Finite(port.centre.z)||
                !Finite(port.right.x)||!Finite(port.right.y)||!Finite(port.right.z)||
                Math.Abs(port.right.sqrMagnitude-1)>.001f||Math.Abs(port.right.y)>.001f)
                throw new ArgumentException("Некорректное согласование обочин въезда.");
            footprint=node.footprint;centre=port.centre;right=port.right;
            forward=new Vector3(-right.z,0,right.x);half=port.width*.5f;
            nodeShoulder=node.shoulder;nodeBlend=node.blend;roadShoulder=shoulder;roadBlend=blend;
            this.length=length;this.sharedPaint=sharedPaint;this.clearCategories=clearCategories;
            radius=half+Math.Max(nodeShoulder+nodeBlend,shoulder+blend);
            var b=node.bounds;
            reach=Math.Max(length,Math.Max(Math.Max(Along(b.xMin,b.yMin),Along(b.xMax,b.yMin)),
                Math.Max(Along(b.xMin,b.yMax),Along(b.xMax,b.yMax))))+.001f;
            var a=centre-right*radius;var c=centre+right*radius;var e=a+forward*reach;var f=c+forward*reach;
            bounds=Rect.MinMaxRect(Math.Min(Math.Min(a.x,c.x),Math.Min(e.x,f.x)),Math.Min(Math.Min(a.z,c.z),Math.Min(e.z,f.z)),
                Math.Max(Math.Max(a.x,c.x),Math.Max(e.x,f.x)),Math.Max(Math.Max(a.z,c.z),Math.Max(e.z,f.z)));
            string F(float f)=>f.ToString("R",CultureInfo.InvariantCulture);
            ContentKey="shoulder-join-v1|"+footprint.ContentKey+"|"+string.Join("|",new[]{F(centre.x),F(centre.y),F(centre.z),
                F(right.x),F(right.z),F(half),F(nodeShoulder),F(nodeBlend),F(shoulder),F(blend),F(length),sharedPaint?"1":"0",((int)clearCategories).ToString(CultureInfo.InvariantCulture)});
        }
        public bool Clears(LTDetailCategory category)=>(clearCategories&category)!=0;
        public bool Intersects(Rect r)=>r.xMin<=bounds.xMax&&r.xMax>=bounds.xMin&&r.yMin<=bounds.yMax&&r.yMax>=bounds.yMin;
        float Along(float x,float z)=>(x-centre.x)*forward.x+(z-centre.z)*forward.z;
        public bool Behind(float x,float z)=>Along(x,z)<-.00001f;
        float Across(float x,float z)=>(x-centre.x)*right.x+(z-centre.z)*right.z;
        float NodeWeight(float x,float z,float padding=0)
        {
            float core=nodeShoulder+padding,d=footprint.SurfaceDistance(x,z,core+nodeBlend);
            return Fade(d,core,nodeBlend);
        }
        public bool Contains(float distance,float x,float z)
        {
            float along=Along(x,z);
            return distance>=0&&distance<=reach&&along>=-.00001f&&along<=reach&&Math.Abs(Across(x,z))<=radius;
        }
        public void Widths(float distance,out float shoulder,out float blend)
        {
            float t=Smooth(distance/length);
            shoulder=nodeShoulder+(roadShoulder-nodeShoulder)*t;
            blend=nodeBlend+(roadBlend-nodeBlend)*t;
        }
        public float TerrainWeight(float distance,LTRoadMath.Hit hit,float x,float z,float original)
        {
            float desired=original;
            if(distance<length)
            {
                float t=Smooth(distance/length);Widths(distance,out float s,out float b);
                // Junction has a full-strength shoulder; road uses a feather from
                // the asphalt edge. Interpolate both width AND this cross-section.
                desired=Fade(hit.radialDistance,half+s*(1-t),b+s*t);
                float lateral=RemapLateral(Across(x,z),s+b);
                float seam=NodeWeight(centre.x+right.x*lateral,centre.z+right.z*lateral);
                desired=seam+(desired-seam)*t;
            }
            // The junction is applied before its attached road. Residual alpha
            // gives max(node, desired), not 1-(1-node)*(1-desired): no double berm.
            return Residual(NodeWeight(x,z),desired);
        }
        float RemapLateral(float lateral,float band)
        {
            float offset=Math.Abs(lateral)-half;
            if(offset<=0||nodeShoulder+nodeBlend<=0)return lateral;
            return Math.Sign(lateral)*(half+offset*(nodeShoulder+nodeBlend)/Math.Max(.000001f,band));
        }
        public float Coverage(float distance,LTRoadMath.Hit hit,float x,float z,float original,bool compensate,float strength=1)
        {
            float desired=original;
            if(distance<length)
            {
                float t=Smooth(distance/length);Widths(distance,out float s,out _);
                float lateral=RemapLateral(Across(x,z),s+nodeBlend*(1-t));
                float seam=NodeWeight(centre.x+right.x*lateral,centre.z+right.z*lateral);
                desired=seam+(Fade(hit.radialDistance,half,s)-seam)*t;
            }
            desired*=strength;
            return compensate?Residual(NodeWeight(x,z),desired):desired;
        }
        public static float Residual(float existing,float desired)
        {
            if(existing>=1)return desired>=1?1:0;
            return Math.Max(0,(desired-existing)/(1-existing));
        }
        static float Fade(float d,float core,float feather)=>d<=core?1:feather<=0||float.IsPositiveInfinity(d)?0:1-Smooth((d-core)/feather);
        static float Smooth(float t){t=Math.Max(0,Math.Min(1,t));return t*t*(3-2*t);}
        static bool Finite(float f)=>!float.IsNaN(f)&&!float.IsInfinity(f);
    }
}
