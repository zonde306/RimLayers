using System.IO.Compression;
using RimLayers.Core;
using Verse;

namespace RimLayers.Environment;

public enum Atmosphere { Fog, Toxic, Smog, Ash, Gray, Death }

/// <summary>Explicit registration: an unknown third-party condition is never silently reclassified.</summary>
public static class LayerEventPolicy
{
    private static readonly Dictionary<string,Atmosphere> airborne=new(StringComparer.Ordinal)
    {
        ["NoxiousHaze"]=Atmosphere.Smog,["ToxicFallout"]=Atmosphere.Toxic,["ToxicSmog"]=Atmosphere.Smog,["VolcanicAsh"]=Atmosphere.Ash,
        ["GrayPall"]=Atmosphere.Gray,["DeathPall"]=Atmosphere.Death
    };
    private static readonly HashSet<string> knownWeather=new(StringComparer.Ordinal)
    {"Clear","Fog","Rain","DryThunderstorm","RainyThunderstorm","FoggyRain","SnowHard","SnowGentle","Underground",
     "Windy","ToxRain","Sandstorm","BlindFog","Overcast","Blizzard","TorrentialRain","Orbit","BloodRain","UnnaturalFog","GrayPall",
     "UnnaturalDarkness_Stage1","UnnaturalDarkness_Stage2","Undercave","MetalHell"};
    private static readonly HashSet<Type> liquidEvents=new(){typeof(RimWorld.SeasonalFlood),typeof(RimWorld.TorrentialRainFlood),typeof(RimWorld.GameCondition_LavaFlow)};
    public static void RegisterLiquidEvent(Type eventType)=>liquidEvents.Add(eventType);
    internal static bool KnownLiquid(object instance)=>liquidEvents.Contains(instance.GetType());
    public static void RegisterWeather(string defName)=>knownWeather.Add(defName);
    internal static bool Known(WeatherDef weather)=>knownWeather.Contains(weather.defName);
    public static void RegisterAirborne(string defName,Atmosphere channel)=>airborne[defName]=channel;
    internal static bool[] Sources(Map map)
    {
        var result=new bool[6];
        for(var manager=map.gameConditionManager;manager!=null;manager=manager.Parent)
            foreach(var condition in manager.ActiveConditions)
                if(airborne.TryGetValue(condition.def.defName,out var channel)&&condition.CanApplyOnMap(map)&&!condition.HiddenByOtherCondition(map))
                    result[(int)channel]=true;
        string weather=map.weatherManager.curWeather.defName;
        result[0]=weather is "Fog" or "FoggyRain" or "BlindFog" or "UnnaturalFog" or "GrayPall";
        if(weather=="ToxicSmog")result[(int)Atmosphere.Smog]=true;
        return result;
    }
}

/// <summary>Shared local environment. Derived Room references are rebuilt, never serialized.</summary>
public sealed class LayerEnvironment
{
    private readonly LayerMapComponent owner;
    private Dictionary<int,float> rocks=new();
    private List<AirVolume> volumes=new();
    private Dictionary<Room,AirVolume> byRoom=new();
    private List<(AirVolume A,AirVolume B,float Area)> edges=new();
    private long revision=-1;
    private long connectors=-1;
    private bool topologyBuilt;
    /// <summary>Room-topology rebuilds (the metric the perf test asserts on).</summary>
    public long TopologyBuilds { get; private set; }
    private string? savedAir;
    private Dictionary<IntVec3,float[]>? pendingAir;
    private bool[] sources=new bool[6];
    private int sourceTick=-1;
    private float fogAccuracy=.5f,fogMove=1,fogRange=-1;
    private sealed class AirVolume
    {
        internal Room Room=null!;
        internal IntVec3[] Cells=Array.Empty<IntVec3>();
        internal float[] Values=new float[6];
        internal int Exposure;
    }
    public LayerEnvironment(LayerMapComponent owner)=>this.owner=owner;
    public bool Enabled=>owner.Managed&&LayerEnvironmentGame.Active;
    public static EnvironmentSettings Settings=>RimLayersMod.Settings.Environment;
    public bool Sky(IntVec3 cell)=>owner.Sky.IsExposed(cell);
    public bool WindExposed(IntVec3 cell)
    {
        if(!owner.TryDecode(cell,out var c)||owner.Elevation(c.LayerId)<0)return false;
        Room room=cell.GetRoom(owner.map);
        return Sky(cell)||room!=null&&(room.TouchesMapEdge||room.OpenRoofCount>0);
    }
    public bool RockShielded(IntVec3 cell)
    {
        if(!owner.TryDecode(cell,out var c))return false;
        foreach(int layer in new[]{c.LayerId}.Concat(owner.Layout!.Above(c.LayerId)))
            if(owner.map.roofGrid.RoofAt(owner.ToStorage(new(layer,c.X,c.Z)))?.isThickRoof==true)return true;
        return false;
    }
    public float Outdoor(IntVec3 cell)=>owner.map.mapTemperature.OutdoorTemp-
        (owner.TryDecode(cell,out var c)?Math.Max(0,owner.Elevation(c.LayerId))*Settings.AltitudeLapse:0);
    public float WindFactor(IntVec3 cell)=>owner.TryDecode(cell,out var c)&&WindExposed(cell)?
        EnvironmentMath.Wind(owner.Elevation(c.LayerId),Settings.WindStrength):1;
    public float Rock(int layer)
    {
        int depth=Math.Max(0,-owner.Elevation(layer));
        if(!rocks.TryGetValue(layer,out float value))
            rocks[layer]=value=EnvironmentMath.RockTarget(Find.WorldGrid[owner.map.Tile].temperature-Math.Max(0,owner.Elevation(layer))*Settings.AltitudeLapse,depth,Settings.RockOffset,Settings.GeothermalGradient);
        return value;
    }
    public float LocalOutside(Room room)=>room.Cells.FirstOrDefault() is var at?Outdoor(at):owner.map.mapTemperature.OutdoorTemp;
    public void InitializeClimate(int first)
    {
        if(!Enabled)return;
        foreach(var room in owner.map.regionGrid.AllRooms)
            if(room.Cells.FirstOrDefault() is var at&&owner.TryDecode(at,out var c)&&c.LayerId>=first&&owner.Elevation(c.LayerId)<0)
                room.Temperature=Rock(c.LayerId);
    }
    internal sealed record SnapshotState(Dictionary<int,float> Rocks,Dictionary<IntVec3,float[]> Air,float FogAccuracy,float FogMove,float FogRange);
    internal SnapshotState Snapshot()
    {
        var air=pendingAir!=null?pendingAir.ToDictionary(p=>p.Key,p=>(float[])p.Value.Clone()):
            volumes.Where(v=>v.Values.Any(x=>x>.0001f)).SelectMany(v=>{var values=(float[])v.Values.Clone();return v.Cells.Select(c=>(c,Values:values));}).ToDictionary(x=>x.c,x=>x.Values);
        return new(new(rocks),air,fogAccuracy,fogMove,fogRange);
    }
    internal void Restore(SnapshotState snapshot)
    {
        rocks=snapshot.Rocks;pendingAir=snapshot.Air;volumes=new();byRoom=new();edges=new();
        fogAccuracy=snapshot.FogAccuracy;fogMove=snapshot.FogMove;fogRange=snapshot.FogRange;Invalidate();
    }
    public void Invalidate(){revision=-1;sourceTick=-1;residualTick=-1;topologyBuilt=false;}
    private bool[] Sources()
    {
        if(sourceTick==Find.TickManager.TicksGame)return sources;
        sourceTick=Find.TickManager.TicksGame;sources=LayerEventPolicy.Sources(owner.map);
        string weather=owner.map.weatherManager.curWeather.defName;
        if(weather is "Fog" or "FoggyRain" or "BlindFog" or "UnnaturalFog" or "GrayPall")
        {fogAccuracy=owner.map.weatherManager.CurWeatherAccuracyMultiplier;fogMove=owner.map.weatherManager.CurMoveSpeedMultiplier;fogRange=owner.map.weatherManager.CurWeatherMaxRangeCap;}
        return sources;
    }
    private readonly bool[] residual=new bool[6];
    private int residualTick=-1;
    internal bool HasResidual(Atmosphere channel)
    {
        if(!Enabled||Sources()[(int)channel])return false;
        if(residualTick!=Find.TickManager.TicksGame)
        {
            residualTick=Find.TickManager.TicksGame;
            for(int i=0;i<6;i++)residual[i]=pendingAir?.Values.Any(v=>v[i]>.001f)==true||volumes.Any(v=>v.Values[i]>.001f);
        }
        return residual[(int)channel];
    }
    public void ExposeData()
    {
        Scribe_Collections.Look(ref rocks,"environmentRockTemperatures",LookMode.Value,LookMode.Value);
        Scribe_Values.Look(ref fogAccuracy,"environmentFogAccuracy",.5f);Scribe_Values.Look(ref fogMove,"environmentFogMove",1f);Scribe_Values.Look(ref fogRange,"environmentFogRange",-1f);
        if(Scribe.mode==LoadSaveMode.Saving)savedAir=Pack();
        Scribe_Values.Look(ref savedAir,"environmentAir");
        if(Scribe.mode==LoadSaveMode.PostLoadInit){rocks??=new();pendingAir=Unpack(savedAir);savedAir=null;Invalidate();}
    }
    public void Tick()
    {
        if(!Enabled)return;
        // One fixed-point topology rebuild per tick, before any consumer reads concentrations.
        // Concentration() itself no longer rebuilds, so stat getters cannot pay for this.
        EnsureTopology();
        if(Find.TickManager.TicksGame%3451==0&&!LayerEventPolicy.Sources(owner.map)[(int)Atmosphere.Toxic])
            foreach(var pawn in owner.map.mapPawns.AllPawnsSpawned)
                if(!pawn.kindDef.immuneToGameConditionEffects&&Concentration(pawn.Position,Atmosphere.Toxic)>0)
                    ToxicUtility.DoPawnToxicDamage(pawn,Concentration(pawn.Position,Atmosphere.Toxic));
        if(Find.TickManager.TicksGame%3251==0&&!LayerEventPolicy.Sources(owner.map)[(int)Atmosphere.Ash])
            foreach(var pawn in owner.map.mapPawns.AllPawnsSpawned)
                if(!pawn.kindDef.immuneToGameConditionEffects&&GasUtility.IsAffectedByExposure(pawn)&&Concentration(pawn.Position,Atmosphere.Ash)>=.25f)
                {
                    var hediff=pawn.health.hediffSet.GetFirstHediffOfDef(RimWorld.HediffDefOf.VolcanicAsh)??pawn.health.AddHediff(RimWorld.HediffDefOf.VolcanicAsh);
                    hediff.TryGetComp<HediffComp_Disappears>().ticksToDisappear=3256;
                }
        if(Find.TickManager.TicksGame%120!=7)return;
        foreach(int layer in owner.Layout!.OrderedLayers.Where(l=>owner.Elevation(l)<0||rocks.ContainsKey(l)))
        {
            int depth=Math.Max(0,-owner.Elevation(layer));
            rocks[layer]=EnvironmentMath.Follow(Rock(layer),owner.Environment.Outdoor(owner.ToStorage(new(layer,0,0)))+Settings.RockOffset+Settings.GeothermalGradient*depth,120,Settings.RockResponseDays,Math.Max(1,depth));
        }
        var active=Sources();
        bool any=active.Any(x=>x)||volumes.Any(v=>v.Values.Any(x=>x>.0001f))||pendingAir?.Count>0;
        sources=active;
        if(!any)return;
        // Snapshot all concentrations before applying any pair; room order cannot duplicate flux.
        var changes=volumes.ToDictionary(v=>v,v=>new float[6]);
        var indices=volumes.Select((v,i)=>(v,i)).ToDictionary(x=>x.v,x=>x.i);
        var links=edges.Select(e=>new HeatEdge(indices[e.A],indices[e.B],e.Area*.084*Settings.AirExchange)).ToArray();
        for(int ch=0;ch<6;ch++)
        {
            var snapshot=volumes.Select(v=>new HeatVolume(v.Values[ch],v.Cells.Length)).ToArray();
            var mass=HeatExchange.Calculate(snapshot,links);
            for(int i=0;i<volumes.Count;i++)changes[volumes[i]][ch]=(float)(mass[i]/volumes[i].Cells.Length);
        }
        foreach(var v in volumes)
            for(int ch=0;ch<6;ch++)
            {
                float value=Math.Clamp(v.Values[ch]+changes[v][ch],0,1);
                if(v.Exposure>0)
                {
                    float rate=1-MathF.Exp(-v.Exposure*.084f*Settings.AirExchange/v.Cells.Length);
                    value+=(sources[ch]?1-value:-value)*rate;
                    if(v.Exposure==v.Cells.Length)value=sources[ch]?1:0;
                }
                // Settling removes particles, not the native pawn's existing health conditions.
                v.Values[ch]=EnvironmentMath.Decay(value,120,Settings.PollutionHalfLife);
                if(v.Exposure==v.Cells.Length&&sources[ch])v.Values[ch]=1;
            }
    }
    internal double Mass(int layer,Atmosphere channel)
    {EnsureTopology();return volumes.Sum(v=>v.Cells.Count(c=>owner.TryDecode(c,out var q)&&q.LayerId==layer)*(double)v.Values[(int)channel]);}
    public float Concentration(IntVec3 cell,Atmosphere channel)
    {
        if(!Enabled)return Sky(cell)?1:0;
        if(!owner.TryDecode(cell,out _))return 0;
        int i=(int)channel;
        // Directly exposed cells receive the active ambient condition immediately.
        var active=Sources();
        if(Sky(cell)&&active[i])return 1;
        // This runs from per-plant/per-pawn stat getters (NoxiousHaze, Plant growth, Aurora thoughts).
        // Rebuilding the room topology here made one door toggle do a whole-map walk in the middle of
        // a stat query. The rebuild is now owned by Tick(), which runs once per tick at a fixed point.
        // Only the very first use still builds eagerly so a fresh map has an answer.
        if(!topologyBuilt)EnsureTopology();
        Room room=cell.GetRoom(owner.map);
        return room!=null&&byRoom.TryGetValue(room,out var v)?v.Values[i]:0;
    }
    public float WeatherExposure(IntVec3 cell)
    {
        string name=owner.map.weatherManager.curWeather.defName;
        if(name is "Fog" or "FoggyRain" or "BlindFog" or "GrayPall" or "UnnaturalFog")
            return Concentration(cell,Atmosphere.Fog);
        if(name=="ToxicSmog")return Concentration(cell,Atmosphere.Smog);
        return WindExposed(cell)?1:0;
    }
    internal float Accuracy(IntVec3 cell)
    {
        float current=1+(owner.map.weatherManager.CurWeatherAccuracyMultiplier-1)*WeatherExposure(cell);
        return Math.Min(current,1+(fogAccuracy-1)*Concentration(cell,Atmosphere.Fog));
    }
    internal float Move(IntVec3 cell)
    {
        float current=1+(owner.map.weatherManager.CurMoveSpeedMultiplier-1)*WeatherExposure(cell);
        return Math.Min(current,1+(fogMove-1)*Concentration(cell,Atmosphere.Fog));
    }
    internal float InverseRange(IntVec3 cell)
    {
        float cap=owner.map.weatherManager.CurWeatherMaxRangeCap;
        float current=cap>0?WeatherExposure(cell)/cap:0;
        return Math.Max(current,fogRange>0?Concentration(cell,Atmosphere.Fog)/fogRange:0);
    }
    private bool AirPass(IntVec3 cell)=>owner.TryDecode(cell,out _)&&owner.map.gasGrid.GasCanMoveTo(cell)&&
        (cell.GetDoor(owner.map) is not {} door||door.Open);
    private void EnsureTopology()
    {
        if(revision==owner.Revision&&connectors==owner.Connectors.Version)return;
        TopologyBuilds++;
        topologyBuilt=true;
        var previous=pendingAir??volumes.SelectMany(v=>v.Cells.Select(c=>(c,v.Values))).ToDictionary(x=>x.c,x=>(float[])x.Values.Clone());
        pendingAir=null;volumes=new();byRoom=new();edges=new();
        foreach(Room room in owner.map.regionGrid.AllRooms)
        {
            var cells=room.Cells.Where(c=>owner.TryDecode(c,out _)).ToArray();
            if(cells.Length==0)continue;
            var v=new AirVolume{Room=room,Cells=cells,Exposure=room.UsesOutdoorTemperature?cells.Length:cells.Count(Sky)};
            foreach(var c in cells)if(previous.TryGetValue(c,out var values))
                for(int i=0;i<6;i++)v.Values[i]+=values[i]/cells.Length;
            volumes.Add(v);byRoom.Add(room,v);
        }
        var pairs=new Dictionary<(AirVolume,AirVolume),float>();
        var indices=volumes.Select((v,i)=>(v,i)).ToDictionary(x=>x.v,x=>x.i);
        foreach(var a in volumes)foreach(var c in a.Cells)
        {
            if(!AirPass(c))continue;
            void Link(IntVec3 other)
            {
                if(!AirPass(other))return;
                Room room=other.GetRoom(owner.map);
                if(room==null||!byRoom.TryGetValue(room,out var b)||a==b)return;
                var key=indices[a]<indices[b]?(a,b):(b,a);
                pairs[key]=pairs.GetValueOrDefault(key)+.5f;
            }
            foreach(var d in GenAdj.CardinalDirections)Link(c+d);
            var up=owner.Adjacent(c,1);
            if(up.IsValid&&VolumeLinks.Open(owner,c,up,LayerChangeKind.Air))
            {
                Room room=up.GetRoom(owner.map);
                if(room!=null&&byRoom.TryGetValue(room,out var b)&&a!=b)
                {var key=indices[a]<indices[b]?(a,b):(b,a);pairs[key]=pairs.GetValueOrDefault(key)+1;}
            }
        }
        edges=pairs.Select(p=>(p.Key.Item1,p.Key.Item2,p.Value)).ToList();
        revision=owner.Revision;connectors=owner.Connectors.Version;
    }
    private string? Pack()
    {
        if(!owner.Managed)return null;
        // Cell-index addressing is stable across append-only expansion. Only active channels are written.
        var current=pendingAir??volumes.Where(v=>v.Values.Any(x=>x>.0001f)).SelectMany(v=>v.Cells.Select(c=>(c,v.Values))).ToDictionary(x=>x.c,x=>x.Values);
        if(current.Count==0)return null;
        using var output=new MemoryStream();
        using(var zip=new DeflateStream(output,CompressionLevel.Fastest,true))
        using(var writer=new BinaryWriter(zip))
        {
            writer.Write(1);writer.Write(current.Count);
            foreach(var pair in current.OrderBy(p=>owner.map.cellIndices.CellToIndex(p.Key)))
            {
                writer.Write(owner.map.cellIndices.CellToIndex(pair.Key));
                foreach(float value in pair.Value)writer.Write((byte)Math.Clamp((int)Math.Round(value*255),0,255));
            }
        }
        return Convert.ToBase64String(output.ToArray());
    }
    private Dictionary<IntVec3,float[]> Unpack(string? data)
    {
        var result=new Dictionary<IntVec3,float[]>();if(string.IsNullOrEmpty(data))return result;
        using var input=new MemoryStream(Convert.FromBase64String(data));
        using var zip=new DeflateStream(input,CompressionMode.Decompress);
        using var reader=new BinaryReader(zip);
        if(reader.ReadInt32()!=1)throw new InvalidDataException("Unsupported RimLayers atmosphere version.");
        int count=reader.ReadInt32();
        if(count<0||count>owner.map.cellIndices.NumGridCells)throw new InvalidDataException("Invalid atmosphere cell count.");
        for(int n=0;n<count;n++)
        {
            int index=reader.ReadInt32();var value=new float[6];
            for(int i=0;i<6;i++)value[i]=reader.ReadByte()/255f;
            result.Add(CellIndicesUtility.IndexToCell(index,owner.map.Size.x),value);
        }
        return result;
    }
}
