using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    // Read-only diagnostic for two horizontally adjacent saved SpatialLOD assets.
    // Deliberately narrow YAML reader: rejects unsupported vertex streams/formats.
    // Does not load Unity, modify assets, or represent live camera-selected LODs.
    sealed class SavedHoleChunk
    {
        public LTSpatialLODTopology.Chunk topology;
        public Vector3[] vertices;
        public int divisions,revision;
    }
    static SavedHoleChunk ReadSavedHoleChunk(string path,bool readVertices=true)
    {
        var result=new SavedHoleChunk();var patches=new List<LTSpatialLODPatch>();
        var levels=new List<LTSpatialLODLevel>();var cells=new List<LTSpatialLODCell>();
        var variants=new List<LTSpatialLODVariant>();LTSpatialLODPatch patch=null;LTSpatialLODCell cell=default;
        bool haveCell=false,mesh=false,channels=false;int count=0,stride=0,stream=0,offset=0,format=0,channel=-1;
        byte[] bytes=null;
        int Value(string line)=>int.Parse(line.Substring(line.IndexOf(':')+1));
        Vector3 Vector(string line)
        {
            var fields=line.Substring(line.IndexOf('{')+1).TrimEnd('}').Split(',');
            float F(int i)=>float.Parse(fields[i].Substring(fields[i].IndexOf(':')+1),System.Globalization.CultureInfo.InvariantCulture);
            return new Vector3(F(0),F(1),F(2));
        }
        int[] Indices(string line)
        {
            var hex=line.Substring(line.IndexOf(':')+1).Trim();if(hex=="[]"||hex.Length==0)return Array.Empty<int>();
            var raw=Convert.FromHexString(hex);Require(raw.Length%4==0,"saved indices byte alignment");
            var output=new int[raw.Length/4];Buffer.BlockCopy(raw,0,output,0,raw.Length);return output;
        }
        void FinishPatch(){if(patch!=null){patch.levels=levels.ToArray();patches.Add(patch);levels.Clear();patch=null;}}
        void FinishCell(){if(haveCell){cell.variants=variants.ToArray();cells.Add(cell);variants.Clear();haveCell=false;}}
        foreach(string line in File.ReadLines(path))
        {
            if(line.StartsWith("--- !u!43")){FinishPatch();FinishCell();mesh=true;continue;}
            if(line.StartsWith("--- !u!114")){mesh=false;continue;}
            if(!mesh)
            {
                if(line.StartsWith("  - root:")){FinishPatch();patch=new LTSpatialLODPatch{root=Value(line)};}
                else if(line.StartsWith("      m_Center:"))patch.bounds.center=Vector(line);
                else if(line.StartsWith("      m_Extent:"))patch.bounds.extents=Vector(line);
                else if(line.StartsWith("    - cells:"))levels.Add(new LTSpatialLODLevel{cells=Indices(line)});
                else if(line=="  cells:")FinishPatch();
                else if(line.StartsWith("  - x:")){FinishCell();cell=new LTSpatialLODCell{x=Value(line)};haveCell=true;}
                else if(line.StartsWith("    - indices:"))variants.Add(new LTSpatialLODVariant{indices=Indices(line)});
                else if(line.StartsWith("    z:"))cell.z=Value(line);
                else if(line.StartsWith("    size:"))cell.size=Value(line);
                else if(line.StartsWith("    patch:"))cell.patch=Value(line);
                else if(line.StartsWith("    child0:"))cell.child0=Value(line);
                else if(line.StartsWith("    child1:"))cell.child1=Value(line);
                else if(line.StartsWith("    child2:"))cell.child2=Value(line);
                else if(line.StartsWith("    child3:"))cell.child3=Value(line);
                else if(line.StartsWith("    possibleMask:"))cell.possibleMask=Value(line);
                else if(line.StartsWith("    requiredMask:"))cell.requiredMask=Value(line);
                else if(line.StartsWith("  divisions:"))result.divisions=Value(line);
                else if(line.StartsWith("  revision:"))result.revision=Value(line);
                else if(line.StartsWith("  formatVersion:"))Require(Value(line)==LTSpatialLODMath.Version,"saved spatial format");
            }
            else if(readVertices)
            {
                if(line.StartsWith("    m_VertexCount:"))count=Value(line);
                else if(line=="    m_Channels:")channels=true;
                else if(line.StartsWith("    m_DataSize:"))channels=false;
                else if(line.StartsWith("    _typelessdata:"))bytes=Convert.FromHexString(line.Substring(line.IndexOf(':')+1).Trim());
                else if(channels)
                {
                    if(line.StartsWith("    - stream:")){stream=Value(line);channel++;}
                    else if(line.StartsWith("      offset:"))offset=Value(line);
                    else if(line.StartsWith("      format:"))format=Value(line);
                    else if(line.StartsWith("      dimension:"))
                    {
                        int dimension=Value(line);if(dimension==0)continue;
                        Require(stream==0&&format==0,"only single float32 stream supported by saved diagnostic");
                        if(channel==0)Require(offset==0&&dimension==3,"position must be first float3 channel");
                        stride=Math.Max(stride,offset+dimension*4);
                    }
                }
            }
        }
        FinishPatch();FinishCell();
        result.topology=new LTSpatialLODTopology.Chunk{cells=cells.ToArray(),patches=patches.ToArray()};
        if(!readVertices){Require(result.topology.cells.Length>0&&result.topology.patches.Length==result.divisions*result.divisions,"saved topology payload: "+path);return result;}
        Require(count>0&&bytes!=null&&bytes.Length==count*stride,"saved mesh vertex payload");
        result.vertices=new Vector3[count];
        for(int i=0;i<count;i++)result.vertices[i]=new Vector3(BitConverter.ToSingle(bytes,i*stride),BitConverter.ToSingle(bytes,i*stride+4),BitConverter.ToSingle(bytes,i*stride+8));
        return result;
    }
    static void SavedHoleContourProbe(string[] args)
    {
        int arg=Array.IndexOf(args,"--saved-hole-contour");
        Require(args.Length==arg+4||args.Length==arg+10,"Usage: --saved-hole-contour left.asset right.asset chunkWidth [--rectangle centreX centreZ side edge lodEdge]");
        var data=new[]{ReadSavedHoleChunk(args[arg+1]),ReadSavedHoleChunk(args[arg+2])};
        float width=float.Parse(args[arg+3],System.Globalization.CultureInfo.InvariantCulture);
        Require(data[0].divisions==data[1].divisions,"matching divisions");
        LTHoleStampSnapshot hole=null;
        if(args.Length==arg+10)
        {
            Require(args[arg+4]=="--rectangle","optional saved diagnostic shape");
            float F(int i)=>float.Parse(args[arg+i],System.Globalization.CultureInfo.InvariantCulture);
            hole=Hole(F(5),F(6),F(7),F(7),LTStampShape.Rectangle,step:F(8),lodEdgeCellSize:F(9));
        }
        var topology=new LTSpatialLODTopology(2,1,data[0].divisions,data.Select(c=>c.topology).ToArray());
        Console.WriteLine($"SAVED DATA ONLY: revisions {data[0].revision}/{data[1].revision}, vertices {data[0].vertices.Length}/{data[1].vertices.Length}; 2-chunk subset, outer neighbours not included.");
        for(int level=0;level<data[0].topology.patches[0].levels.Length;level++)
        {
            for(int c=0;c<2;c++)for(int p=0;p<data[c].topology.patches.Length;p++)topology.SetLevel(c,p,level);
            topology.Update();topology.Validate();
            var edges=new Dictionary<((int,int) a,(int,int) b),int>();var indices=new List<int>();int triangles=0;
            for(int c=0;c<2;c++)
            {
                topology.WriteIndices(c,indices);triangles+=indices.Count/3;
                (int,int) Key(int id){var p=data[c].vertices[id];return ((int)Math.Round((p.x+c*width)*10000),(int)Math.Round(p.z*10000));}
                for(int i=0;i<indices.Count;i+=3)for(int side=0;side<3;side++)
                {
                    var a=Key(indices[i+side]);var b=Key(indices[i+(side+1)%3]);if(a==b)continue;
                    if(edges.TryGetValue((b,a),out int n)){if(n==1)edges.Remove((b,a));else edges[(b,a)]=n-1;}
                    else{edges.TryGetValue((a,b),out n);edges[(a,b)]=n+1;}
                }
            }
            int endX=(int)(width*2*10000),endZ=(int)(width*10000);
            var rim=edges.Where(e=>!(e.Key.a.Item1==e.Key.b.Item1&&(e.Key.a.Item1==0||e.Key.a.Item1==endX)||
                e.Key.a.Item2==e.Key.b.Item2&&(e.Key.a.Item2==0||e.Key.a.Item2==endZ))).ToArray();
            Require(rim.All(e=>e.Value==1),"non-manifold saved boundary");
            var lengths=rim.Select(e=>{double x=(double)e.Key.a.Item1-e.Key.b.Item1,z=(double)e.Key.a.Item2-e.Key.b.Item2;return Math.Sqrt(x*x+z*z)*.0001;}).ToArray();
            Console.WriteLine($"LOD {level}: contour segments {rim.Length}; mean/max XZ segment length {lengths.DefaultIfEmpty().Average():F4}/{lengths.DefaultIfEmpty().Max():F4} m; triangles {triangles}");
        }
        if(hole!=null)for(int c=0;c<2;c++)
        {
            var chunk=data[c].topology;
            List<Vector3Int> Plan(int level)=>chunk.patches.SelectMany(p=>p.levels[level].cells).Select(i=>chunk.cells[i]).Select(v=>new Vector3Int(v.x,v.z,v.size)).ToList();
            Rect Area(Vector3Int p)=>new Rect(c*width+p.x/(float)LTSpatialLODMath.N*width,p.y/(float)LTSpatialLODMath.N*width,p.z/(float)LTSpatialLODMath.N*width,p.z/(float)LTSpatialLODMath.N*width);
            string Near(List<Vector3Int> cells)=>string.Join(", ",cells.Where(p=>hole.Intersects(Area(p))).GroupBy(p=>Area(p).width).OrderBy(g=>g.Key).Select(g=>$"{g.Key:F3}m:{g.Count()}"));
            var saved=Plan(chunk.patches[0].levels.Length-1);
            var flat=new LTLODMesh.Preparation(Plan(0),new Rect(c*width,0,width,width),(x,z)=>0,new LTLODMesh.BuildProgress(false))
                .Coarsen(4,50,null,hole.ProtectsLOD,data[c].divisions,false);
            Console.WriteLine($"chunk {c} saved far hole-intersecting cells by size: {Near(saved)}; current hole-only flat policy: {Near(flat)}");
        }
    }
}
