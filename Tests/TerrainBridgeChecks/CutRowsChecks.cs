using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
partial class Checks
{
    sealed class Stamp
    {
        public bool meshCutTerrain=true,meshTrimRock=true;
        public Vector3[] meshVertices=Array.Empty<Vector3>();
        public int[] meshAllTriangles=Array.Empty<int>();
        public List<Vector3> contactSegments=new List<Vector3>();
        public List<int>[] cutRows;
        public float cutRowMin,cutRowStep,meshCutOffset;
    }
        static bool InsideRockCut(Stamp s,float x,float z,float terrainHeight)
        {
            if(!s.meshCutTerrain||s.meshVertices==null||s.meshAllTriangles==null)return false;
            if(s.meshTrimRock&&s.contactSegments!=null)
            {
                bool inside=false;float closest=float.MaxValue;
                Vector2 query=new Vector2(x,z);
                var candidates=s.cutRows==null?null:s.cutRows[Mathf.Clamp(Mathf.FloorToInt((z-s.cutRowMin)/s.cutRowStep),0,s.cutRows.Length-1)];
                int count=candidates==null?s.contactSegments.Count/2:candidates.Count;
                for(int j=0;j<count;j++)
                {
                    int i=candidates==null?j*2:candidates[j];
                    Vector3 av=s.contactSegments[i],bv=s.contactSegments[i+1];
                    Vector2 a=new Vector2(av.x,av.z),b=new Vector2(bv.x,bv.z),ab=b-a;
                    float t=ab.sqrMagnitude>1e-12f?Mathf.Clamp01(Vector2.Dot(query-a,ab)/ab.sqrMagnitude):0;
                    closest=Mathf.Min(closest,(query-a-t*ab).sqrMagnitude);
                    if((a.y>z)!=(b.y>z)&&x<(b.x-a.x)*(z-a.y)/(b.y-a.y)+a.x)inside=!inside;
                }
                float offset=s.meshCutOffset;
                return inside||closest<offset*offset;
            }
            Vector2 p=new Vector2(x,z);bool projected=false;
            float bottom=float.MaxValue,top=float.MinValue;
            for(int i=0;i+2<s.meshAllTriangles.Length;i+=3)
            {
                Vector3 a=s.meshVertices[s.meshAllTriangles[i]],b=s.meshVertices[s.meshAllTriangles[i+1]],c=s.meshVertices[s.meshAllTriangles[i+2]];
                Vector2 aa=new Vector2(a.x,a.z),bb=new Vector2(b.x,b.z),cc=new Vector2(c.x,c.z);
                float den=(bb.y-cc.y)*(aa.x-cc.x)+(cc.x-bb.x)*(aa.y-cc.y);
                if(Mathf.Abs(den)<.0000001f)continue;
                float u=((bb.y-cc.y)*(p.x-cc.x)+(cc.x-bb.x)*(p.y-cc.y))/den;
                float v=((cc.y-aa.y)*(p.x-cc.x)+(aa.x-cc.x)*(p.y-cc.y))/den;
                if(u>=-.0001f&&v>=-.0001f&&u+v<=1.0001f)
                {
                    float hit=u*a.y+v*b.y+(1-u-v)*c.y;
                    bottom=Mathf.Min(bottom,hit);top=Mathf.Max(top,hit);projected=true;
                }
            }
            // The terrain is cut only where its original height passes through the
            // vertical rock volume. A mere XZ projection must not create an open hole
            // below an overhang or a part of the rock that has already been trimmed.
            if(!projected||terrainHeight<bottom-.001f||terrainHeight>top+.001f)return false;
            return true;
        }
        static void BuildCutRows(Stamp stamp)
        {
            var segments=stamp.contactSegments;stamp.cutRows=null;
            if(segments==null||segments.Count<2)return;
            float pad=Mathf.Abs(stamp.meshCutOffset)+.001f;
            float min=segments.Min(p=>p.z)-pad,max=segments.Max(p=>p.z)+pad;
            int count=Math.Min(64,Math.Max(1,segments.Count/2));
            stamp.cutRowMin=min;stamp.cutRowStep=Mathf.Max(.001f,(max-min)/count);
            stamp.cutRows=new List<int>[count];
            for(int row=0;row<count;row++)stamp.cutRows[row]=new List<int>();
            for(int i=0;i+1<segments.Count;i+=2)
            {
                // Include every ray crossing and every segment within the offset.
                // Extra rows are conservative guards against float boundary rounding.
                int lo=Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(segments[i].z,segments[i+1].z)-pad-min)/stamp.cutRowStep)-1,0,count-1);
                int hi=Mathf.Clamp(Mathf.FloorToInt((Mathf.Max(segments[i].z,segments[i+1].z)+pad-min)/stamp.cutRowStep)+1,0,count-1);
                for(int row=lo;row<=hi;row++)stamp.cutRows[row].Add(i);
            }
        }

    static void CutRowsChecks()
    {
        var random=new System.Random(1729);int tested=0;
        foreach(float offset in new[]{0f,.01f,.5f,5f,20f})
        {
            var s=new Stamp{meshCutOffset=offset};
            for(int i=0;i<128;i++)
            {
                Vector3 P(int k){double a=k*Math.PI*2/128;double r=10+3*Math.Cos(7*a);return new Vector3(200+(float)(r*Math.Cos(a)),0,95+(float)(r*Math.Sin(a)));}
                s.contactSegments.Add(P(i));s.contactSegments.Add(P(i+1));
            }
            BuildCutRows(s);var rows=s.cutRows;
            for(int i=0;i<20000;i++)
            {
                float x=160+(float)random.NextDouble()*80,z=55+(float)random.NextDouble()*80;
                if(i< s.contactSegments.Count){x=s.contactSegments[i].x;z=s.contactSegments[i].z;}
                bool indexed=InsideRockCut(s,x,z,0);s.cutRows=null;
                bool original=InsideRockCut(s,x,z,0);s.cutRows=rows;
                Require(indexed==original,"cut row index mismatch");tested++;
            }
        }
        Console.WriteLine("PASS cut rows: "+tested+" indexed/full-scan comparisons, offsets and contour vertices.");
    }
}
