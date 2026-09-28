// Opt-in only: no changes to the main test runner or project are required.
// Define ROAD_MATH_CHECKS when integrating Run(); define ROAD_MATH_STANDALONE too
// for an isolated csc executable referencing only UnityEngine.CoreModule and the three road sources.
#if ROAD_MATH_CHECKS
using System;
using System.Collections.Generic;
using LocalTerrainPrototype;
using UnityEngine;

public static class RoadMathChecks
{
    static int assertions;
    static void Require(bool condition, string message)
    { assertions++; if (!condition) throw new Exception("Road math: " + message); }
    static void Near(float actual, float expected, string message, float tolerance = .0001f)
        => Require(Math.Abs(actual - expected) <= tolerance, message + ": " + actual + " vs " + expected);
    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException ex) { rejected = ex.Message.Contains("LTRoad:"); }
        Require(rejected, message);
    }
    static LTRoadPoint[] Straight(float bank = 0) => new[]
    {
        new LTRoadPoint(new Vector3(0, 10, 0), bank),
        new LTRoadPoint(new Vector3(0, 10, 10), bank),
        new LTRoadPoint(new Vector3(0, 10, 20), bank)
    };
    static LTRoadMath.Snapshot Build(LTRoadMath.Settings settings)
        => LTRoadMath.Build(Straight(), Matrix4x4.identity, settings);

    public static void Run()
    {
        assertions = 0;
        HeightsAndMasks(); TextureCoordinates(); TextureBakeRegions(); CurvesAndNearest(); Validation(); HashesAndCopies(); Allocations();
#if !ROAD_MATH_STANDALONE
        RoadOptimizationChecks.Run();
        NextOptimizationChecks.Run();
#endif
        Console.WriteLine("PASS RoadMathChecks: " + assertions + " assertions; managed math only, no Unity native runtime.");
    }

    static void TextureCoordinates()
    {
        var settings=LTRoadMath.Settings.Default;
        settings.projection=LTRoadProjection.Spline;settings.textureRepeatMetres=4;
        settings.textureOffset=new Vector2(.2f,-.3f);
        var road=Build(settings);
        foreach(float z in new[]{-10f,-.01f,0f,.01f,8f,19.99f,20f,20.01f,30f})
        foreach(float x in new[]{-2f,0f,2f})
        {
            Require(road.TryTextureCoordinates(x,z,out var uv,out var right),"straight UV query");
            Near(uv.x,x/settings.width+.5f+.2f,"lateral UV including outside caps");
            Near(uv.y,z/4-.3f,"unwrapped V continues through both endpoints");
            Near(right.x,1,"UV right X");Near(right.y,0,"UV right Z");
            Require(road.TrySample(x,z,out var hit),"geometry query remains available");
            Near(hit.distance,Math.Max(0,Math.Min(20,z)),"geometry arc remains capped");
            Near(hit.position.z,Math.Max(0,Math.Min(20,z)),"geometry position remains capped");
            float beyond=z<0?-z:z>20?z-20:0;
            Near(hit.radialDistance,(float)Math.Sqrt(x*x+beyond*beyond),"geometry retains radial cap distance");
        }
        Near(road.PaintWeight(0,-10),0,"UV extension does not extend start paint");
        Near(road.PaintWeight(0,30),0,"UV extension does not extend end paint");
        Near(road.ApplyHeight(0,-10,100),100,"UV extension does not extend terrain flattening");
        foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity})
        {
            Require(!road.TryTextureCoordinates(invalid,0,out _,out _),"invalid UV X rejected");
            Require(!road.TryTextureCoordinates(0,invalid,out _,out _),"invalid UV Z rejected");
        }
        // Sloped, translated road: arc length is 3D while endpoint projection is XZ.
        var slope=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(4,0,7)),
            new LTRoadPoint(new Vector3(24,15,7))},Matrix4x4.identity,settings);
        foreach(float along in new[]{-4f,0f,10f,20f,24f})
        {
            Require(slope.TryTextureCoordinates(4+along,5,out var uv,out var right),"sloped endpoint UV");
            Near(uv.x,2/settings.width+.7f,"rotated lateral UV");
            Near(uv.y,along*1.25f/4-.3f,"sloped arc-length continuation",.001f);
            Near(right.x,0,"rotated right X");Near(right.y,-1,"rotated right Z");
        }
    }

    static void TextureBakeRegions()
    {
        var settings=LTRoadMath.Settings.Default;settings.projection=LTRoadProjection.Spline;
        settings.textureRepeatMetres=3;settings.textureOffset=new Vector2(.17f,-.31f);
        var road=Build(settings);
        foreach(var chunk in new[]{new Rect(-64,-54,128,128),new Rect(-3,0,16,12),
            new Rect(-16,20,16,8),new Rect(-1,-2,2,24)})
        foreach(int resolution in new[]{33,65,257})
        {
            var region=road.TextureBakeRegion(chunk,resolution);
            Require(region.xMin>=0&&region.yMin>=0&&region.xMax<=resolution&&region.yMax<=resolution,
                "bake region is clamped half-open texel bounds");
            if(chunk.width==128)Require(region.width*region.height<resolution*resolution/4,"typical road bakes under quarter of chunk");
            var full=new Vector4[resolution*resolution];var roi=new Vector4[full.Length];
            bool Inside(int x,int z)=>x>=region.xMin&&x<region.xMax&&z>=region.yMin&&z<region.yMax;
            for(int z=0;z<resolution;z++)for(int x=0;x<resolution;x++)
            {
                float px=chunk.xMin+chunk.width*x/(resolution-1),pz=chunk.yMin+chunk.height*z/(resolution-1);
                Require(road.TryTextureCoordinates(px,pz,out var uv,out var right),"full-grid UV available");
                var value=new Vector4(uv.x,uv.y,right.x,right.y);full[z*resolution+x]=value;
                if(Inside(x,z))roi[z*resolution+x]=value;
                if(road.PaintWeight(px,pz)<=0)continue;
                for(int dz=-1;dz<=1;dz++)for(int dx=-1;dx<=1;dx++)
                    Require(Inside(Math.Max(0,Math.Min(resolution-1,x+dx)),Math.Max(0,Math.Min(resolution-1,z+dz))),
                        "positive paint texels and one guard lie in bake region");
            }
            Vector4 Sample(Vector4[] values,float x,float z)
            {
                float gx=Math.Max(0,Math.Min(1,(x-chunk.xMin)/chunk.width))*(resolution-1);
                float gz=Math.Max(0,Math.Min(1,(z-chunk.yMin)/chunk.height))*(resolution-1);
                int ix=Math.Min(resolution-2,(int)gx),iz=Math.Min(resolution-2,(int)gz),at=iz*resolution+ix;
                return Vector4.LerpUnclamped(Vector4.LerpUnclamped(values[at],values[at+1],gx-ix),
                    Vector4.LerpUnclamped(values[at+resolution],values[at+resolution+1],gx-ix),gz-iz);
            }
            int compared=0;
            void Compare(float x,float z)
            {
                if(x<chunk.xMin||x>chunk.xMax||z<chunk.yMin||z>chunk.yMax||road.PaintWeight(x,z)<=0)return;
                var expected=Sample(full,x,z);var actual=Sample(roi,x,z);
                Require((expected-actual).sqrMagnitude<1e-10f,"ROI bilinear UV and right match full grid at paint footprint/boundary");
                compared++;
            }
            // Fractional grid positions include interior and all four chunk edges.
            for(int z=0;z<=resolution*2;z++)for(int x=0;x<=resolution*2;x++)
                Compare(chunk.xMin+chunk.width*x/(resolution*2),chunk.yMin+chunk.height*z/(resolution*2));
            foreach(float z in new[]{-3.999f,-.001f,0f,.001f,19.999f,20f,20.001f,23.999f})
            foreach(float x in new[]{-3.999f,-3f,0f,3f,3.999f})Compare(x,z);
            Require(compared>0,"ROI fixture exercises positive paint");
        }
        var empty=road.TextureBakeRegion(new Rect(100,100,10,10),65);
        Require(empty.width==0&&empty.height==0,"nonintersecting road has empty bake region");
        Reject(()=>road.TextureBakeRegion(new Rect(0,0,1,1),1),"invalid bake resolution rejected");
        Reject(()=>road.TextureBakeRegion(new Rect(0,0,0,1),65),"zero-width bake chunk rejected");
        Reject(()=>road.TextureBakeRegion(new Rect(float.NaN,0,1,1),65),"nonfinite bake chunk rejected");
        Console.WriteLine("PASS road UV ROI: full-grid bilinear equivalence, footprint guards, chunk/cap boundaries, rectangular chunks and bounded work.");
    }

    static void HeightsAndMasks()
    {
        var settings = LTRoadMath.Settings.Default;
        var road = Build(settings);
        Require(road.TrySample(2, 8, out var hit), "nearest exists");
        Near(hit.position.x, 0, "centre X"); Near(hit.position.z, 8, "centre Z");
        Near(hit.lateral, 2, "signed lateral"); Near(hit.radialDistance, 2, "radial");
        Near(hit.distance, 8, "distance from start"); Near(hit.right.x, 1, "right orientation");
        Near(road.ApplyHeight(0, 8, 100), 10, "full flatten");
        Near(road.ApplyHeight(5, 8, 100), 55, "shoulder+blend transition");
        Near(road.ApplyHeight(8, 8, 100), 100, "outside unchanged");
        Near(road.PaintWeight(3, 8), 1, "solid full road");
        Near(road.PaintWeight(3.5f, 8), .5f, "smooth shoulder");
        Near(road.PaintWeight(0, -3.5f), .5f, "rounded start cap");
        Near(road.PaintWeight(0, 23.5f), .5f, "rounded end cap");
        Near(road.PaintWeight(0, 25), 0, "finite endpoint");
        Require(road.Intersects(new Rect(-1, 19, 2, 2)), "bounds intersect");
        Require(!road.Intersects(new Rect(100, 100, 2, 2)), "bounds reject");
        Require(!road.TrySample(float.NaN, 0, out _), "NaN query rejected");
        Near(road.PaintWeight(float.PositiveInfinity, 0), 0, "infinite weight rejected");
        Near(road.ClearWeight(0, 8, LTDetailCategory.Stones), 0, "stone default preserved");
        Near(road.ClearWeight(0, 8, LTDetailCategory.Other), 0, "other preserved");

        settings.pattern = LTRoadPattern.Tracks;
        settings.edgeNoise = 0;
        road = Build(settings);
        Near(road.PaintWeight(0, 8), 0, "central grass strip preserved");
        Near(road.PaintWeight(.9f, 8), 1, "right rut centre");
        Near(road.PaintWeight(-.9f, 8), 1, "left rut centre");
        Near(road.ApplyHeight(.9f, 8, 100), 9.92f, "rut depression");
        Near(road.ApplyHeight(0, 8, 100), 10, "centre height no depression");
        Near(road.PaintWeight(.9f, -.1f), 1, "round track cap");
        Near(road.PaintWeight(.9f, -.3f), 0, "track cap terminates");
        var cleanTracks = road;
        settings.edgeNoise = 1;
        road = Build(settings);
        bool noiseChanged = false;
        for (int z = -5; z <= 205; z++) for (int x = -40; x <= 40; x++)
        {
            float px = x * .05f, pz = z * .1f;
            float paint = road.PaintWeight(px, pz);
            Near(road.ClearWeight(px, pz, LTDetailCategory.Vegetation), paint, "track clearing equals paint");
            Require(paint >= 0 && paint <= 1, "bounded paint");
            if (Math.Abs(px) < .625f) Near(paint, 0, "noise preserves central gap");
            if (Math.Abs(paint - cleanTracks.PaintWeight(px, pz)) > .01f) noiseChanged = true;
        }
        Require(noiseChanged, "noise changes strip edge");
        settings.vegetationFade = .4f; settings.clearStones = true;
        road = Build(settings);
        Near(road.ClearWeight(.9f, 8, LTDetailCategory.Vegetation), road.PaintWeight(.9f, 8) * .4f, "vegetation strength");
        Near(road.ClearWeight(.9f, 8, LTDetailCategory.Stones), road.PaintWeight(.9f, 8), "stone removal independent of vegetation strength");
        settings.flatten = 0;
        Near(Build(settings).ApplyHeight(.9f, 8, 100), 100, "zero flatten disables ruts");

        settings.mode = LTRoadMode.Asphalt;
        road = LTRoadMath.Build(Straight(10), Matrix4x4.identity, settings);
        Require(road.TrySample(2, 8, out hit), "banked hit");
        float target = 10 + 2 * (float)Math.Tan(10 * Math.PI / 180);
        Near(road.SurfaceHeight(hit, 2), target, "bank crossfall");
        Near(road.ApplyHeight(2, 8, 100), target, "asphalt forces flatten");
        Near(road.ApplyHeight(2, 8, target), target, "asphalt repeat does not accumulate offset");
        Near(road.SurfaceHeight(hit, 2) + road.surfaceOffset - road.ApplyHeight(2, 8, 100), .06f, "visible offset exactly once");
        Near(road.PaintWeight(0, 8), 1, "asphalt ignores tracks");
        Near(road.ClearWeight(0, 8, LTDetailCategory.Stones), 1, "asphalt clears centre");
        Near(road.ClearWeight(3.5f, 8, LTDetailCategory.Stones), .5f, "asphalt shoulder clearing");
    }

    static void CurvesAndNearest()
    {
        var points = new[] { new LTRoadPoint(new Vector3(-8, 1, 0)),
            new LTRoadPoint(new Vector3(0, 2, 10), 8), new LTRoadPoint(new Vector3(15, 4, 12), -5),
            new LTRoadPoint(new Vector3(25, 6, 25), 3) };
        var settings = LTRoadMath.Settings.Default;
        settings.sampleSpacing = .4f;
        var road = LTRoadMath.Build(points, Matrix4x4.identity, settings);
        Near((road.samples[0].position - points[0].position).magnitude, 0, "open first point");
        Near((road.samples[road.samples.Count - 1].position - points[3].position).magnitude, 0, "open last point");
        for (int i = 1; i < road.samples.Count; i++)
        {
            var a = road.samples[i - 1]; var b = road.samples[i];
            Require((b.position - a.position).magnitude <= settings.sampleSpacing + .00001f, "spacing bound");
            Require(b.distance > a.distance, "distance monotonic");
            Near(b.right.y, 0, "right horizontal"); Near(b.right.magnitude, 1, "unit right");
            Require(Vector3.Dot(a.right, b.right) > 0, "stable right");
        }
        var random = new System.Random(715);
        for (int q = 0; q < 1000; q++)
        {
            float x = (float)random.NextDouble() * 70 - 25, z = (float)random.NextDouble() * 70 - 20;
            float best = float.PositiveInfinity;
            for (int i = 0; i < road.samples.Count - 1; i++)
            {
                var a = road.samples[i].position; var b = road.samples[i + 1].position;
                float dx = b.x - a.x, dz = b.z - a.z;
                float t = Math.Max(0, Math.Min(1, ((x - a.x) * dx + (z - a.z) * dz) / (dx * dx + dz * dz)));
                float rx = x - (a.x + dx * t), rz = z - (a.z + dz * t);
                best = Math.Min(best, rx * rx + rz * rz);
            }
            Require(road.TrySample(x, z, out var hit), "curve query");
            Near(hit.radialDistance, (float)Math.Sqrt(best), "tree agrees with brute force");
        }
        var matrix = Matrix4x4.identity;
        matrix.m00 = 0; matrix.m02 = 1; matrix.m20 = -1; matrix.m22 = 0;
        matrix.m03 = 4; matrix.m13 = -3; matrix.m23 = 7;
        var transformed = LTRoadMath.Build(Straight(10), matrix, settings);
        Require(transformed.TrySample(12, 5, out var rotated), "transformed hit");
        Near(rotated.position.x, 12, "yaw X"); Near(rotated.position.z, 7, "translation Z");
        Near(rotated.position.y, 7, "terrain local height"); Near(rotated.right.z, -1, "yaw right");
        Near(rotated.lateral, 2, "yaw lateral"); Near(rotated.bank, 10, "yaw bank unchanged");
    }

    static void Validation()
    {
        var settings = LTRoadMath.Settings.Default;
        Reject(() => LTRoadMath.Build(null, Matrix4x4.identity, settings), "null path");
        Reject(() => LTRoadMath.Build(new LTRoadPoint[1], Matrix4x4.identity, settings), "one point");
        Reject(() => LTRoadMath.Build(new LTRoadPoint[LTRoadMath.MaxPoints + 1], Matrix4x4.identity, settings), "point cap");
        Reject(() => LTRoadMath.Build(new[] { new LTRoadPoint(Vector3.zero), new LTRoadPoint(Vector3.up) }, Matrix4x4.identity, settings), "vertical span");
        Reject(() => LTRoadMath.Build(new[] { new LTRoadPoint(Vector3.zero), new LTRoadPoint(Vector3.forward), new LTRoadPoint(Vector3.zero) }, Matrix4x4.identity, settings), "backtracking");
        var points = Straight(); points[1].position.x = float.NaN;
        Reject(() => LTRoadMath.Build(points, Matrix4x4.identity, settings), "NaN point");
        settings.sampleSpacing = float.NaN;
        Reject(() => Build(settings), "NaN setting");
        settings = LTRoadMath.Settings.Default; settings.pattern = LTRoadPattern.Tracks; settings.rutWidth = 2;
        Reject(() => Build(settings), "overlapping tracks");
        settings = LTRoadMath.Settings.Default; settings.sampleSpacing = .01f;
        Reject(() => LTRoadMath.Build(new[] { new LTRoadPoint(Vector3.zero), new LTRoadPoint(new Vector3(0, 0, 1000)) }, Matrix4x4.identity, settings), "sample cap");
        foreach (bool world in new[] { false, true })
        {
            var matrix = Matrix4x4.identity; matrix.m00 = 2;
            Reject(() => LTRoadMath.ValidateTransform(matrix, world, "Test"), "nonunit scale");
            matrix = Matrix4x4.identity; matrix.m01 = .3f;
            Reject(() => LTRoadMath.ValidateTransform(matrix, world, "Test"), "shear/pitch/roll");
            matrix = Matrix4x4.identity; matrix.m00 = -1;
            Reject(() => LTRoadMath.ValidateTransform(matrix, world, "Test"), "reflection");
        }
        var yaw = Matrix4x4.identity; yaw.m00 = 0; yaw.m02 = 1; yaw.m20 = -1; yaw.m22 = 0;
        Reject(() => LTRoadMath.ValidateTransform(yaw, true, "World"), "rotated world");
    }

    static void HashesAndCopies()
    {
        var settings = LTRoadMath.Settings.Default;
        var points = Straight();
        var baseline = LTRoadMath.Build(points, Matrix4x4.identity, settings);
        points[0].position.y = 999;
        Near(baseline.samples[0].position.y, 10, "snapshot owns point copy");
        var sample = baseline.samples[0]; sample.position.y = 800;
        Near(baseline.samples[0].position.y, 10, "sample copy cannot mutate snapshot");
        Require(((IList<LTRoadMath.Sample>)baseline.samples).IsReadOnly, "samples read only");
        var copy = Build(settings);
        Require(copy.geometryHash == baseline.geometryHash && copy.paintHash == baseline.paintHash && copy.detailHash == baseline.detailHash, "deterministic hashes");
        settings.groundLayerId = 999;
        copy = Build(settings);
        Require(copy.geometryHash == baseline.geometryHash && copy.detailHash == baseline.detailHash && copy.paintHash != baseline.paintHash, "layer paint only");
        settings = LTRoadMath.Settings.Default; settings.projection = LTRoadProjection.Spline;
        copy = Build(settings);
        Require(copy.geometryHash == baseline.geometryHash && copy.detailHash == baseline.detailHash && copy.paintHash != baseline.paintHash, "projection paint only");
        settings = LTRoadMath.Settings.Default; settings.textureOffset = new Vector2(.2f, .4f);
        copy = Build(settings);
        Require(copy.geometryHash == baseline.geometryHash && copy.detailHash == baseline.detailHash && copy.paintHash != baseline.paintHash, "offset paint only");
        settings = LTRoadMath.Settings.Default; settings.clearStones = true;
        copy = Build(settings);
        Require(copy.geometryHash == baseline.geometryHash && copy.paintHash == baseline.paintHash && copy.detailHash != baseline.detailHash, "clearing detail only");
        settings = LTRoadMath.Settings.Default; settings.meshChunkLength = 16; settings.surfaceOffset = .2f;
        copy = Build(settings);
        Require(copy.geometryHash == baseline.geometryHash && copy.paintHash == baseline.paintHash && copy.detailHash == baseline.detailHash, "asphalt-only controls ignored offroad");
        settings = LTRoadMath.Settings.Default; settings.seed++;
        copy = Build(settings);
        Require(copy.geometryHash == baseline.geometryHash && copy.paintHash == baseline.paintHash && copy.detailHash == baseline.detailHash, "noise ignored for solid");
        settings = LTRoadMath.Settings.Default; settings.sourceTransformHash++;
        copy = Build(settings);
        Require(copy.geometryHash != baseline.geometryHash && copy.paintHash != baseline.paintHash && copy.detailHash != baseline.detailHash, "full world transform affects all hashes");
        settings = LTRoadMath.Settings.Default;
        var matrix = Matrix4x4.identity; matrix.m13 = 4;
        copy = LTRoadMath.Build(Straight(), matrix, settings);
        Require(copy.geometryHash != baseline.geometryHash && copy.paintHash != baseline.paintHash && copy.detailHash != baseline.detailHash, "relative transform affects all hashes");
        settings = LTRoadMath.Settings.Default; settings.projection = LTRoadProjection.Spline;
        baseline = Build(settings); settings.textureRepeatMetres = 2;
        copy = Build(settings);
        Require(copy.geometryHash == baseline.geometryHash && copy.detailHash == baseline.detailHash && copy.paintHash != baseline.paintHash, "offroad spline repeat paint only");
        settings = LTRoadMath.Settings.Default; settings.pattern = LTRoadPattern.Tracks;
        baseline = Build(settings); settings.seed++;
        copy = Build(settings);
        Require(copy.geometryHash != baseline.geometryHash && copy.paintHash != baseline.paintHash && copy.detailHash != baseline.detailHash, "track noise affects rut height, paint and clearing");
    }

    static void Allocations()
    {
        var settings = LTRoadMath.Settings.Default; settings.pattern = LTRoadPattern.Tracks;
        var road = Build(settings);
        float Evaluate(int i)
        {
            float x = (i % 71) * .1f - 3, z = (i % 257) * .1f - 2;
            road.TrySample(x, z, out var hit);
            road.TryTextureCoordinates(x,z,out var uv,out var right);
            return uv.x + uv.y + right.x + hit.distance + road.PaintWeight(x, z) + road.ApplyHeight(x, z, 12) +
                road.ClearWeight(x, z, LTDetailCategory.Vegetation) + (road.Intersects(new Rect(x, z, 1, 1)) ? 1 : 0);
        }
        for (int i = 0; i < 2000; i++) Evaluate(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        float sum = 0;
        for (int i = 0; i < 10000; i++) sum += Evaluate(i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(sum > 0, "queries evaluated"); Require(allocated == 0, "queries allocate " + allocated + " bytes");
    }

#if ROAD_MATH_STANDALONE
    public static void Main() => Run();
#endif
}

#if ROAD_MATH_STANDALONE
// Compile-only stand-ins for project component dependencies. Never instantiated by these math checks.
namespace LocalTerrainPrototype
{
    public sealed class LTWorld : MonoBehaviour { }
    public sealed class LTPaintStamp : MonoBehaviour { }
    public sealed class LTSurfaceLayer : ScriptableObject { }
    [Flags] public enum LTDetailCategory { Vegetation = 1, Stones = 2, Other = 4, All = 7 }
}
#endif
#endif
