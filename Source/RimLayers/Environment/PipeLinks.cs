using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using RimLayers.Layout;
using Verse;

namespace RimLayers.Environment;

/// <summary>
/// Cross-deck adjacency for PipeSystem networks (VEF's astrofuel and oxygen pipes used by VGE).
///
/// PipeSystem.PipeNetManager.NeighbourThingsCardinal only walks GenAdjFast.AdjacentCellsCardinal,
/// i.e. the four same-deck neighbours, so two decks of one ship never share a PipeNet. The storage
/// layer is already layer-aware (PipeNet.networkGrid is a map-sized BoolGrid and GetPipeNetAt
/// indexes it with a storage cell), so only adjacency is missing.
///
/// The supported way to cross a deck is an explicit vertical connector (RL_PipeRiser). Native
/// PostSpawnSetup -> RegisterConnector -> NeighbourThingsCardinal then merges the two decks on its
/// own; no manual unregister/re-register is involved, which is what kept corrupting PipeNet state
/// when this service used to re-register whole columns.
///
/// PipeSystem stays an optional dependency: every member is reached through reflection and nothing is
/// installed unless the types exist.
/// </summary>
public sealed class PipeLinks
{
    private static Type? managerType, compResourceType;
    private static bool installed;
    internal static bool Active=>installed;

    private readonly LayerMapComponent owner;

    /// <summary>
    /// ThingDefs that are allowed to carry a resource across a deck boundary. Only these bridges may
    /// join two layers; ordinary pipes and tanks keep native same-deck behaviour so an unrelated
    /// colony network can never be welded to a passing deck.
    /// </summary>
    private static readonly HashSet<string> riserDefNames=new(StringComparer.Ordinal)
    {
        "RL_PipeRiser"
    };

    internal static void RegisterRiser(string defName) => riserDefNames.Add(defName);

    public PipeLinks(LayerMapComponent owner)=>this.owner=owner;

    /// <summary>Called once from the compatibility installer when PipeSystem is loaded.</summary>
    internal static bool Install(Harmony harmony)
    {
        if(installed)return true;
        managerType=AccessTools.TypeByName("PipeSystem.PipeNetManager");
        compResourceType=AccessTools.TypeByName("PipeSystem.CompResource");
        if(managerType==null||compResourceType==null)return false;
        var neighbour=AccessTools.Method(managerType,"NeighbourThingsCardinal",new[]{typeof(Thing)});
        if(neighbour==null)
        {
            Log.Warning("[RimLayers] PipeSystem found but its connector API changed; layered pipes stay same-deck.");
            managerType=null;compResourceType=null;return false;
        }
        harmony.Patch(neighbour,prefix:new HarmonyMethod(typeof(PipeLinks),nameof(NeighbourPrefix)));
        installed=true;
        return true;
    }

    /// <summary>
    /// Adds the vertical projection of the thing's footprint. PipeSystem itself still filters by
    /// pipeNet and TransmitResourceNow, so different resources never mix.
    /// </summary>
    private static bool NeighbourPrefix(Thing thing,ref List<Thing> __result)
    {
        if(thing.Map is not {} map||LayerMapComponent.For(map) is not {} owner)return true;
        // Ordinary pipes and tanks keep the native implementation untouched. Only a deliberate riser
        // gets a patched neighbour list, so an unrelated column can never be welded across decks.
        if(!CarriesResource(thing))return true;
        var result=new List<Thing>();
        foreach(var cell in GenAdjFast.AdjacentCellsCardinal(thing))Collect(map,cell,result);
        foreach(var cell in thing.OccupiedRect())
            foreach(int direction in new[]{-1,1})
            {
                if(!VerticalAllowed(owner,cell,direction))continue;
                Collect(map,owner.Adjacent(cell,direction),result);
            }
        __result=result;
        return false;
    }

    /// <summary>True when this Thing is a deliberate cross-deck connector rather than an ordinary pipe.</summary>
    private static bool CarriesResource(Thing thing)
    {
        if(riserDefNames.Contains(thing.def.defName))return true;
        // Modded bridges may opt in by reusing one of our comps, so a subclass of CompResourcePort
        // also counts without hard-coding every third-party defName.
        return compResourceType!=null&&thing is ThingWithComps withComps&&
            withComps.AllComps.Any(c=>c is CompResourcePort);
    }

    /// <summary>
    /// A riser may only join the layer directly above or below it, and only when that layer carries a
    /// riser of its own: the player must build one on each deck, which is what makes the crossing an
    /// explicit act. Requiring ship ownership instead would break risers placed on ordinary ground
    /// outside a hull, where Ships.OwnerAt is null by design.
    /// </summary>
    private static bool VerticalAllowed(LayerMapComponent owner,IntVec3 cell,int direction)
    {
        var other=owner.Adjacent(cell,direction);
        if(!other.IsValid)return false;
        return CarriesResourceAt(owner.map,other);
    }

    /// <summary>True when the cell holds a built, spawned cross-deck connector.</summary>
    internal static bool CarriesResourceAt(Map map,IntVec3 cell)
    {
        if(!cell.InBounds(map))return false;
        foreach(var thing in map.thingGrid.ThingsListAt(cell))
            if(thing.Spawned&&!thing.Destroyed&&CarriesResource(thing))return true;
        return false;
    }

    private static void Collect(Map map,IntVec3 cell,List<Thing> result)
    {
        if(!cell.InBounds(map))return;
        foreach(var thing in map.thingGrid.ThingsListAt(cell))
            if(!result.Contains(thing))result.Add(thing);
    }
}

/// <summary>
/// Marks a building as a deliberate cross-deck resource bridge. Merely having this comp is enough:
/// the def itself must still carry PipeSystem's own CompProperties_Resource so PipeSystem registers
/// it as a normal connector and performs all merging/splitting natively.
/// </summary>
public sealed class CompProperties_ResourcePort : CompProperties
{
    public CompProperties_ResourcePort()=>compClass=typeof(CompResourcePort);
}

public sealed class CompResourcePort : ThingComp
{
    /// <summary>
    /// A riser only bridges one deck, and only when the layer above or below carries a riser of
    /// its own. Nothing else in the game says so: an unpaired riser looks identical to a working
    /// one until a network silently fails to merge. Report the paired layers, or the absence.
    /// </summary>
    public override string CompInspectStringExtra()
    {
        var owner=LayerMapComponent.For(parent.Map);
        if(owner==null)return "RL_RequiresLayerMap".Translate();
        if(!parent.Spawned)return "";
        var paired=new List<string>();
        bool placedAnywhere=false;
        foreach(int direction in new[]{-1,1})
        {
            var other=owner.Adjacent(parent.Position,direction);
            if(!other.IsValid)continue;
            placedAnywhere=true;
            if(!PipeLinks.CarriesResourceAt(parent.Map,other))continue;
            if(!owner.TryDecode(other,out var at))continue;
            paired.Add(owner.LevelName(at.LayerId));
        }
        // A riser on the outermost layer has nowhere to go; that is not a player mistake.
        if(!placedAnywhere)return "";
        return paired.Count==0
            ? "RL_PipeNoPeer".Translate()
            : "RL_PipePeers".Translate(string.Join(", ",paired.Distinct().OrderBy(x=>x)));
    }
}
