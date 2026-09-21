using UnityEngine;

namespace LocalTerrainPrototype
{
    public static class LTDeformationMath
    {
        public static float UpdateInterval(float distance,float near,float far)
            =>distance<=Mathf.Max(1,near)?1f/30:distance<=Mathf.Max(near+1,far)?.1f:.5f;
        public static Vector3 Prediction(Vector3 velocity,float seconds)
            =>Vector3.ClampMagnitude(velocity*Mathf.Clamp(seconds,0,.5f),3);
        public static Vector3 ClosestSphere(Vector3 point,Vector3 center,float radius)
        {
            var delta=point-center;float squared=delta.sqrMagnitude;
            return squared<=radius*radius?point:center+delta*(radius/Mathf.Sqrt(squared));
        }
        public static Vector3 ClosestCapsule(Vector3 point,Vector3 a,Vector3 b,float radius)
        {
            var axis=b-a;float length=axis.sqrMagnitude;
            float t=length>1e-12f?Mathf.Clamp01(Vector3.Dot(point-a,axis)/length):0;
            return ClosestSphere(point,a+axis*t,radius);
        }
        public static Vector3 ClosestBox(Vector3 point,Vector3 center,Vector3 x,Vector3 y,Vector3 z,Vector3 half)
        {
            var delta=point-center;
            return center+x*Mathf.Clamp(Vector3.Dot(delta,x),-half.x,half.x)
                +y*Mathf.Clamp(Vector3.Dot(delta,y),-half.y,half.y)
                +z*Mathf.Clamp(Vector3.Dot(delta,z),-half.z,half.z);
        }
        public static int BlockIndex(int x,int y,int width,int block)
            =>y/block*((width+block-1)/block)+x/block;
        public static float ActivityFactor(Rect area,float x,float z,float stepX,float stepZ,float maximum)
        {
            float distance=Mathf.Max(Mathf.Max(area.xMin-x,x-area.xMax)/stepX,
                Mathf.Max(area.yMin-z,z-area.yMax)/stepZ);
            return 1+(maximum-1)*(1-Mathf.Clamp01(distance));
        }
        public static float FadeFactor(float current,float target,float dt)
            =>target>=current?target:Mathf.MoveTowards(current,target,126*Mathf.Max(0,dt));
        public static RectInt BlockRect(int index,int width,int height,int block)
        {
            int columns=(width+block-1)/block,x=index%columns*block,y=index/columns*block;
            return new RectInt(x,y,Mathf.Min(block,width-x),Mathf.Min(block,height-y));
        }
        public static int Resolution(float metres,float stepCm,int maximum)
            =>Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(.001f,metres)/Mathf.Max(.005f,stepCm*.01f))+1,2,Mathf.Clamp(maximum,128,2048));
        public static float Contact(float horizontal,float vertical,float softness,float tolerance)
            =>(1-Mathf.SmoothStep(0,1,horizontal/Mathf.Max(.001f,softness)))*
              (1-Mathf.SmoothStep(0,1,vertical/Mathf.Max(.001f,tolerance)));
        // settings = max depth, full-depth recovery seconds, tessellation factor.
        // Same height blend and dominance thresholds as ordinary displacement.
        public static Vector4 Controls(float[] weights,float[] heights,Vector4[] settings,float blend)
        {
            float highest=-1,total=0,visible=0,depth=0,recovery=0,factor=1;
            for(int i=0;i<8;i++)if(weights[i]>.00001f)highest=Mathf.Max(highest,heights[i]+weights[i]);
            for(int i=0;i<8;i++)
            {
                float w=weights[i];
                if(blend>.0001f)w*=Mathf.Lerp(1,Mathf.Clamp01((heights[i]+w-highest+.2f)/.2f),blend);
                total+=w;
                if(settings[i].x<=0||w<=.00001f)continue;
                visible+=w;depth+=w*settings[i].x;
                if(settings[i].y>0)recovery+=w*settings[i].x/settings[i].y;
                factor=Mathf.Max(factor,settings[i].z);
            }
            total=Mathf.Max(total,.00001f);
            float coverage=visible/total;
            float fade=Mathf.SmoothStep(0,1,Mathf.InverseLerp(.5f,.65f,coverage));
            return new Vector4(depth/total*fade,recovery/total*fade,fade>0?factor:1,coverage);
        }
        public static float Recover(float depth,float speed,float dt)=>Mathf.Max(0,depth-Mathf.Max(0,speed)*Mathf.Max(0,dt));
        public static float Press(float depth,float limit,float speed,float contact,float dt)
            =>Mathf.Min(Mathf.Max(0,limit),Mathf.Max(0,depth)+Mathf.Max(0,speed)*Mathf.Clamp01(contact)*Mathf.Max(0,dt));
    }
}
