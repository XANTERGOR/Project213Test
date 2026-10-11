using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public enum LTBoundaryCacheMiss { None, Proposed, Missing, Mesh, Transform, Revision, Asset }

    // Project notifications are not geometry revisions: saving/importing a coarse
    // LOD can raise them too. The persistent dependency hash detects real reimports
    // even when Unity preserves the object identity and resets its dirty counter.
    public readonly struct LTBoundaryCacheKey
    {
        public readonly int dirtyVersion;
        public readonly Matrix4x4 matrix;
        public readonly string assetHash;
        public LTBoundaryCacheKey(int dirtyVersion,Matrix4x4 matrix,string assetHash)
        {this.dirtyVersion=dirtyVersion;this.matrix=matrix;this.assetHash=assetHash;}

        public static LTBoundaryCacheMiss Compare(bool proposed,bool present,bool sameMesh,LTBoundaryCacheKey previous,LTBoundaryCacheKey current)
        {
            if(proposed)return LTBoundaryCacheMiss.Proposed;
            if(!present)return LTBoundaryCacheMiss.Missing;
            if(!sameMesh)return LTBoundaryCacheMiss.Mesh;
            if(!previous.matrix.Equals(current.matrix))return LTBoundaryCacheMiss.Transform;
            if(previous.dirtyVersion!=current.dirtyVersion)return LTBoundaryCacheMiss.Revision;
            if(!string.Equals(previous.assetHash,current.assetHash,StringComparison.Ordinal))return LTBoundaryCacheMiss.Asset;
            return LTBoundaryCacheMiss.None;
        }
    }
}
