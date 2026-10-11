#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTDetailRenderer
    {
        public static int CheckJunctionRemoval(LTRoadJunction node)
        {
            var data=node.Capture();var stamp=JunctionRemoval(node,data);int checks=0;
            for(int z=0;z<=32;z++)for(int x=0;x<=32;x++)
            {
                var p=new Vector3(data.bounds.xMin+data.bounds.width*x/32,0,data.bounds.yMin+data.bounds.height*z/32);
                float weight=stamp.Weight(node.World.transform.TransformPoint(p),null,null);
                if(Math.Abs(weight-data.Weight(p.x,p.z))>.0001f)throw new InvalidOperationException("Grass removal does not match mesh foundation.");
                checks++;
            }
            return checks;
        }
        // Only the menu validator calls this, on its own inactive preview object.
        // Drives the production iterator without cameras, rendering or scene updates.
        public string CheckSavedPlacement(LTChunk chunk,LTSurfaceLayer layer,Texture2D weightTexture,
            LTDetailPlacementAsset asset,Func<LTDetailPlacementAsset,LTDetailPlacementAsset> roundTrip)
        {
            if(gameObject.activeInHierarchy)throw new InvalidOperationException("Validation requires an inactive owned preview object.");
            int checks=0;
            void Require(bool ok,string label){if(!ok)throw new InvalidOperationException(label);checks++;}
            world=GetComponent<LTWorld>();cellSize=8;maxInstances=10000;matrixCacheMiB=1;
            world.fixedChunkSize=Vector2.one*8;world.chunksX=world.chunksZ=1;world.chunkOrigin=new Vector2Int(-1,0);
            var rect=world.TerrainRect;var coordinate=new Vector2Int(-1,0);var chunks=new[]{chunk};
            terrain.Update(world,chunks);planned.Add(coordinate);wanted.Add(coordinate);
            var tile=new LTDetailSurface.Tile{rect=rect,layers=new[]{layer},weights0=new LTDetailSurface.Image(weightTexture)};
            var surface=new LTDetailSurface();surface.tiles.Add(tile);
            var layerSettings=new LTDetailSurface.Layer{size=Vector2.one,strength=1};surface.layers.Add(layer,layerSettings);
            var entry=new LTDetailEntry{density=3,heightOffsetRange=new Vector2(-.1f,.2f),scaleRange=new Vector2(.6f,1.4f)};
            entry.Validate(new HashSet<string>());
            var recipe=new LTDetailPrefab{rootScale=new Vector3(1,2,.5f),bounds=new Bounds(Vector3.up,Vector3.one*2),lodSize=2,lodReferencePoint=Vector3.up};
            var level=new LTDetailPrefab.Level();level.parts.Add(new LTDetailPrefab.Part{localMatrix=Matrix4x4.Translate(Vector3.up)});recipe.levels.Add(level);
            var source=new Source{entry=entry,recipe=recipe,layer=layer,owner="layer",seed=17,order=-1,density=3};
            var sources=new List<Source>{source};var stamps=new List<Stamp>();
            var roads=new List<LTRoadMath.Snapshot>();var masks=new List<LTRoadVegetationMask.Entry>();
            string Key(out UnityEngine.Object[] references)=>new PlacementKeyContext(this,surface,world.transform.localToWorldMatrix)
                .Key(coordinate,rect,surface.tiles,sources,stamps,roads,masks,out references);
            string baseline=Key(out var baselineReferences);
            void Changed(Action change,Action restore,string label)
            {change();Require(Key(out _)!=baseline,label);restore();Require(Key(out var refs)==baseline&&LTDetailPlacementAsset.ReferencesMatch(refs,baselineReferences),label+" restore");}
            Changed(()=>seed++,()=>seed--,"renderer seed invalidation");
            Changed(()=>entry.patchSeed++,()=>entry.patchSeed--,"mask invalidation");
            Changed(()=>entry.patchScaleEnabled=true,()=>entry.patchScaleEnabled=false,"mask size invalidation");
            Changed(()=>entry.probability=.5f,()=>entry.probability=1,"density probability invalidation");
            Changed(()=>layerSettings.heightOffset=.3f,()=>layerSettings.heightOffset=0,"surface height blend invalidation");
            var savedImage=tile.weights0;
            weightTexture.SetPixel(0,0,Color.black);weightTexture.Apply();tile.weights0=new LTDetailSurface.Image(weightTexture);
            Require(Key(out _)!=baseline,"weight pixel content invalidation");tile.weights0=savedImage;
            Changed(()=>world.transform.position+=Vector3.right,()=>world.transform.position-=Vector3.right,"world translation invalidation");
            var stamp=new Stamp{rect=rect,inverse=Matrix4x4.identity,size=Vector2.one*8,allLayers=true,mode=LTDetailStampMode.Remove};
            stamps.Add(stamp);var stampKey=Key(out _);stamp.id="junction:transient";Require(Key(out _)==stampKey,"stamp instance ID excluded");
            stamp.falloff=.4f;Require(Key(out _)!=stampKey,"suppression rule invalidation");
            var junctionShape=new LTRoadFootprint(new[]{new Vector3(-7,0,1),new Vector3(-5,0,1),new Vector3(-7,0,3)},new[]{0,1,2},0);
            stamp.junction=LTRoadJunctionMath.MeshFoundation(Vector3.zero,junctionShape,.1f,.5f,.5f,1);
            var junctionKey=Key(out _);Require(junctionKey!=stampKey,"mesh junction suppression key");
            Require(stamp.Weight(new Vector3(-6.5f,0,1.5f),tile,Array.Empty<float>())==1&&
                stamp.Weight(new Vector3(-3,0,4),tile,Array.Empty<float>())==0,"mesh junction removal uses silhouette not rectangle");
            stamp.junction.hash=7654;Require(Key(out _)==junctionKey,"junction session hash excluded");
            stamp.junction.shoulder=.3f;Require(Key(out _)!=junctionKey,"junction shoulder invalidates saved grass");stamp.junction.shoulder=.1f;
            stamp.junction.footprint=new LTRoadFootprint(new[]{new Vector3(-7,0,1),new Vector3(-5,0,1),new Vector3(-5,0,3)},new[]{0,1,2},0);
            Require(Key(out _)!=junctionKey,"junction contour change with unchanged bounds invalidates saved grass");stamps.Clear();
            var roadSettings=LTRoadMath.Settings.Default;roadSettings.width=2;roadSettings.groundLayerId=2;
            var points=new[]{new LTRoadPoint(new Vector3(-6,0,0)),new LTRoadPoint(new Vector3(-6,0,8))};
            roads.Add(LTRoadMath.Build(points,Matrix4x4.identity,roadSettings));var roadKey=Key(out _);
            roadSettings.groundLayerId=55;roadSettings.sourceTransformHash=900;
            roads[0]=LTRoadMath.Build(points,Matrix4x4.identity,roadSettings);Require(Key(out _)==roadKey,"road session IDs excluded");
            roadSettings.width=3;roads[0]=LTRoadMath.Build(points,Matrix4x4.identity,roadSettings);Require(Key(out _)!=roadKey,"road width invalidation");roads.Clear();
            masks.Add(new LTRoadVegetationMask.Entry{footprint=new LTRoadFootprint(chunk.mesh.vertices,chunk.mesh.triangles,0)});
            Require(Key(out _)!=baseline,"authored asphalt clearing invalidation");masks.Clear();
            var vertices=chunk.mesh.vertices;vertices[0].y=.5f;chunk.mesh.vertices=vertices;chunk.updatedAt++;
            terrain.Update(world,chunks);Require(Key(out _)!=baseline,"terrain geometry invalidation");
            vertices[0].y=0;chunk.mesh.vertices=vertices;chunk.updatedAt++;terrain.Update(world,chunks);
            Require(Key(out _) == baseline,"terrain geometry equality survives mesh revision");
            tile.coverage=938;tile.surface=543;Require(Key(out _)==baseline,"surface session hashes excluded");
            void GenerateCell()
            {
                cells.Clear();instanceCount=0;cachedMatrixBytes=0;
                var iterator=Generate(surface,chunks,sources,stamps,0,world.transform.localToWorldMatrix,new HashSet<Vector2Int>(planned));
                try{int steps=0;while(iterator.MoveNext())if(++steps>10000)throw new InvalidOperationException("Unbounded fixture generation.");}
                finally{(iterator as IDisposable)?.Dispose();}
                Require(cells.ContainsKey(coordinate),"complete cell published");
            }
            GenerateCell();Require(instanceCount>0&&!cells[coordinate].fromSaved,"fresh generation");
            var generated=cells[coordinate].groups[0].instances.ToArray();
            asset.Initialize(CaptureReadyPlacement());savedPlacement=roundTrip(asset);useSavedPlacement=true;
            // Reloaded objects deliberately have different InstanceIDs, as after a
            // domain/editor restart; internal serialized references must remap too.
            var reloaded=savedPlacement.Find(coordinate);
            var reloadedLayer=(LTSurfaceLayer)reloaded.references[0];Require(reloadedLayer&&reloadedLayer!=layer,"binary reference remapping");
            surface.layers.Clear();surface.layers.Add(reloadedLayer,layerSettings);tile.layers[0]=source.layer=reloadedLayer;
            Require(Key(out _) == baseline,"same content after native binary round trip");
            GenerateCell();Require(cells[coordinate].fromSaved&&instanceCount==generated.Length,"cache hit after native binary load");
            var loaded=cells[coordinate].groups[0].instances;
            for(int i=0;i<generated.Length;i++)
            {
                var a=generated[i];var b=loaded[i];
                Require(a.matrix==b.matrix&&a.id==b.id&&a.maskPosition==b.maskPosition&&a.scale==b.scale&&a.bounds==b.bounds&&
                    a.lodPosition==b.lodPosition&&a.lodSize==b.lodSize,"restored transforms/bounds/LOD/id");
            }
            Require(cells[coordinate].groups[0].partMatrices.Count==1,"part matrix cache rebuilt");
            source.layer=tile.layers[0]=layer;surface.layers.Clear();surface.layers.Add(layer,layerSettings);
            GenerateCell();Require(!cells[coordinate].fromSaved,"different layer reference rejects cache");
            source.layer=tile.layers[0]=reloadedLayer;surface.layers.Clear();surface.layers.Add(reloadedLayer,layerSettings);
            entry.patchSeed++;GenerateCell();Require(!cells[coordinate].fromSaved,"stale cache falls back to generator");entry.patchSeed--;
            reloaded.groups[0].items[0].id++;GenerateCell();Require(!cells[coordinate].fromSaved,"corrupt payload falls back");reloaded.groups[0].items[0].id--;
            useSavedPlacement=false;GenerateCell();Require(!cells[coordinate].fromSaved,"disabled saved placement");useSavedPlacement=true;
            Rebuild();GenerateCell();Require(!cells[coordinate].fromSaved,"manual rebuild bypass");bypassSavedPlacement=false;
            void ValidateCell(bool expected)
            {
                cells.Clear();instanceCount=0;cachedMatrixBytes=0;
                placementValidationOnly=true;placementValidatedCells=0;bool accepted=false;
                var iterator=Generate(surface,chunks,sources,stamps,0,world.transform.localToWorldMatrix,new HashSet<Vector2Int>(planned));
                try{while(iterator.MoveNext()){}accepted=true;}
                catch(InvalidOperationException){ }
                finally{(iterator as IDisposable)?.Dispose();placementValidationOnly=false;}
                Require(accepted==expected,"strict validation result");
                Require(cells.Count==0&&instanceCount==0,"validation never generates or restores instances");
                Require(placementValidatedCells==(expected?1:0),"validation cell accounting");
            }
            ValidateCell(true);
            entry.patchSeed++;ValidateCell(false);entry.patchSeed--;
            reloaded.groups[0].items[0].id++;ValidateCell(false);reloaded.groups[0].items[0].id--;
            useSavedPlacement=false;ValidateCell(false);useSavedPlacement=true;
            var diskPlacement=savedPlacement;savedPlacement=null;ValidateCell(false);savedPlacement=diskPlacement;
            void IncrementalCell(bool expectedReuse)
            {
                cells.Clear();instanceCount=0;cachedMatrixBytes=0;placementReusedCell=null;placementIncremental=true;
                var iterator=Generate(surface,chunks,sources,stamps,0,world.transform.localToWorldMatrix,new HashSet<Vector2Int>(planned));
                try{while(iterator.MoveNext()){} }
                finally{(iterator as IDisposable)?.Dispose();placementIncremental=false;}
                Require((placementReusedCell!=null)==expectedReuse,"incremental reuse decision");
                if(expectedReuse)
                {
                    Require(ReferenceEquals(placementReusedCell,reloaded),"reuse immutable serialized record directly");
                    Require(cells.Count==0&&instanceCount==0&&cachedMatrixBytes==0,"reuse without restoring matrices or instances");
                }
                else Require(cells.ContainsKey(coordinate)&&!cells[coordinate].fromSaved,"changed cell regenerated");
                placementReusedCell=null;
            }
            IncrementalCell(true);
            useSavedPlacement=false;IncrementalCell(true);useSavedPlacement=true;
            entry.patchSeed++;IncrementalCell(false);entry.patchSeed--;
            reloaded.groups[0].items[0].id++;IncrementalCell(false);reloaded.groups[0].items[0].id--;
            savedPlacement=null;IncrementalCell(false);savedPlacement=diskPlacement;
            savedPlacement=null;entry.probability=0;GenerateCell();Require(instanceCount==0,"empty fixture");
            asset.Initialize(CaptureReadyPlacement());savedPlacement=asset;GenerateCell();Require(cells[coordinate].fromSaved&&instanceCount==0,"empty cell cache hit");
            return $"PASS {checks} assertions; native binary round trip, remapped references, production generation/cache restore, exact transforms/bounds/LOD, stale/corrupt fallback, manual bypass and empty cells. Rendering and load-time speed not measured.";
        }
    }
}
#endif
