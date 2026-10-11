using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Exact live metadata, not an event/dirty-only cache. Only the expensive text
    // representation is reused. Failed/incomplete polls never replace the key.
    internal sealed class LTRoadCaptureSignature
    {
        readonly struct LODInput
        {
            internal readonly int id,count;
            internal readonly Matrix4x4 matrix;
            internal LODInput(int id,Matrix4x4 matrix,int count){this.id=id;this.matrix=matrix;this.count=count;}
            internal bool Same(LODInput other)
            {
                if(id!=other.id||count!=other.count)return false;
                // Unity's approximate matrix operator is unsuitable here. Bits
                // also distinguish signed zero, preserving legacy text exactly.
                for(int i=0;i<16;i++)if(Bits(matrix[i])!=Bits(other.matrix[i]))return false;
                return true;
            }
        }
        readonly struct MeshInput
        {
            internal readonly int id,revision,vertices,submeshes;
            internal readonly string asset;
            internal MeshInput(int id,int revision,string asset,int vertices,int submeshes)
            {this.id=id;this.revision=revision;this.asset=asset;this.vertices=vertices;this.submeshes=submeshes;}
            internal bool Same(MeshInput other)=>id==other.id&&revision==other.revision&&vertices==other.vertices&&
                submeshes==other.submeshes&&string.Equals(asset,other.asset,StringComparison.Ordinal);
        }
        // R/integer formatting uses these symbols. Capture values, not the mutable
        // CultureInfo/NumberFormatInfo reference (a script can edit it in place).
        readonly struct Format : IEquatable<Format>
        {
            readonly string decimalPoint,negative,positive,nan,negativeInfinity,positiveInfinity;
            internal Format(NumberFormatInfo n)
            {decimalPoint=n.NumberDecimalSeparator;negative=n.NegativeSign;positive=n.PositiveSign;
                nan=n.NaNSymbol;negativeInfinity=n.NegativeInfinitySymbol;positiveInfinity=n.PositiveInfinitySymbol;}
            public bool Equals(Format other)=>decimalPoint==other.decimalPoint&&negative==other.negative&&positive==other.positive&&
                nan==other.nan&&negativeInfinity==other.negativeInfinity&&positiveInfinity==other.positiveInfinity;
        }
        readonly List<LODInput> lods=new List<LODInput>(),acceptedLODs=new List<LODInput>();
        readonly List<MeshInput> meshes=new List<MeshInput>(),acceptedMeshes=new List<MeshInput>();
        float edge;int acceptedEdge;Format format,acceptedFormat;string signature;
        static int Bits(float value)=>BitConverter.SingleToInt32Bits(value);
        internal void Begin(float edge)
        {this.edge=edge;format=new Format(CultureInfo.CurrentCulture.NumberFormat);lods.Clear();meshes.Clear();}
        internal void AddLOD(int id,Matrix4x4 matrix,int count)=>lods.Add(new LODInput(id,matrix,count));
        internal void AddMesh(int id,int revision,string asset,int vertices,int submeshes)
            =>meshes.Add(new MeshInput(id,revision,asset,vertices,submeshes));
        bool Unchanged()
        {
            if(signature==null||acceptedEdge!=Bits(edge)||!format.Equals(acceptedFormat)||
                lods.Count!=acceptedLODs.Count||meshes.Count!=acceptedMeshes.Count)return false;
            for(int i=0;i<lods.Count;i++)if(!lods[i].Same(acceptedLODs[i]))return false;
            for(int i=0;i<meshes.Count;i++)if(!meshes[i].Same(acceptedMeshes[i]))return false;
            return true;
        }
        internal string Finish()
        {
            if(Unchanged())return signature;
            var text=new StringBuilder("road-foundation-v2:");
            text.Append(edge.ToString("R",CultureInfo.InvariantCulture)).Append(':');int index=0;
            foreach(var lod in lods)
            {
                text.Append(lod.id).Append(':').Append(lod.matrix.ToString("R")).Append(':');
                for(int level=0;level<lod.count;level++)
                {
                    var mesh=meshes[index++];
                    text.Append(mesh.id).Append(',').Append(mesh.revision).Append(',').Append(mesh.asset).Append(',')
                        .Append(mesh.vertices).Append(',').Append(mesh.submeshes).Append(';');
                }
            }
            if(index!=meshes.Count)throw new InvalidOperationException("Incomplete road capture signature.");
            signature=text.ToString();acceptedEdge=Bits(edge);acceptedFormat=format;
            acceptedLODs.Clear();acceptedLODs.AddRange(lods);acceptedMeshes.Clear();acceptedMeshes.AddRange(meshes);
            return signature;
        }
    }
}
