using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimLayers.Core;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimLayers.Ships;

/// <summary>Only layer facts supplement the native Gravship. Native dictionaries continue to own every Thing/Pawn.</summary>
public sealed class ShipLayerData : IExposable
{
    public int version=2;
    public int engineDeckOffset;
    public int layerCount;
    public List<ShipFloorCell> floors=new();
    public List<ShipSupportRoof> supportRoofs=new();
    public void ExposeData()
    {
        Scribe_Values.Look(ref version,"version",1);
        Scribe_Values.Look(ref engineDeckOffset,"engineDeckOffset",0);
        Scribe_Values.Look(ref layerCount,"layerCount",3);
        Scribe_Collections.Look(ref floors,"floors",LookMode.Deep);
        Scribe_Collections.Look(ref supportRoofs,"supportRoofs",LookMode.Deep);
        if(Scribe.mode==LoadSaveMode.PostLoadInit)supportRoofs??=new();
        if(Scribe.mode==LoadSaveMode.PostLoadInit && (version is not (1 or 2) || layerCount<1 || layerCount>6 || engineDeckOffset<0 || engineDeckOffset>=layerCount))
            throw new InvalidOperationException("[RimLayers] Unsupported gravship layer data.");
        if(Scribe.mode==LoadSaveMode.PostLoadInit&&version==1)version=2;
    }
}

public sealed class ShipSupportRoof:IExposable
{
    public IntVec3 local;public RoofDef roof=null!;
    public void ExposeData(){Scribe_Values.Look(ref local,"local");Scribe_Defs.Look(ref roof,"roof");}
}

public sealed class ShipFloorCell : IExposable
{
    // Payload only: x/z are offsets from the core, y is the relative layer, never a Map coordinate.
    public IntVec3 local;
    public TerrainDef? top,under,temp;
    public void ExposeData()
    {
        Scribe_Values.Look(ref local,"local");
        Scribe_Defs.Look(ref top,"top");Scribe_Defs.Look(ref under,"under");Scribe_Defs.Look(ref temp,"temp");
    }
}

internal sealed class ShipCapture
{
    internal required Building_GravEngine Engine;
    internal required LayerMapComponent Owner;
    internal required HashSet<IntVec3> Cells;
    internal required ShipLayerData Data;
    internal HashSet<Thing> Straddlers=new();
    internal HashSet<IntVec3> HollowRoofs=new();
    internal required int BaseLayer;
    internal required IntVec3 Origin;
    internal IntVec3 Encode(IntVec3 relative)
    {
        if(!Owner.TryDecode(Origin+relative,out var c))throw new InvalidOperationException("[RimLayers] Ship payload crosses a storage gap.");
        Owner.TryDecode(Origin,out var origin);
        return new(relative.x,Owner.Elevation(c.LayerId)-BaseLayer,c.Z-origin.Z);
    }
}

internal static class ShipLayers
{
    private static readonly ConditionalWeakTable<Gravship,ShipLayerData> data=new();
    [ThreadStatic] internal static ShipCapture? Capture;
    [ThreadStatic] internal static Gravship? Incoming;
    [ThreadStatic] internal static Gravship? Placing;
    [ThreadStatic] internal static LayerMapComponent? PlacementMap;
    [ThreadStatic] internal static int PlacementBaseLayer;
    internal static bool TryData(Gravship ship,out ShipLayerData value)=>data.TryGetValue(ship,out value!);
    internal static int RequiredLayers(Gravship ship)=>TryData(ship,out var payload)?Math.Max(1,payload.layerCount):1;
    internal static bool IsLayerShip(Gravship? ship)=>ship!=null && ModsConfig.OdysseyActive &&
        ship.Engine is {Destroyed:false} engine && Compatibility.VanillaGravshipCompatibility.EngineDef(engine.def) && engine.Faction==Faction.OfPlayer && TryData(ship,out var payload)&&payload.layerCount>=1;
    internal static bool Eligible(Building_GravEngine? engine)=>ModsConfig.OdysseyActive &&
        engine is {Spawned:true,Destroyed:false} && Compatibility.VanillaGravshipCompatibility.EngineDef(engine.def) && engine.Faction==Faction.OfPlayer &&
        (LayerMapComponent.For(engine.Map) is {} owner?owner.Ships.Get(engine)!=null:
        engine.ValidSubstructure.Count>0 && engine.OccupiedRect().All(engine.ValidSubstructure.Contains));

    internal static ShipCapture? Prepare(Building_GravEngine engine,out AcceptanceReport report)
    {
        report=true;
        if(!Eligible(engine) || LayerMapComponent.For(engine.Map) is not {} owner)return null;
        if(!owner.TryDecode(engine.Position,out var origin))return null;
        var structure=owner.Ships.Get(engine);if(structure==null)return null;
        report=owner.Ships.LaunchReport(engine);if(!report.Accepted)return null;
        var cells=new HashSet<IntVec3>(structure.Cells);
        var payload=new ShipLayerData{layerCount=structure.Top-structure.Bottom+1,engineDeckOffset=owner.Elevation(origin.LayerId)-structure.Bottom};
        foreach(var c in cells)
        {
            owner.TryDecode(c,out var p);
            payload.floors.Add(new ShipFloorCell{local=new IntVec3(p.X-origin.X,owner.Elevation(p.LayerId)-structure.Bottom,p.Z-origin.Z),
                top=owner.map.terrainGrid.TopTerrainAt(c),under=owner.map.terrainGrid.UnderTerrainAt(c),temp=owner.map.terrainGrid.TempTerrainAt(c)});
        }
        var capture=new ShipCapture{Engine=engine,Owner=owner,BaseLayer=structure.Bottom,Origin=engine.Position,Cells=cells,Data=payload};
        if(payload.floors.Count==0)return capture;
        foreach(var c in cells)
        {
            owner.TryDecode(c,out var at);
            if(owner.Elevation(at.LayerId)<=structure.Bottom)continue;
            var below=owner.Adjacent(c,-1);var roof=owner.map.roofGrid.RoofAt(below);
            if(roof==null||cells.Contains(below))continue;
            payload.supportRoofs.Add(new(){local=new(at.X-origin.X,owner.Elevation(at.LayerId)-structure.Bottom-1,at.Z-origin.Z),roof=roof});
            if(owner.Underlays.SupportRoofOwner(below)==engine.thingIDNumber||
                !owner.Structure.HasSurface(below)&&!owner.Mountains.IsNaturalFloor(below))capture.HollowRoofs.Add(below);
        }
        var checkedThings=new HashSet<Thing>();
        foreach(var c in cells)
        {
            if(!owner.TryDecode(c,out var p))continue;
            foreach(var thing in c.GetThingList(owner.map))
            {
                if(!checkedThings.Add(thing)||thing is Mote or Skyfaller)continue;
                // Native attachments travel with their supporting wall, not their protruding anchor cell.
                if(thing.def.building?.isAttachment==true&&thing.OccupiedRect().Count()==1)continue;
                if(thing.OccupiedRect().Any(pos=>!cells.Contains(pos)))
                {
                    if(thing is not (Building or Blueprint or Frame)||
                       thing.OccupiedRect().Any(at=>!cells.Contains(at)&&owner.Ships.ClaimAt(at)!=0&&owner.Ships.ClaimAt(at)!=engine.thingIDNumber))
                    {report="RL_ShipSplitObject".Translate(thing.LabelCap);return null;}
                    capture.Straddlers.Add(thing);
                }
            }
        }
        foreach(var hoist in RimLayers.Cargo.CargoNetwork.For(owner.map).Hoists)
        {
            var ports=RimLayers.Cargo.CargoNetwork.For(owner.map).Ports.Where(p=>p.Master==hoist).ToArray();
            if(ports.Any(p=>cells.Contains(p.Position))&&ports.Any(p=>!cells.Contains(p.Position)))
            {
                if(ports.Any(p=>!cells.Contains(p.Position)&&owner.Ships.ClaimAt(p.Position)!=0&&owner.Ships.ClaimAt(p.Position)!=engine.thingIDNumber))
                {report="RL_ShipSplitObject".Translate(hoist.LabelCap);return null;}
                foreach(var port in ports)capture.Straddlers.Add(port);
            }
        }
        if(capture.Straddlers.Contains(engine))
        {report="RL_ShipSplitFacility".Translate();return null;}
        report=ShipSeparation.CheckClearance(capture);if(!report.Accepted)return null;
        foreach(var zone in owner.map.zoneManager.AllZones)
            if(zone.Cells.Any(c=>cells.Contains(c)) &&
                zone.Cells.Any(c=>!cells.Contains(c)))
            {report="RL_ShipSplitZone".Translate(zone.label);return null;}
        return capture;
    }

    internal static void AttachNativeSingle(Gravship ship,Building_GravEngine? source=null)
    {
        if(TryData(ship,out _)||!ModsConfig.OdysseyActive||ship.Engine.Faction!=Faction.OfPlayer)return;
        var payload=new ShipLayerData{layerCount=1,engineDeckOffset=0};
        if(source is {Spawned:true}&&LayerMapComponent.For(source.Map)==null)
        {
            var grid=source.Map.terrainGrid;
            foreach(var c in source.ValidSubstructure)
                payload.floors.Add(new(){local=c-source.Position,top=grid.TopTerrainAt(c),under=grid.UnderTerrainAt(c),temp=grid.TempTerrainAt(c)});
        }
        else foreach(var pair in ship.Terrains)
            payload.floors.Add(new(){local=pair.Key,top=pair.Value});
        if(payload.floors.Count>0)data.Add(ship,payload);
    }

    internal static void AttachAndNormalize(Gravship ship,ShipCapture capture)
    {
        var seen=new HashSet<PositionData>();
        foreach(string name in new[]{"things","pawns","thrusters","exteriorDoors"})
            foreach(PositionData position in ((IDictionary)AccessTools.Field(typeof(Gravship),name).GetValue(ship)!).Values)
                if(seen.Add(position))position.position=capture.Encode(position.position);
        foreach(string name in new[]{"foundations","terrains","roofs","terrainColors","gases","terrainDesignations"})
        {
            var dictionary=(IDictionary)AccessTools.Field(typeof(Gravship),name).GetValue(ship)!;
            var entries=dictionary.Keys.Cast<object>().Select(k=>(key:(IntVec3)k,value:dictionary[k])).ToArray();dictionary.Clear();
            foreach(var entry in entries)dictionary.Add(capture.Encode(entry.key),entry.value);
        }
        var extenders=(List<IntVec3>)AccessTools.Field(typeof(Gravship),"gravFieldExtenderPositions").GetValue(ship)!;
        for(int i=0;i<extenders.Count;i++)extenders[i]=capture.Encode(extenders[i]);
        foreach(var room in (List<RoomTemperatureVacuum>)AccessTools.Field(typeof(Gravship),"roomTemperatures").GetValue(ship)!)
            room.roomCell=capture.Encode(room.roomCell);
        foreach(var area in AllAreas(ship.areas))
        {
            var cells=(List<IntVec3>)AccessTools.Field(typeof(MoveableArea),"relativeCells").GetValue(area)!;
            for(int i=0;i<cells.Count;i++)cells[i]=capture.Encode(cells[i]);
        }
        foreach(var field in AccessTools.GetDeclaredFields(typeof(Gravship)))
            if(!field.IsStatic&&field.Name.StartsWith("tmp",StringComparison.Ordinal)&&field.FieldType.IsGenericType &&
                field.FieldType.GetGenericTypeDefinition()==typeof(Dictionary<,>))field.SetValue(ship,null);
        Compatibility.VanillaGravshipCompatibility.NormalizeRoofColors(ship,capture);
        data.Add(ship,capture.Data);
    }
    private static IEnumerable<MoveableArea> AllAreas(MoveableAreas a)=>a.stockpiles.Cast<MoveableArea>()
        .Concat(a.growZones).Concat(a.allowedAreas).Concat(new MoveableArea?[]{a.homeArea,a.buildRoofArea,a.noRoofArea,a.snowClearArea,a.pollutionClearArea}.OfType<MoveableArea>());

    internal static void Expose(Gravship ship)
    {
        data.TryGetValue(ship,out var value);
        Scribe_Deep.Look(ref value,"rimLayersShip");
        if(Scribe.mode==LoadSaveMode.LoadingVars && value!=null){data.Remove(ship);data.Add(ship,value);}
    }
    internal static TerrainDef FoundationForDeck(TerrainDef value,int deck)=>deck==0&&(Placing==null||PlacementBaseLayer==0)&&value.defName=="RL_ShipStructuralFloor"?TerrainDefOf.Substructure:value;
    internal static bool IsPlacing(Gravship ship)=>Placing==ship;
    internal static IntVec3 Offset(LayerMapComponent owner,int baseId,IntVec3 local) => new(local.x,0,local.z+(owner.Layout!.LayerOrFuture(owner.Elevation(baseId)+local.y)-baseId)*owner.Layout.Stride);
    internal static IntVec3 DecodeOffset(IntVec3 local)=>PlacementMap is {} owner?Offset(owner,PlacementBaseLayer,local):new(local.x,0,local.z);
    internal static IEnumerable<(IntVec3,T)> Project<T>(Gravship ship,IEnumerable<(IntVec3,T)> source)
    {
        if(!TryData(ship,out _))return source;
        bool place=IsPlacing(ship);
        return source.Where(x=>place||x.Item1.y==0).Select(x=>(place?DecodeOffset(x.Item1):x.Item1,x.Item2)).ToList();
    }
    internal static Dictionary<IntVec3,T> Project<T>(Gravship ship,Dictionary<IntVec3,T> source)=>
        !TryData(ship,out _)?source:Project(ship,source.Select(x=>(x.Key,x.Value))).ToDictionary(x=>x.Item1,x=>x.Item2);
    internal static Dictionary<T,PositionData.Data> Project<T>(Gravship ship,Dictionary<T,PositionData.Data> source) where T:Thing
    {
        if(!TryData(ship,out _))return source;
        bool place=IsPlacing(ship);
        var result=new Dictionary<T,PositionData.Data>();
        foreach(var pair in source)
        {
            var value=pair.Value;if(!place&&value.local.y!=0)continue;
            if(place)value.local=DecodeOffset(value.local);result.Add(pair.Key,value);
        }
        return result;
    }
}
