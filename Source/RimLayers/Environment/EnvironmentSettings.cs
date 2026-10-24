using System.Reflection;
using Verse;

namespace RimLayers.Environment;

/// <summary>Global tuning only. No copy of these values is stored in a colony.</summary>
public sealed class EnvironmentSettings : IExposable
{
    public float RockOffset=0, GeothermalGradient=.25f, RockResponseDays=3;
    public float SlabHeat=1, OpeningHeat=1, AltitudeLapse=.02f, WindStrength=1, AirExchange=1, PollutionHalfLife=1;
    public bool SolarShield=true, FloodDown=true, LavaDown=true, LavaEnabled=true;
    public bool EcologyEnabled=true;
    public float WeatherPenalty=1, TornadoDamage=1;
    public float MineralGrowth=.05f, MineralCap=1.4f, MineralDepth=1, LumpSize=1, GeothermalEnrichment=1;
    public int LavaStart=6, VolcanicLavaStart=3, LavaMinSize=24, LavaMaxSize=96;
    public float LavaCoverage=.005f, LavaCap=.04f, LavaGrowth=2, LavaHeat=1;
    public float InitialHiveDensity=1, InfestationWeight=1, LightSuppression=1, InsectCold=-17, InsectHot=60;
    public float InsectSpawnSpeed=1, HiveReproductionSpeed=1;
    public float EcologySoil=.35f, EcologyGeysers=.85f, EcologyPlants=1;
    public Dictionary<string,float> MineralWeights=new();
    public Dictionary<string,float> PlantWeights=new();

    public sealed record Parameter(string Name,string Group,float Default,float Min,float Max,string Unit,bool Generation=false,bool Advanced=false,bool Integer=false)
    {
        internal FieldInfo Field => typeof(EnvironmentSettings).GetField(Name)!;
        public float Get(EnvironmentSettings settings)=>Convert.ToSingle(Field.GetValue(settings));
        public void Set(EnvironmentSettings settings,float value) { if(Integer)Field.SetValue(settings,(int)Math.Round(value));else Field.SetValue(settings,value); }
    }
    public static readonly Parameter[] Parameters =
    {
        new(nameof(RockOffset),"Temperature",0,-30,30,"°C"),
        new(nameof(GeothermalGradient),"Temperature",.25f,0,2,"°C/B"),
        new(nameof(RockResponseDays),"Temperature",3,.25f,30,"d × B²",Advanced:true),
        new(nameof(SlabHeat),"Temperature",1,0,4,"×",Advanced:true),
        new(nameof(OpeningHeat),"Temperature",1,0,4,"×",Advanced:true),
        new(nameof(AltitudeLapse),"Temperature",.02f,0,.2f,"°C/F",Advanced:true),
        new(nameof(WindStrength),"Temperature",1,0,2,"×",Advanced:true),
        new(nameof(AirExchange),"Temperature",1,0,4,"×"),
        new(nameof(PollutionHalfLife),"Temperature",1,.1f,24,"h",Advanced:true),
        new(nameof(WeatherPenalty),"Disasters",1,0,2,"×"),
        new(nameof(TornadoDamage),"Disasters",1,0,3,"×"),
        new(nameof(MineralGrowth),"Minerals",.05f,0,.2f,"%/B",true),
        new(nameof(MineralCap),"Minerals",1.4f,1,3,"×",true),
        new(nameof(MineralDepth),"Minerals",1,0,2,"×",true),
        new(nameof(LumpSize),"Minerals",1,.5f,2,"×",true,true),
        new(nameof(GeothermalEnrichment),"Minerals",1,0,3,"×",true),
        new(nameof(LavaStart),"Lava",6,1,9,"B",true,Integer:true),
        new(nameof(VolcanicLavaStart),"Lava",3,1,9,"B",true,Integer:true),
        new(nameof(LavaCoverage),"Lava",.005f,0,.05f,"fraction",true),
        new(nameof(LavaCap),"Lava",.04f,0,.1f,"fraction",true),
        new(nameof(LavaGrowth),"Lava",2,1,3,"×/B",true,true),
        new(nameof(LavaMinSize),"Lava",24,8,256,"cells",true,true,true),
        new(nameof(LavaMaxSize),"Lava",96,8,256,"cells",true,true,true),
        new(nameof(LavaHeat),"Lava",1,0,4,"×"),
        new(nameof(InitialHiveDensity),"Insects",1,0,3,"×",true),
        new(nameof(InfestationWeight),"Insects",1,0,3,"×"),
        new(nameof(LightSuppression),"Insects",1,0,2,"×"),
        new(nameof(InsectCold),"Insects",-17,-50,0,"°C",Advanced:true),
        new(nameof(InsectHot),"Insects",60,40,120,"°C",Advanced:true),
        new(nameof(InsectSpawnSpeed),"Insects",1,0,3,"×"),
        new(nameof(HiveReproductionSpeed),"Insects",1,0,3,"×"),
        new(nameof(EcologySoil),"Ecology",.35f,0,1,"fraction",true),
        new(nameof(EcologyGeysers),"Ecology",.85f,0,5,"/10k",true),
        new(nameof(EcologyPlants),"Ecology",1,0,3,"×",true)
    };
    public void ExposeData()
    {
        foreach(var p in Parameters)
        {
            if(p.Integer){int value=(int)p.Get(this);Scribe_Values.Look(ref value,p.Name,(int)p.Default);p.Set(this,value);}
            else {float value=p.Get(this);Scribe_Values.Look(ref value,p.Name,p.Default);p.Set(this,value);}
        }
        Scribe_Values.Look(ref SolarShield,"solarShield",true);
        Scribe_Values.Look(ref FloodDown,"floodDown",true);
        Scribe_Values.Look(ref LavaDown,"lavaDown",true);
        Scribe_Values.Look(ref LavaEnabled,"lavaEnabled",true);
        Scribe_Values.Look(ref EcologyEnabled,"ecologyEnabled",true);
        Scribe_Collections.Look(ref MineralWeights,"mineralWeights",LookMode.Value,LookMode.Value);
        Scribe_Collections.Look(ref PlantWeights,"plantWeights",LookMode.Value,LookMode.Value);
        if(Scribe.mode==LoadSaveMode.PostLoadInit)Normalize();
    }
    public void Normalize()
    {
        foreach(var p in Parameters)
        {
            float v=p.Get(this);
            p.Set(this,float.IsFinite(v)&&v>=p.Min&&v<=p.Max?v:p.Default);
        }
        LavaCap=Math.Max(LavaCoverage,LavaCap);
        LavaMaxSize=Math.Max(LavaMinSize,LavaMaxSize);
        MineralWeights??=new();
        foreach(string key in MineralWeights.Keys.ToArray())
            if(!float.IsFinite(MineralWeights[key])||MineralWeights[key]<0||MineralWeights[key]>5)MineralWeights[key]=1;
        PlantWeights??=new();
        foreach(string key in PlantWeights.Keys.ToArray())
            if(!float.IsFinite(PlantWeights[key])||PlantWeights[key]<0||PlantWeights[key]>5)PlantWeights.Remove(key);
    }
    public float Weight(string defName)=>MineralWeights.GetValueOrDefault(defName,1);
    public float PlantWeight(string defName)=>PlantWeights.GetValueOrDefault(defName,1);
    public void ResetGroup(string group)
    {
        foreach(var p in Parameters.Where(p=>p.Group==group))p.Set(this,p.Default);
        if(group=="Disasters"){SolarShield=true;FloodDown=true;LavaDown=true;}
        if(group=="Lava")LavaEnabled=true;
        if(group=="Minerals")MineralWeights.Clear();
        if(group=="Ecology"){EcologyEnabled=true;PlantWeights.Clear();}
    }
}
