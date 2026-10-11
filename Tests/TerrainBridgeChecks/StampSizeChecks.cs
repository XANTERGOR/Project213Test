using System;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void StampSizeChecks()
    {
        void Near(Vector2 a, Vector2 b, string label)
        {
            float tolerance = Math.Max(1, Math.Max(Math.Abs(b.x), Math.Abs(b.y))) * .00001f;
            Require(Math.Abs(a.x-b.x) <= tolerance && Math.Abs(a.y-b.y) <= tolerance, $"Stamp size {label}: {a} != {b}");
        }
        var original = new Vector2(8, 3);
        foreach (int sign in new[] { -1, 1 })
        {
            Near(LTStampSizeMath.Edge(original, new Vector3(sign*6, 200, 0), 0, sign), new Vector2(12,3), "width edge");
            Near(LTStampSizeMath.Edge(original, new Vector3(0, -400, sign*2), 1, sign), new Vector2(8,4), "length edge");
            Near(LTStampSizeMath.Edge(original, new Vector3(-sign*6, 0, 0), 0, sign), new Vector2(.01f,3), "crossing centre clamps, does not mirror");
        }
        int cases = 0;
        var random = new System.Random(19520);
        for (int n = 0; n < 2500; n++)
        {
            var start = new Vector2(.1f+(float)random.NextDouble()*80, .1f+(float)random.NextDouble()*80);
            float factor = .05f+(float)random.NextDouble()*8;
            double angle = random.NextDouble()*Math.PI*2, cos = Math.Cos(angle), sin = Math.Sin(angle);
            double sx = .1+random.NextDouble()*20, sz = .1+random.NextDouble()*20;
            foreach (int xSign in new[] { -1,1 }) foreach (int zSign in new[] { -1,1 })
            {
                // Simulate rotated, non-uniformly scaled (including parent scale)
                // XZ footprints. Translation/height cannot alter local Size.
                double px = xSign*start.x*.5*factor, pz = zSign*start.y*.5*factor;
                double wx = 700+cos*px*sx+sin*pz*sz, wz = -300-sin*px*sx+cos*pz*sz;
                var local = new Vector3((float)((cos*(wx-700)-sin*(wz+300))/sx), 1000, (float)((sin*(wx-700)+cos*(wz+300))/sz));
                var size = LTStampSizeMath.Corner(start, local, xSign, zSign);
                float expectedFactor = Math.Max(factor, Math.Max(.01f/start.x, .01f/start.y));
                Near(size, start*expectedFactor, "corner under rotation + non-uniform scale");
                Require(Math.Abs((double)size.x/size.y-(double)start.x/start.y) <= Math.Max(1,(double)start.x/start.y)*.000001, "aspect retained");
                cases++;
            }
        }
        foreach (var start in new[] { new Vector2(8,3), new Vector2(.01f,20), new Vector2(500,.01f) })
        {
            var min = LTStampSizeMath.Corner(start, new Vector3(-100,0,-100), 1,1);
            Require(LTStampSizeMath.Valid(min), "corner minimum valid");
            Require(Math.Min(min.x,min.y) <= .010001f, "corner crosses centre to smallest aspect-preserving size");
            Near(min, start * Math.Max(.01f/start.x,.01f/start.y), "corner clamp preserves ratio");
            var restore = LTStampSizeMath.Corner(start, new Vector3(start.x*.5f,0,start.y*.5f),1,1);
            Near(restore,start,"returning to mouse-down point restores size");
        }
        // Repeating a gesture evaluates from its fixed mouse-down basis.
        var point = new Vector3(11,0,4.125f);
        var once = LTStampSizeMath.Corner(original,point,1,1);
        for (int i=0;i<1000;i++) Near(LTStampSizeMath.Corner(original,point,1,1),once,"no per-frame aspect drift");
        foreach (float bad in new[] { float.NaN,float.PositiveInfinity,float.NegativeInfinity })
        {
            Near(LTStampSizeMath.Edge(original,new Vector3(bad,0,0),0,1),original,"reject non-finite edge");
            Near(LTStampSizeMath.Corner(original,new Vector3(bad,0,3),1,1),original,"reject non-finite corner");
            Require(!LTStampSizeMath.Valid(new Vector2(bad,1)),"invalid input size rejected");
        }
        Near(LTStampSizeMath.Edge(original,new Vector3(2,0,4),2,1),original,"invalid axis");
        Near(LTStampSizeMath.Corner(original,new Vector3(2,0,4),0,1),original,"invalid corner sign");
        Require(!LTStampSizeMath.Valid(new Vector2(0,1))&&!LTStampSizeMath.Valid(new Vector2(-1,1)),"zero/negative size rejected");
        var large = new Vector2(1e20f,2e20f);
        Near(LTStampSizeMath.Corner(large,new Vector3(1e20f,0,2e20f),1,1),large*2,"large diagonal avoids float-square overflow");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++) { LTStampSizeMath.Corner(original,point,1,1); LTStampSizeMath.Edge(original,point,0,1); }
        Require(GC.GetAllocatedBytesForCurrentThread()==before,"size math is allocation-free");
        Console.WriteLine($"STAMP SIZE PASS: {cases} rotated/nonuniform-scale corner cases; edge axes/signs, fixed aspect, minimum/clamp, invalid input, large sizes, no per-call allocation. Undo/UI interaction requires Unity.");
    }
}
