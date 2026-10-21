using System.Collections;
using System.Reflection;
using HarmonyLib;
using RimLayers.Core;
using Verse;
using Verse.AI;

namespace RimLayers.Vehicles;

/// <summary>Provider capabilities install independently; diagnostic overrides require isolated test data.</summary>
[StaticConstructorOnStartup]
internal static class VehicleCompatibility
{
    internal static IVehicleFlightAdapter? Flight {get;private set;}
    internal static IVehicleAdapter? Adapter {get;private set;}
    internal static VehicleCapabilities Capabilities {get;private set;}
    internal static string Diagnostic {get;private set;}="Not installed";
    internal static string EnhancementDiagnostic {get;private set;}="Not installed";
    private static FrameworkVehicleAdapter? bindings;
    private static MethodInfo? start;
    private static readonly Harmony ground=new("rimlayers.vehicles.ground");
    static VehicleCompatibility()
    {
        bool probe=GenCommandLine.CommandLineArgPassed("rimlayersvehiclebindings");
        bool prototype=GenCommandLine.CommandLineArgPassed("rimlayersvehiclesprototype");
        bool diagnostic=probe||prototype||GenCommandLine.CommandLineArgPassed("rimlayersvehiclebasic");
        if(diagnostic)
        {
            string artifacts=System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(VehicleCompatibility).Assembly.Location)!,"..","Tests","Artifacts"));
            string profile=System.IO.Path.GetFullPath(GenFilePaths.SaveDataFolderPath);
            if(!string.Equals(new System.IO.DirectoryInfo(profile).Parent?.FullName,artifacts,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Vehicle diagnostic tools require a dedicated RimLayers/Tests/Artifacts/<run> profile.");
        }
        if(!probe||prototype)Install();
        if(probe)Probe();
    }
    private static FrameworkVehicleAdapter? Bind()
    {
        // Steel Horizon aliases ModsConfig.IsActive for upstream content. Provider identity must use the actual loaded packs.
        bool Enabled(string id)=>LoadedModManager.RunningModsListForReading.Any(m=>m.PackageId.Equals(id,StringComparison.OrdinalIgnoreCase));
        bool steel=Enabled("Baal.VehicleFramework"),upstream=Enabled("SmashPhil.VehicleFramework");
        if(steel&&upstream)throw new InvalidOperationException("Both Vehicle Framework providers are enabled. Enable only one.");
        if(!steel&&!upstream){Diagnostic="No Vehicle Framework provider enabled";return null;}
        // Upstream 1.7 has not yet been validated. An old 1.6 DLL must never opt itself into these hooks.
        if(upstream)throw new NotSupportedException("Upstream Vehicle Framework requires a verified RimWorld 1.7 adapter.");
        var provider=LoadedModManager.RunningModsListForReading.Single(m=>m.PackageId.Equals("baal.vehicleframework",StringComparison.OrdinalIgnoreCase));
        var assembly=provider.assemblies.loadedAssemblies.Single(a=>a.GetName().Name=="Vehicles");
        return new FrameworkVehicleAdapter(assembly,provider.PackageId);
    }
    private static void Probe()
    {
        try
        {
            if(GenCommandLine.CommandLineArgPassed("rimlayersvehiclesprototype"))
            {if(Adapter!=null)Diagnostic="Ground hook installation accepted";}
            else {var result=Bind();Diagnostic=result==null?Diagnostic:"Steel Horizon ground binding signatures accepted";}
        }
        catch(Exception e){Diagnostic=e.ToString();}
        Log.Message("[RimLayers vehicles] "+Diagnostic);
        if(GenCommandLine.TryGetCommandLineArg("rimlayersvehiclebindingreport",out var file))
        {
            var destination=System.IO.Path.GetFullPath(file);
            var allowed=System.IO.Path.GetFullPath(System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath,"vehicle-bindings.txt"));
            if(destination!=allowed)throw new InvalidOperationException("Binding report must be in this test profile.");
            System.IO.File.WriteAllText(destination,Diagnostic);
            LongEventHandler.ExecuteWhenFinished(()=>
            {
                var inventory=LoadedModManager.RunningModsListForReading.Select(m=>new
                {
                    m.PackageId,m.RootDir,
                    assemblies=m.assemblies.loadedAssemblies.Select(a=>new{name=a.GetName().Name,path=a.Location}).ToArray(),
                    vehicles=m.AllDefs.OfType<ThingDef>().Where(d=>d.GetType().FullName=="Vehicles.VehicleDef")
                        .Select(d=>new{d.defName,d.label,size=d.size.ToString(),type=d.thingClass?.FullName}).ToArray()
                }).ToArray();
                System.IO.File.WriteAllText(System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath,"vehicle-content.json"),
                    System.Text.Json.JsonSerializer.Serialize(inventory,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            });
            if(GenCommandLine.CommandLineArgPassed("rimlayersvehicleprobeexit"))LongEventHandler.ExecuteWhenFinished(UnityEngine.Application.Quit);
        }
    }
    private static void Install()
    {
        try
        {
            bindings=Bind();if(bindings==null)return;
            var type=bindings.PatherType;
            start=FrameworkVehicleAdapter.Method(type,"StartPath",typeof(void),typeof(LocalTargetInfo),typeof(PathEndMode),typeof(bool));
            void Patch(MethodBase original,string before)=>ground.Patch(original,prefix:new HarmonyMethod(typeof(VehicleCompatibility),before){priority=Priority.First});
            Patch(start,nameof(StartPrefix));
            Patch(AccessTools.Method(typeof(GenSpawn),nameof(GenSpawn.SpawnBuildingAsPossible)),nameof(RestoreRampPrefix));
            ground.Patch(AccessTools.PropertyGetter(typeof(MapPawns),"AnyPawnBlockingMapRemoval"),postfix:new HarmonyMethod(typeof(VehicleCompatibility),nameof(HeldBlocksRemoval)));
            Patch(FrameworkVehicleAdapter.Method(type,"PatherTick",typeof(void)),nameof(TickPrefix));
            ground.Patch(FrameworkVehicleAdapter.Property(bindings.VehicleType,"DrawPos").GetMethod!,postfix:new HarmonyMethod(typeof(VehicleCompatibility),nameof(DrawPositionPostfix)){priority=Priority.Last});
            Patch(FrameworkVehicleAdapter.Method(type,"PatherArrived",typeof(void)),nameof(ArrivedPrefix));
            Patch(FrameworkVehicleAdapter.Method(type,"PatherFailed",typeof(void)),nameof(FailedPrefix));
            Patch(FrameworkVehicleAdapter.Method(type,"StopDead",typeof(void)),nameof(StopPrefix));
            Patch(bindings.Validation,nameof(ValidateLegPrefix));
            var reach=bindings.VehicleType.Assembly.GetType("Vehicles.VehicleReachabilityUtility",true)!;
            Patch(FrameworkVehicleAdapter.Method(reach,"CanReachVehicle",typeof(bool),bindings.VehicleType,typeof(LocalTargetInfo),typeof(PathEndMode),typeof(Danger),typeof(TraverseMode)),nameof(ReachPrefix));
            foreach(var method in AccessTools.GetDeclaredMethods(typeof(Pawn_JobTracker)).Where(m=>m.Name=="StopAll"))Patch(method,nameof(StopJobsPrefix));
            var despawn=FrameworkVehicleAdapter.Method(bindings.VehicleType,"DeSpawn",typeof(void),typeof(DestroyMode));
            ground.Patch(despawn,prefix:new HarmonyMethod(typeof(VehicleCompatibility),nameof(BeforeDeSpawn)),finalizer:new HarmonyMethod(typeof(VehicleCompatibility),nameof(AfterDeSpawn)));
            ground.Patch(FrameworkVehicleAdapter.Method(bindings.VehicleType,"GetGizmos",typeof(IEnumerable<Gizmo>)),postfix:new HarmonyMethod(typeof(VehicleCompatibility),nameof(GizmosPostfix)));
            Adapter=bindings;Capabilities=VehicleCapabilities.GroundMovement;
            if(FrameworkWorldAdapter.TryInstall(bindings,out var worldDiagnostic))Capabilities|=VehicleCapabilities.WorldTravel;
            else Log.Warning("[RimLayers vehicles] "+worldDiagnostic);
            var pack=LoadedModManager.RunningModsListForReading.Single(m=>m.PackageId.Equals("baal.vehicleframework",StringComparison.OrdinalIgnoreCase));
            var enhancementAssembly=pack.assemblies.loadedAssemblies.SingleOrDefault(a=>a.GetName().Name=="VehicleFramework.LocalUpgrade");
            if(enhancementAssembly!=null&&!GenCommandLine.CommandLineArgPassed("rimlayersvehiclebasic"))
            {
                var enhancement=SteelGroundMotionAdapter.TryInstall(enhancementAssembly,bindings,out var detail);
                EnhancementDiagnostic=detail;
                if(enhancement!=null)bindings.AfterCommit=enhancement.Rebase;
                else Log.Warning("[RimLayers vehicles] "+detail);
                if(SteelManualAdapter.TryInstall(enhancementAssembly,bindings,out var manualDiagnostic))Capabilities|=VehicleCapabilities.ManualDriving;
                else Log.Warning("[RimLayers vehicles] "+manualDiagnostic);
                EnhancementDiagnostic+="; "+manualDiagnostic;
                if(SteelFlightAdapter.TryInstall(enhancementAssembly,bindings,out var flightDiagnostic)){Flight=SteelFlightAdapter.Installed;Capabilities|=VehicleCapabilities.ContinuousFlight;}
                else Log.Warning("[RimLayers vehicles] "+flightDiagnostic);
                EnhancementDiagnostic+="; "+flightDiagnostic;
            }
            try{VehicleCapabilityGuards.Install(bindings,enhancementAssembly);}
            catch(Exception e){Log.Error("[RimLayers vehicles] Capability entry-point guards unavailable: "+e);}
            Diagnostic="Enabled provider capabilities: "+Capabilities;
            Log.Message("[RimLayers vehicles] "+Diagnostic);
        }
        catch(Exception e)
        {
            ground.UnpatchAll(ground.Id);Adapter=null;bindings=null;Capabilities=VehicleCapabilities.None;
            Diagnostic=e.ToString();Log.Error("[RimLayers vehicles] Ground integration disabled: "+Diagnostic);
        }
    }
    private static bool RestoreRampPrefix(Building building,Map map,bool respawningAfterLoad)
    {
        if(!respawningAfterLoad||building is not Building_VehicleConnector {IsLift:false} ramp||
            LayerMapComponent.For(map) is not {} owner||!ramp.OccupiedRect().InBounds(map))return true;
        var traffic=map.GetComponent<VehicleTraffic>();bool carriesVehicle=false;
        var profile=VehicleRampService.Profile(owner,ramp);
        foreach(var cell in ramp.OccupiedRect())
        foreach(var thing in cell.GetThingList(map))
        {
            if(thing is Pawn pawn)
            {
                if(traffic.Trip(pawn) is not {Phase:VehicleTravelPhase.Crossing} trip||
                    Math.Abs(trip.Ramp.BaseHeight-profile.BaseHeight)>1e-6||
                    Math.Abs(VehicleGeometry.SignedTurn(trip.Ramp.Yaw,profile.Yaw))>1e-6||
                    Math.Abs(trip.Ramp.Along(profile.StartX,profile.StartZ))>1e-6)return true;
                carriesVehicle=true;
            }
            else if((thing.def.category==ThingCategory.Building||thing.def.category==ThingCategory.Item)&&GenSpawn.SpawningWipes(ramp.def,thing.def))return true;
        }
        if(!carriesVehicle)return true;
        // Native loading spawns Pawns first, then refunds impassable buildings underneath
        // them. A saved vehicle on its own grade is the one legitimate overlap. Its saved
        // position/rotation are already set; restore the original building without wiping
        // contents or invoking vanilla Pawn displacement out of this owned transport.
        ramp.SpawnSetup(map,true);
        return false;
    }
    internal static void Resume(Pawn pawn,LocalTargetInfo target,PathEndMode mode)=>start!.Invoke(bindings!.Pather(pawn),new object[]{target,mode,false});
    private static bool StartPrefix(object __instance,LocalTargetInfo dest,PathEndMode peMode)
    {
        if(VehicleTraffic.InternalCommand||bindings==null)return true;
        var pawn=bindings.Actor(__instance);var owner=LayerMapComponent.For(pawn.Map);
        if(owner!=null&&Flight is {} flight&&flight.Active(pawn))
        {
            var saved=pawn.Map.GetComponent<VehicleFlight>().State(pawn);
            // The provider reloads its current proxy destination. It is an intermediate
            // coordinate, not a replacement for the same saved Job's final logical goal.
            var final=saved is {RouteActive:true}&&saved.JobId==(pawn.CurJob?.loadID??-1)?saved.Destination:dest;
            if(!flight.BeginRoute(pawn,final,peMode))VehicleTraffic.Command(()=>bindings.Fail(pawn));
            return false;
        }
        if(pawn.Spawned&&pawn.Map.GetComponent<VehicleTraffic>().Trip(pawn) is {} active)
        {
            if(active.JobId==(pawn.CurJob?.loadID??-1)&&dest.Cell==active.EntryCell)return false;
            if(active.Phase==VehicleTravelPhase.Crossing){pawn.Map.GetComponent<VehicleTraffic>().Cancel(pawn);return false;}
        }
        if(owner==null||!owner.TryDecode(pawn.Position,out var from)||!owner.TryDecode(dest.Cell,out var to)||from.LayerId==to.LayerId)
        {if(pawn.Spawned)pawn.Map.GetComponent<VehicleTraffic>().Cancel(pawn);return true;}
        if(!pawn.Map.GetComponent<VehicleTraffic>().Begin(pawn,dest,peMode))
        {
            pawn.Map.GetComponent<VehicleTraffic>().Cancel(pawn);
            VehicleTraffic.Command(()=>bindings.Fail(pawn));
            Messages.Message("RL_VehicleRouteLost".Translate(),pawn,RimWorld.MessageTypeDefOf.RejectInput,false);
        }
        return false;
    }
    private static void GizmosPostfix(Pawn __instance,ref IEnumerable<Gizmo> __result)
    {if(__instance.Spawned)__result=__result.Concat(__instance.Map.GetComponent<VehicleTraffic>().VehicleGizmos(__instance));}
    private static void HeldBlocksRemoval(Map ___map,ref bool __result)
    {if(___map.GetComponent<VehicleTraffic>().GetDirectlyHeldThings().Count>0||VehicleWorldTravel.Current.HasWaiting(___map))__result=true;}
    private static bool FailedPrefix(object __instance)
    {
        if(VehicleTraffic.InternalCommand)return true;
        var pawn=bindings?.Actor(__instance);
        // Native spawn recalculates ground permissions; an authored zero taxi speed does not
        // invalidate a saved airborne order. Flight power/failures are checked by the flight comp.
        if(pawn?.Spawned==true&&Flight is {} air&&air.OwnsRoute(pawn)&&air.Active(pawn))return false;
        return pawn?.Spawned!=true||!pawn.Map.GetComponent<VehicleTraffic>().DeferNativeFailure(pawn);
    }
    private static bool TickPrefix(object __instance)
    {
        var pawn=bindings?.Actor(__instance);
        return pawn?.Spawned!=true||pawn.Map.GetComponent<VehicleTraffic>().AllowNativeTick(pawn,bindings!.NextCell(pawn));
    }
    private static void DrawPositionPostfix(Pawn __instance,ref UnityEngine.Vector3 __result)
    {
        if(__instance.Spawned&&__instance.Map.GetComponent<VehicleTraffic>().DrawPosition(__instance) is {} position)__result=position;
    }
    private static bool ArrivedPrefix(object __instance)
    {
        var pawn=bindings?.Actor(__instance);
        if(!VehicleTraffic.InternalCommand&&pawn?.Spawned==true&&Flight?.OwnsRoute(pawn)==true)return false;
        return pawn?.Spawned!=true||!pawn.Map.GetComponent<VehicleTraffic>().IntermediateArrival(pawn);
    }
    private static void StopPrefix(object __instance)
    {
        if(!VehicleTraffic.InternalCommand&&bindings?.Actor(__instance) is {Spawned:true} pawn)
        {
            if(pawn.Map.GetComponent<VehicleFlight>().State(pawn) is {RouteActive:true} air&&(pawn.CurJob?.loadID??-1)!=air.JobId)
                Flight?.CancelRoute(pawn);
            var traffic=pawn.Map.GetComponent<VehicleTraffic>();
            // The framework's idle job periodically calls StopDead even during WASD. A manual
            // ramp has no native Goto to cancel; braking/leaving control must retain its safe pose.
            if(traffic.Trip(pawn)?.Manual!=true)traffic.Cancel(pawn);
        }
    }
    private static bool ValidateLegPrefix(IntVec3 start,LocalTargetInfo dest,TraverseParms traverseParms,ref bool __result)
    {
        if(!FrameworkVehicleAdapter.FindingLeg)return true;
        var pawn=traverseParms.pawn;var owner=LayerMapComponent.For(pawn?.Map);
        __result=owner!=null&&pawn!.Spawned&&owner.TryDecode(start,out var from)&&owner.TryDecode(dest.Cell,out var to)&&from.LayerId==to.LayerId&&(!dest.HasThing||dest.Thing.MapHeld==pawn.Map);
        return false;
    }
    private static bool ReachPrefix(object __0,LocalTargetInfo dest,PathEndMode peMode,ref bool __result)
    {
        if(FrameworkVehicleAdapter.FindingLeg||__0 is not Pawn pawn||LayerMapComponent.For(pawn.Map) is not {} owner||
           !owner.TryDecode(pawn.Position,out var from)||!owner.TryDecode(dest.Cell,out var to)||from.LayerId==to.LayerId)return true;
        if(Flight?.Active(pawn)==true){__result=true;return false;}
        __result=VehicleRoutePlanner.FirstCrossing(pawn.Map.GetComponent<VehicleTraffic>(),pawn,dest,peMode)!=null;return false;
    }
    private static bool StopJobsPrefix(Pawn ___pawn)=>___pawn!=VehicleTraffic.Boarding;
    private static void BeforeDeSpawn(Pawn __instance,out (IList? List,object[]? Contents) __state)
    {
        __state=default;
        if(__instance!=VehicleTraffic.Boarding)return;
        var list=FrameworkVehicleAdapter.Field(__instance.GetType(),"cargoToLoad").GetValue(__instance) as IList;
        if(list!=null)__state=(list,list.Cast<object>().ToArray());
    }
    private static void AfterDeSpawn((IList? List,object[]? Contents) __state)
    {
        if(__state.List==null||__state.Contents==null)return;
        __state.List.Clear();foreach(var item in __state.Contents)__state.List.Add(item);
    }
}
