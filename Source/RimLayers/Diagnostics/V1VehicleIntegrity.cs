using RimLayers.Vehicles;
using Verse;
namespace RimLayers.Diagnostics;
internal static partial class V1Integration
{
    private static void DamageVehicleFixture(Pawn vehicle)
    {
        var handler=FrameworkVehicleAdapter.Field(vehicle.GetType(),"statHandler").GetValue(vehicle)!;
        var component=((System.Collections.IEnumerable)FrameworkVehicleAdapter.Field(handler.GetType(),"components").GetValue(handler)!).Cast<object>().First();
        float health=Convert.ToSingle(FrameworkVehicleAdapter.Property(component.GetType(),"Health").GetValue(component));
        FrameworkVehicleAdapter.Method(component.GetType(),"SetHealth",typeof(void),typeof(float)).Invoke(component,new object[]{health*.83f});
        Check("fixture-starts-with-persistent-component-damage",Convert.ToSingle(FrameworkVehicleAdapter.Property(component.GetType(),"Health").GetValue(component))<health);
    }
    private static float[] VehicleIntegrity(Pawn vehicle)
    {
        var handler=FrameworkVehicleAdapter.Field(vehicle.GetType(),"statHandler").GetValue(vehicle)!;
        var components=((System.Collections.IEnumerable)FrameworkVehicleAdapter.Field(handler.GetType(),"components").GetValue(handler)!).Cast<object>().ToArray();
        var health=components.Select(c=>Convert.ToSingle(FrameworkVehicleAdapter.Property(c.GetType(),"Health").GetValue(c)));
        var fuel=vehicle.AllComps.Where(c=>c.GetType().FullName=="Vehicles.CompFueledTravel")
            .Select(c=>Convert.ToSingle(FrameworkVehicleAdapter.Property(c.GetType(),"Fuel").GetValue(c)));
        return new[]{(float)components.Length}.Concat(health).Concat(fuel).ToArray();
    }
    private static bool IntegrityMatches(float[] expected,Pawn vehicle)
    {
        var actual=VehicleIntegrity(vehicle);
        return expected.Length==actual.Length&&expected.Zip(actual).All(pair=>Math.Abs(pair.First-pair.Second)<.001f);
    }
    private static void BindBoardIntegrity()
    {
        new HarmonyLib.Harmony("rimlayers.vehicles.integrity.test").Patch(HarmonyLib.AccessTools.Method(typeof(VehicleTraffic),"Board"),
            prefix:new HarmonyLib.HarmonyMethod(typeof(V1Integration),nameof(BeforeVehicleBoard)),postfix:new HarmonyLib.HarmonyMethod(typeof(V1Integration),nameof(AfterVehicleBoard)));
    }
    private static void BeforeVehicleBoard(VehicleTravelState trip,out float[] __state)=>__state=VehicleIntegrity(trip.Vehicle);
    private static void AfterVehicleBoard(VehicleTravelState trip,float[] __state)=>Check("boarding-preserves-fuel-and-component-damage",IntegrityMatches(__state,trip.Vehicle),trip.Vehicle.ThingID);
}
