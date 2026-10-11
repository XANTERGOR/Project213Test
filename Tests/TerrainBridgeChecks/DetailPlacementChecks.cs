using System;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void DetailPlacementChecks()
    {
        int checks=0;
        void Check(bool ok,string message){Require(ok,message);checks++;}
        var weightPixels=new Color32[257*257];var paintRect=new Rect(0,0,256,256);
        Check(!LTDetailMath.HasLayerWeight(weightPixels,257,257,paintRect,new Rect(0,0,16,16),0),"unpainted zone skipped");
        weightPixels[8*257+8]=new Color32(0,255,0,0);
        Check(LTDetailMath.HasLayerWeight(weightPixels,257,257,paintRect,new Rect(0,0,16,16),1),"single-pixel painted island retained");
        Check(!LTDetailMath.HasLayerWeight(weightPixels,257,257,paintRect,new Rect(32,0,16,16),1),"distant empty cell skipped");
        Check(!LTDetailMath.HasLayerWeight(weightPixels,257,257,paintRect,new Rect(0,0,16,16),0),"other layer channel remains empty");
        weightPixels[8*257+16]=new Color32(255,0,0,0);
        Check(LTDetailMath.HasLayerWeight(weightPixels,257,257,paintRect,new Rect(0,0,15.5f,16),0),"bilinear neighbour beyond cell edge retained");
        var maskEntry=new LTDetailEntry{densityMask=true,patchCoverage=0,patchMinimumDensity=0};
        Check(!LTDetailMath.DensityMaskMayAffect(maskEntry,paintRect,1),"zero-coverage mask skipped");
        maskEntry.patchMinimumDensity=.01f;
        Check(LTDetailMath.DensityMaskMayAffect(maskEntry,paintRect,1),"nonzero density floor retained");
        maskEntry.patchMinimumDensity=0;maskEntry.patchCoverage=.08f;maskEntry.patchSize=30;
        int skipped=0;
        for(int z=-8;z<8;z++)for(int x=-8;x<8;x++)
        {
            var area=new Rect(x*4,z*4,4,4);bool possible=LTDetailMath.DensityMaskMayAffect(maskEntry,area,123);
            if(!possible)skipped++;
            for(int j=0;j<=8;j++)for(int i=0;i<=8;i++)
                Check(possible||LTDetailMath.DensityMask(maskEntry,area.xMin+i*.5f,area.yMin+j*.5f,123)==0,"noise bound never removes positive density");
        }
        Check(skipped>0,"noise bounds skip provably empty patches");
        var worldBounds=new Rect(-17,-9,34,18);
        var worldCells=LTDetailStreaming.WorldCells(worldBounds,8).ToArray();
        Check(worldCells.Length==24&&worldCells.Distinct().Count()==24,"full world includes all negative and positive cells once");
        Check(worldCells[0].x==-3&&worldCells[0].y==-2&&worldCells.Last().x==2&&worldCells.Last().y==1,"full world stable row ordering");
        float covered=worldCells.Sum(k=>{var r=LTDetailStreaming.CellRect(k,8,worldBounds.size,worldBounds.min);return r.width*r.height;});
        Check(covered==worldBounds.width*worldBounds.height,"clipped edge cells cover exact world area");
        Check(LTDetailStreaming.WorldCells(new Rect(0,0,16,16),8).Count()==4,"aligned maximum excludes extra edge cells");
        var rect=new Rect(-16,8,16,16);
        LTDetailPlacementAsset.Cell Fresh()
        {
            var cell=new LTDetailPlacementAsset.Cell{coordinate=new Vector2Int(-1,0),inputKey="stable-input",sourceCount=2,
                groups=new[]{new LTDetailPlacementAsset.Group{source=1,items=new[]{new LTDetailPlacementAsset.Item{
                    position=new Vector3(-3,2,12),rotation=Quaternion.identity,scale=.75f,maskPosition=new Vector2(-3,12),id=uint.MaxValue}}}}};
            cell.payloadKey=LTDetailPlacementAsset.PayloadKey(cell);return cell;
        }
        bool Valid(LTDetailPlacementAsset.Cell c,int count=2,int budget=100)=>LTDetailPlacementAsset.ValidPayload(c,count,budget,rect);
        var original=Fresh();Check(Valid(original),"valid negative-coordinate cell");
        Check(original.payloadKey==Fresh().payloadKey,"repeatable payload key");
        Check(!Valid(original,1),"source count change");Check(!Valid(original,2,0),"instance budget");
        Check(!Valid(null),"missing cell");
        void Reject(Action<LTDetailPlacementAsset.Cell> change,string reason,bool reseal=false)
        {var cell=Fresh();change(cell);if(reseal)cell.payloadKey=LTDetailPlacementAsset.PayloadKey(cell);Check(!Valid(cell),reason);}
        Reject(c=>c.groups=null,"missing groups");
        Reject(c=>c.groups[0]=null,"missing group");
        Reject(c=>c.groups[0].items=null,"missing items");
        Reject(c=>c.groups[0].source=-1,"negative source");Reject(c=>c.groups[0].source=2,"unknown source");
        Reject(c=>c.groups=new[]{c.groups[0],c.groups[0]},"duplicate source");
        Reject(c=>c.groups[0].items[0].scale=0,"zero scale",true);
        Reject(c=>c.groups[0].items[0].scale=float.NaN,"NaN scale",true);
        Reject(c=>c.groups[0].items[0].position=new Vector3(float.PositiveInfinity,0,0),"infinite position",true);
        Reject(c=>c.groups[0].items[0].rotation=new Quaternion(0,0,0,0),"invalid quaternion",true);
        Reject(c=>c.groups[0].items[0].maskPosition=new Vector2(0,12),"half-open maximum cell edge",true);
        Reject(c=>c.groups[0].items[0].maskPosition=new Vector2(-17,12),"outside cell",true);
        Reject(c=>c.groups[0].items[0].maskPosition=new Vector2(float.NaN,12),"NaN mask position",true);
        Reject(c=>c.groups[0].items[0].id--,"corrupted id checksum");
        Reject(c=>c.inputKey+="changed","input key is part of payload checksum");
        var empty=Fresh();empty.groups=Array.Empty<LTDetailPlacementAsset.Group>();empty.payloadKey=LTDetailPlacementAsset.PayloadKey(empty);
        Check(Valid(empty),"empty cells are saved too");
        var lower=Fresh();lower.groups[0].items[0].maskPosition=rect.min;lower.payloadKey=LTDetailPlacementAsset.PayloadKey(lower);
        Check(Valid(lower),"inclusive minimum cell edge");
        string Fingerprint(string a,string b)
        {var hash=new LTDetailPlacementFingerprint();hash.Add(a);hash.Add(b);hash.Add(1.25f);hash.Add(new Vector3(-9,7,13));return hash.ToString();}
        Check(Fingerprint("ab","c")!=Fingerprint("a","bc"),"string field boundaries");
        Check(Fingerprint(null,"x")!=Fingerprint("","x"),"null vs empty string");
        var pixels=new[]{new Color32(1,2,3,4)};
        string Image(){var h=new LTDetailPlacementFingerprint();h.Add(pixels);return h.ToString();}
        var oldImage=Image();pixels[0].g++;Check(oldImage!=Image(),"height mask content invalidates key");
        var v=new[]{new Vector3(0,0,0),new Vector3(4,0,0),new Vector3(0,0,4)};var ix=new[]{0,2,1};
        var footprint=new LTRoadFootprint(v,ix,0);string before=footprint.ContentKey;
        Check(before==new LTRoadFootprint(v,ix,0).ContentKey,"footprint key ignores object identity");
        Check(before!=new LTRoadFootprint(v,ix,.5f).ContentKey,"road clearing padding changes key");
        v[0].x=.25f;Check(before!=new LTRoadFootprint(v,ix,0).ContentKey,"road geometry changes key");
        Check(before==footprint.ContentKey,"footprint snapshot is immutable");
        var entry=new LTDetailEntry{scaleRange=new Vector2(.2f,2),patchSeed=937,patchScaleEnabled=true};
        var copy=entry.SavedPlacementSettings();copy.scaleRange=Vector2.one;
        Check(entry.scaleRange.x==.2f&&copy.patchSeed==entry.patchSeed&&copy.patchScaleEnabled,"settings copy preserves mask and isolates mutations");
        Console.WriteLine($"PASS detail placement: {checks} payload/content checks. Stable vector: {Fingerprint("трава","test")}; payload: {original.payloadKey}");
    }
}
