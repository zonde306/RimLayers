using RimLayers.Core;
using RimWorld;
using UnityEngine;
using Verse;
namespace RimLayers.Ships;

internal static class ShipMigration
{
    private static Window? dialog;
    private static LayerMapComponent? previewMap;
    private static List<IntVec3> preview=new();
    internal static void Open(LayerMapComponent owner,Building_GravEngine engine)
    {
        previewMap=owner;preview=new();
        if(owner.Elevation(owner.ViewedLayer)>0)
            foreach(var c in owner.LayerRect(owner.ViewedLayer))
                if(owner.StructuralCells.Contains(c)&&owner.map.terrainGrid.FoundationAt(c)==null&&
                    (owner.map.terrainGrid.TopTerrainAt(c)==RimLayersDefOf.RL_StructuralFloor||owner.map.terrainGrid.UnderTerrainAt(c)==RimLayersDefOf.RL_StructuralFloor)&&
                    owner.Ships.BuildOwner(c)==engine&&owner.Structure.HasShipRoof(c))preview.Add(c);
        if(preview.Count==0){Messages.Message("RL_ShipMigrationEmpty".Translate(),MessageTypeDefOf.RejectInput,false);return;}
        var candidates=preview.ToArray();
        dialog=Dialog_MessageBox.CreateConfirmation("RL_ShipMigrationConfirm".Translate(candidates.Length),()=>
        {
            foreach(var c in candidates)
                if(engine.Spawned&&engine.Map==owner.map&&owner.Ships.BuildOwner(c)==engine&&owner.map.terrainGrid.FoundationAt(c)==null)
                {owner.Ships.Assign(c,engine.thingIDNumber);ShipDeckTerrain.Install(owner,c,true);}
            owner.Ships.Tick();
        },destructive:true);
        Find.WindowStack.Add(dialog);
    }
    internal static void Draw(LayerMapComponent owner)
    {
        if(owner==previewMap&&dialog!=null&&Find.WindowStack.Windows.Contains(dialog)&&preview.Count>0)
            GenDraw.DrawFieldEdges(preview.Where(owner.IsViewedCell).ToList(),new Color(1f,.65f,.15f));
    }
}
