using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimLayers.Compatibility;
using RimLayers.Core;
using RimLayers.Layout;
using RimLayers.Structure;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>
/// Ship combat on a map that was born in orbit, not carved out of a colony map.
/// </summary>
/// <remarks>
/// The existing SOS2 scenario launches a hull from the ground and then tries to fight on whatever
/// map it lands on. That cannot answer the question a player reported - whether a shield on one deck
/// stops fire aimed at another - because SOS2 only fights on an orbiting map:
/// <c>ShipMapComp.StartShipEncounter</c> reads <c>((WorldObjectOrbitingShip)Map.Parent).Theta</c> to
/// place the enemy fleet, and a colony map has a <c>Settlement</c> there, which throws. Turrets also
/// refuse to fire unless <c>ShipMapState</c> is <c>inCombat</c>, which only an encounter sets.
/// <para>
/// So this scenario starts where a space-based player starts: it makes a real
/// <c>WorldObjectOrbitingShip</c>, generates its map through SOS2's own <c>EmptySpaceMap</c> generator,
/// builds a two-deck hull on it, puts a shield on the lower deck, starts a genuine encounter, and then
/// lets SOS2's own turrets and its own projectile list do the shooting.
/// </para>
/// </remarks>
internal static partial class V1Integration
{
    private static Map? space;
    private static LayerMapComponent? hull;
    private static Building? bridge;
    private static Building? shield;
    private static Building? engine;
    private static Building? turret;
    private static Building? reactor;
    private static bool started;
    private static int phase;
    private static int spaceTicks;

    /// <summary>Deck rectangle this scenario owns, in band-0 storage coordinates.</summary>
    private static readonly CellRect Pad = new(60, 60, 15, 13);

    /// <summary>Entry point for <c>-rimlayerscase=sos2-space</c>.</summary>
    private static void BeginSos2Space()
    {
        RestoreSos2Space();
        Check("sos2-space-adapter-is-installed", Sos2Compatibility.Active, "run with -Sos2");
        if (!Sos2Compatibility.Active) { Finish(); return; }
        stage = 604;
        spaceTicks = 0;
    }

    /// <summary>Restores the scenario from a checkpoint; the space map is rebuilt either way.</summary>
    private static void RestoreSos2Space()
    {
        space = null;
        hull = null;
        bridge = shield = engine = reactor = turret = null;
        started = false;
        phase = 0;
        spaceTicks = 0;
    }

    private static void Sos2SpaceStep()
    {
        if (phase == 0)
        {
            BuildOrbit();
            phase = 1;
            spaceTicks = 0;
            return;
        }
        if (phase == 1 && ++spaceTicks > 3)
        {
            BuildHull();
            phase = 2;
            spaceTicks = 0;
            return;
        }
        if (phase == 2 && ++spaceTicks > 3)
        {
            StartEncounter();
            phase = 3;
            spaceTicks = 0;
            return;
        }
        if (phase == 3)
        {
            WatchCombat();
            phase = 4;
            spaceTicks = 0;
            return;
        }
        if (phase == 4)
        {
            Finish();
            phase = 5;
        }
    }

    /// <summary>
    /// Makes the orbiting map the way SOS2 does, so its parent is the type every combat path expects.
    /// </summary>
    private static void BuildOrbit()
    {
        Type? orbiterType = AccessTools.TypeByName("SaveOurShip2.WorldObjectOrbitingShip");
        if (orbiterType == null)
        {
            Check("sos2-space-world-object-type", false, "SaveOurShip2.WorldObjectOrbitingShip not found");
            return;
        }
        WorldObjectDef? def = DefDatabase<WorldObjectDef>.GetNamedSilentFail("ShipOrbiting")
            ?? DefDatabase<WorldObjectDef>.AllDefs.FirstOrDefault(d => d.worldObjectClass == orbiterType);
        if (def == null)
        {
            Check("sos2-space-world-object-def", false, $"defs={DefDatabase<WorldObjectDef>.DefCount}");
            return;
        }
        var orbiter = (MapParent)WorldObjectMaker.MakeWorldObject(def);
        orbiter.SetFaction(Faction.OfPlayer);
        // Adding an object whose tile is unset gets its tile forced to 0, with a log error this
        // scenario would then report as a runtime error. SOS2's own launch finds a free orbit tile
        // first; do the same instead of relying on the fallback.
        Type? interiorType = AccessTools.TypeByName("SaveOurShip2.ShipInteriorMod2");
        object? tile = AccessTools.Method(interiorType, "FindWorldTileOnLayers", new[] { typeof(bool) })
            ?.Invoke(null, new object[] { true });
        if (tile is PlanetTile found && found.Valid) orbiter.Tile = found;
        // SetNominalPos is what gives the object its theta/phi; without it the enemy fleet would be
        // placed at (0,0) and every ship would exist on top of the player's.
        AccessTools.Method(orbiterType, "SetNominalPos")?.Invoke(orbiter, null);
        Find.WorldObjects.Add(orbiter);
        MapGeneratorDef? generator = orbiter.MapGeneratorDef;
        if (generator == null)
        {
            Check("sos2-space-generator", false, "the orbiter has no map generator");
            return;
        }
        IntVec3 size = new(120, 1, 120);
        // Hand the generator the band count a two-deck hull needs. The adapter reads this before the
        // map exists, so a hull with two decks is not later moved onto a one-band map.
        Sos2Compatibility.ExpectSpaceLayout(size);
        space = MapGenerator.GenerateMap(size, orbiter, generator, null, null, false);
        Check("sos2-space-map-exists", space != null && !space.Disposed, $"map={space}");
        if (space == null) return;
        hull = LayerMapComponent.For(space);
        Check("sos2-space-map-has-bands", hull?.Layout is { LayerCount: >= 2 },
            $"layout={hull?.Layout}, size={space.Size}");
        Check("sos2-space-map-parent-is-an-orbiter",
            space.Parent != null && orbiterType.IsInstanceOfType(space.Parent),
            $"parent={space.Parent?.GetType().Name}");
        Check("sos2-space-map-is-space", Sos2Compatibility.IsSpaceMap(space),
            $"biome={space.Biome?.defName}");
        Check("sos2-space-map-has-ground-under-the-hull", GroundUnder(space) > 0,
            $"ground={GroundUnder(space)}");
    }

    /// <summary>Cells on band 0 that can hold a building, so a hull has somewhere to stand.</summary>
    private static int GroundUnder(Map target)
    {
        int solid = 0;
        foreach (IntVec3 c in LayerMapComponent.For(target)?.LayerRect(0) ?? default)
            if (target.terrainGrid.FoundationAt(c) != null || target.terrainGrid.TerrainAt(c)?.affordances?.Contains(TerrainAffordanceDefOf.Heavy) == true)
                solid++;
        return solid;
    }

    /// <summary>A two-deck hull with a bridge, an engine, power and a shield on the lower deck.</summary>
    private static void BuildHull()
    {
        if (space == null || hull?.Layout is not { LayerCount: >= 2 }) return;
        int stride = hull.Layout.Stride;
        int height = hull.Layout.Height;

        // Hull plating first: SOS2 only claims a cell that carries a ship-cache part, so a bridge on
        // bare ground becomes a wreck core. Plating goes down with SOS2's own cache switched off (its
        // default on a fresh map), which is the path RecacheMap exists for - spawning a whole deck with
        // the cache live ran a full merge per tile and took the scenario past its time limit.
        object? shipComp = Sos2Compatibility.ShipCompOf(space);
        AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "CacheOff")?.SetValue(shipComp, true);
        for (int deck = 0; deck < 2; deck++)
        {
            foreach (IntVec3 band in hull.LayerRect(deck))
            {
                hull.TryDecode(band, out LayerCell at);
                if (at.X < Pad.minX || at.X > Pad.maxX || at.Z < Pad.minZ || at.Z > Pad.maxZ) continue;
                if (Sos2Compatibility.IsShipDeck(hull, band)) continue;
                Spawn("ShipHullTile", band);
            }
        }
        int plated = 0;
        int platedAbove = 0;
        foreach (IntVec3 c in Pad.Cells)
            plated += c.GetThingList(space).Count(t => t.def.defName == "ShipHullTile");
        foreach (IntVec3 c in hull.LayerRect(1))
            platedAbove += c.GetThingList(space).Count(t => t.def.defName == "ShipHullTile");
        AccessTools.Method(Sos2Compatibility.ShipMapCompType!, "RecacheMap")?.Invoke(shipComp, null);
        Check("sos2-space-hull-plating-count", plated > 0,
            $"plated={plated}, platedAbove={platedAbove}, shipCells={Sos2Compatibility.ShipCellsOf(space)?.Count ?? -1}, padArea={Pad.Area}");

        int cx = Pad.minX + Pad.Width / 2;
        int cz = Pad.minZ + Pad.Height / 2;
        bridge = Spawn("ShipPilotSeatMini", hull.AtElevation(1, cx, cz - 4), Rot4.North);
        engine = Spawn("Ship_Engine_Interplanetary", hull.AtElevation(0, cx, Pad.minZ + 2));
        reactor = Spawn("Ship_Reactor", hull.AtElevation(0, cx + 3, cz + 3));
        shield = Spawn("ShipCombatShieldGenerator", hull.AtElevation(0, cx - 4, cz));
        IntVec3 probe61 = hull.AtElevation(1, 61, Pad.minZ + 1);
        // A turret on the deck above the shield: the shield has to catch a shot aimed at a hull cell
        // its own deck cannot see, which is the whole question.
        // A 3x3 building takes its position as the minimum corner, so every occupied cell has to be
        // inside the hull: one corner hanging off the vessel makes the whole part read as unsupported.
        IntVec3? turretCell = null;
        for (int dx = 1; dx < Pad.Width - 3 && turretCell == null; dx++)
            for (int dz = 1; dz < Pad.Height - 3 && turretCell == null; dz++)
            {
                IntVec3 candidate = hull.AtElevation(1, Pad.minX + dx, Pad.minZ + dz);
                if (CellRect.SingleCell(candidate).ExpandedBy(1).Cells.All(c => Sos2Compatibility.MapClaims(space!, c)))
                    turretCell = candidate;
            }
        if (turretCell is { } spot) turret = Spawn("ShipTurret_Laser", spot);
        // SOS2 joins a heat net by cardinal adjacency, so two parts on the same deck a few cells
        // apart are two nets. Lay conduit from the shield down to the hull centre and up again on the
        // deck above, so every part sits on one net - exactly the plumbing a player builds.
        Recache();
        WireHeatNet(cx, cz);
        Recache();
        Check("sos2-space-hull-has-a-bridge", bridge != null && !bridge.DestroyedOrNull(),
            $"bridge={(bridge == null ? "none" : bridge.Position.ToString())}");
        Check("sos2-space-hull-has-a-shield", shield != null && !shield.DestroyedOrNull(),
            $"shield={(shield == null ? "none" : shield.Position.ToString())}");
        int index = bridge == null ? -1 : Sos2Compatibility.ShipIndexAt(space, bridge.Position);
        Check("sos2-space-hull-has-a-turret", turret != null && !turret.DestroyedOrNull(),
            $"turret={(turret == null ? "none" : turret.Position.ToString())}");
        Check("sos2-space-hull-is-one-ship", index >= 0 && Sos2Ship(index) != null,
            $"index={index}, ships={ShipCount()}, stride={stride}, height={height}");
    }

    /// <summary>
    /// Runs <c>ShipHeatConduit</c> between the parts of this hull.
    /// </summary>
    /// <remarks>
    /// A heat net is grown by walking cardinal neighbours from each heat part, so parts that are not
    /// touching are simply not on the same net. The adapter's vertical adjacency is what lets that walk
    /// cross a deck, and a fixture that never lays conduit therefore proves nothing about decks: it
    /// would report one net per part whether or not the adapter works.
    /// </remarks>
    private static void WireHeatNet(int cx, int cz)
    {
        if (hull == null) return;
        // One straight run per deck, plus the vertical pair that meets the deck above: a heat net is
        // grown by cardinal adjacency, so a chain of conduits is what joins the parts, and the
        // adapter's vertical adjacency is what lets the chain cross a deck. Laying a full grid here
        // instead made SOS2 log an "Attached cells" line per conduit and stalled the scenario.
        var run = new List<IntVec3>();
        for (int z = Pad.minZ + 2; z <= Pad.maxZ - 2; z++)
        {
            run.Add(hull.AtElevation(1, cx, z));
            if (z >= cz - 1 && z <= cz + 1)
                run.Add(hull.AtElevation(1, cx + 3, z));
        }
        for (int x = Pad.minX + 2; x <= Pad.maxX - 2; x++)
            if (x >= cx - 5 && x <= cx + 5) run.Add(hull.AtElevation(1, x, cz));
        foreach (IntVec3 cell in run)
        {
            if (!cell.InBounds(space!) || HasHeatPart(cell)) continue;
            Spawn("ShipHeatConduit", cell);
        }
        for (int z = Pad.minZ + 2; z <= Pad.maxZ - 2; z++)
        {
            IntVec3 cell = hull.AtElevation(0, cx, z);
            if (!cell.InBounds(space!) || HasHeatPart(cell)) continue;
            Spawn("ShipHeatConduit", cell);
        }
    }

    /// <summary>Whether anything on this cell is an SOS2 heat part, so conduit does not overwrite it.</summary>
    private static bool HasHeatPart(IntVec3 cell)
    {
        if (space == null) return false;
        return cell.GetThingList(space).Any(t => t is ThingWithComps twc
            && twc.AllComps.Any(c => Sos2Compatibility.ShipHeatType?.IsInstanceOfType(c) == true));
    }

    private static Building? Spawn(string defName, IntVec3 cell, Rot4? rot = null)
    {
        ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def == null || space == null) return null;
        foreach (Thing existing in cell.GetThingList(space).ToList())
            if (existing is not Pawn) existing.Destroy(DestroyMode.Vanish);
        Thing thing = ThingMaker.MakeThing(def);
        GenSpawn.Spawn(thing, cell, space, rot ?? Rot4.North);
        return thing as Building;
    }

    private static void Recache()
    {
        AccessTools.Method(Sos2Compatibility.ShipMapCompType!, "RecacheFromRoots")?.Invoke(Sos2Compatibility.ShipCompOf(space), null);
    }

    /// <summary>
    /// Starts the encounter the same call the bridge uses, then arms the shield.
    /// </summary>
    private static void StartEncounter()
    {
        if (space == null || shield == null) return;
        object? comp = Sos2Compatibility.ShipCompOf(space);
        MethodInfo? start = comp == null ? null : Sos2Compatibility.ShipMapCompType!
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "StartShipEncounter");
        if (start == null)
        {
            Check("sos2-space-encounter-method", false, "StartShipEncounter not found");
            return;
        }
        string error = "";
        try
        {
            ParameterInfo[] ps = start.GetParameters();
            var args = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                    : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
            start.Invoke(comp, args);
        }
        catch (Exception e)
        {
            error = (e.InnerException ?? e).Message;
        }
        string state = Field(comp, "ShipMapState")?.ToString() ?? "none";
        ReportTurret("before-combat");
        object? enemy = Field(comp, "ShipCombatTargetMap");
        Check("sos2-space-encounter-starts", state == "inCombat",
            $"state={state}, enemyMap={(enemy ?? "none")}, error={error}");
        if (state != "inCombat") return;

        object? enemyComp = enemy == null ? null : Sos2Compatibility.ShipCompOf(enemy as Map);
        Check("sos2-space-encounter-has-an-enemy",
            enemyComp != null || enemy != null, $"enemy={(enemy ?? "none")}");

        // The shield is only armed once combat exists: CompTick recomputes shutDown from power and
        // breakdown every tick and a scenario never reaches that tick, so the state a working shield
        // would be in has to be set, and only after the nets exist.
        ArmShield();
        ReportTurret("armed");
        started = true;
    }

    private static void ArmShield()
    {
        if (shield == null || space == null) return;
        object? heat = (shield as ThingWithComps)?.AllComps
            .FirstOrDefault(c => c.GetType().Name == "CompShipHeatShield");
        if (heat == null)
        {
            Check("sos2-space-shield-has-a-heat-comp", false, "no CompShipHeatShield");
            return;
        }
        if (shield.TryGetComp<CompFlickable>() is { } flick) flick.SwitchIsOn = true;
        if (turret?.TryGetComp<CompFlickable>() is { } turretFlick) turretFlick.SwitchIsOn = true;
        space.powerNetManager.UpdatePowerNetsAndConnections_First();
        // The conduit spine already reaches between the decks, so the power net should follow the same
        // path: a turret on the upper deck has to see the lower deck's reactor.
        if (turret != null)
        {
            var cells = turret.OccupiedRect().Cells.ToList();
            string detail = string.Join(", ", cells.Take(4).Select(c =>
                $"{c}: claims={Sos2Compatibility.MapClaims(space!, c)}, openAir={StructuralSupport.IsOpenAir(space.terrainGrid.TerrainAt(c))}, " +
                $"decode={hull!.TryDecode(c, out LayerCell lc) && puts(lc)}"));
            var row = new List<string>();
            for (int x = Pad.minX; x <= Pad.maxX; x++)
            {
                IntVec3 c = hull.AtElevation(1, x, Pad.minZ + 1);
                row.Add($"{x}:{(Sos2Compatibility.MapClaims(space!, c) ? "y" : "n")}");
            }
            Check("sos2-space-band1-row", false, string.Join(" ", row));
            var tops = new List<string>();
            for (int x = 60; x <= 63; x++)
            {
                IntVec3 c = hull.AtElevation(1, x, Pad.minZ + 1);
                tops.Add($"{x}:{space!.terrainGrid.TerrainAt(c)?.defName}/{space.terrainGrid.FoundationAt(c)?.defName}");
            }
            Check("sos2-space-band1-terrain", false, string.Join(" ", tops));
            IntVec3 at61 = hull.AtElevation(1, 61, Pad.minZ + 1);
            Check("sos2-space-turret-cell-support", false,
                $"pad={Pad}, shipCells={Sos2Compatibility.ShipCellsOf(space!)?.Count ?? -1}, " +
                $"L0={L0Count()}, " + detail);
        }
        ReportPower();
        object? shipComp = Sos2Compatibility.ShipCompOf(space);
        AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "heatGridDirty")?.SetValue(shipComp, true);
        AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "breathableZoneDirty")?.SetValue(shipComp, true);
        AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "loaded")?.SetValue(shipComp, false);
        string early = $"dirty={AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "heatGridDirty")?.GetValue(shipComp)}" +
            $", loadedField={AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "loaded")?.GetValue(shipComp)}" +
            $", tick={Find.TickManager.TicksGame}";
        try
        {
            AccessTools.Method(Sos2Compatibility.ShipMapCompType!, "MapComponentUpdate", Type.EmptyTypes)?.Invoke(shipComp, null);
            early += ", update=ok";
        }
        catch (Exception e)
        {
            early += ", update=" + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message;
        }
        int nets = (AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "cachedNets")?.GetValue(shipComp) as ICollection)?.Count ?? -1;
        Check("sos2-space-heat-net-rebuild-runs", nets > 0, $"{early}, nets={nets}");
        AccessTools.Field(heat.GetType(), "shutDown")?.SetValue(heat, false);
        AccessTools.Field(heat.GetType(), "radius")?.SetValue(heat, 6f);
        AccessTools.Field(heat.GetType(), "radiusSet")?.SetValue(heat, 6f);

        object? comp0 = Sos2Compatibility.ShipCompOf(space);
        int gridLen = (AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "grid")?.GetValue(comp0) as Array)?.Length ?? -1;
        int pipes = (AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "cachedPipes")?.GetValue(comp0) as ICollection)?.Count ?? -1;
        object? net = AccessTools.Field(heat.GetType(), "myNet")?.GetValue(heat);
        Object? turretNet = null;
        if (turret != null)
        {
            object? turretHeat = (turret as ThingWithComps)?.AllComps
                .FirstOrDefault(c => c.GetType().Name.StartsWith("CompShipHeat"));
            turretNet = turretHeat == null ? null : AccessTools.Field(turretHeat.GetType(), "myNet")?.GetValue(turretHeat);
        }
        Check("sos2-space-turret-shares-the-shields-net", net != null && ReferenceEquals(net, turretNet),
            $"shieldNet={(net == null ? "none" : AccessTools.Property(net.GetType(), "GridID")?.GetValue(net)?.ToString() ?? "?")}, " +
            $"turretNet={(turretNet == null ? "none" : AccessTools.Property(turretNet.GetType(), "GridID")?.GetValue(turretNet)?.ToString() ?? "?")}");
        Check("sos2-space-heat-grid-matches-the-map", gridLen == space.cellIndices.NumGridCells,
            $"grid={gridLen}, cells={space.cellIndices.NumGridCells}, pipes={pipes}");
        Check("sos2-space-shield-is-on-a-heat-net", net != null,
            $"shield={shield.Position}, capacity={(net == null ? -1f : AccessTools.Property(net.GetType(), "StorageCapacityRaw")?.GetValue(net))}");
        Check("sos2-space-shield-is-powered",
            shield.TryGetComp<CompPower>()?.PowerNet != null,
            $"power={shield.TryGetComp<CompPower>()?.PowerNet != null}");
    }

    /// <summary>
    /// Ticks SOS2's own combat until a shield catches something, or the budget runs out.
    /// </summary>
    /// <remarks>
    /// Nothing here spawns a projectile or calls an intercept test. In combat, <c>Building_ShipTurret</c>
    /// registers a <c>ShipCombatProjectile</c> and <c>ShipMapComp</c>'s own loop picks the edge cell,
    /// spawns the shot on the target map and launches it - the three steps a real battle takes, using
    /// whatever edge cell SOS2 chose on a layered map.
    /// </remarks>
    private static void WatchCombat()
    {
        if (!started || space == null)
        {
            Check("sos2-space-shield-stops-incoming-fire", false, "combat never started");
            return;
        }
        object? comp = Sos2Compatibility.ShipCompOf(space);
        MethodInfo? update = AccessTools.Method(Sos2Compatibility.ShipMapCompType!, "MapComponentUpdate", Type.EmptyTypes);
        int before = Sos2ShieldTrace.Hits;
        int towers = 0;
        for (int tick = 0; tick < 900 && Sos2ShieldTrace.Hits == before; tick++)
        {
            update?.Invoke(comp, null);
            towers = Sos2ShieldTrace.Shots;
        }
        Check("sos2-space-shield-stops-incoming-fire", Sos2ShieldTrace.Hits > before,
            $"hits={Sos2ShieldTrace.Hits - before}, shotsSeen={towers}, last={Sos2ShieldTrace.LastShot}");
    }

    /// <summary>Why the turret will or will not fire, read from its own properties.</summary>
    private static void ReportTurret(string when)
    {
        if (turret == null) return;
        object? heat = (turret as ThingWithComps)?.AllComps
            .FirstOrDefault(c => c.GetType().Name.StartsWith("CompShipHeat"));
        object? net = heat == null ? null : AccessTools.Field(heat.GetType(), "myNet")?.GetValue(heat);
        object? nav = AccessTools.Property(turret.GetType(), "Active")?.GetValue(turret);
        object? linked = AccessTools.Property(turret.GetType(), "ConnectedToBridge")?.GetValue(turret);
        object? power = turret.TryGetComp<CompPower>();
        object? origin = Field(Sos2Compatibility.ShipCompOf(space), "originMapComp");
        object? range = origin == null ? null : AccessTools.Field(origin.GetType(), "Range")?.GetValue(origin);
        object? holdFire = AccessTools.Field(turret.GetType(), "holdFire")?.GetValue(turret);
        object? heatProps = AccessTools.Field(heat?.GetType(), "Props")?.GetValue(heat);
        object? maxRange = heatProps == null ? null : AccessTools.Field(heatProps.GetType(), "maxRange")?.GetValue(heatProps);
        Check($"sos2-space-turret-state-{when}", false,
            $"active={nav}, onBridge={linked}, net={(net == null ? "none" : "ok")}, powerOn={turret.TryGetComp<CompPowerTrader>()?.PowerOn}, " +
            $"holdFire={holdFire}, range={range}, maxRange={maxRange}, " +
            $"nets={(AccessTools.Field(Sos2Compatibility.ShipMapCompType!, "cachedNets")?.GetValue(Sos2Compatibility.ShipCompOf(space)) as ICollection)?.Count ?? -1}");
    }

    private static bool puts(LayerCell cell) => cell.LayerId >= 0;

    private static int L0Count()
    {
        if (space == null || hull == null) return -1;
        int n = 0;
        foreach (IntVec3 c in hull.LayerRect(0))
            if (Sos2Compatibility.MapClaims(space!, c)) n++;
        return n;
    }

    /// <summary>Which power net each part landed on, so a dead turret is attributable.</summary>
    private static void ReportPower()
    {
        if (space == null || turret == null) return;
        var parts = new[] { ("shield", shield), ("turret", turret), ("reactor", reactor), ("engine", engine) };
        var lines = parts.Select(p =>
        {
            CompPowerTrader? trader = p.Item2?.TryGetComp<CompPowerTrader>();
            CompPower? power = p.Item2?.TryGetComp<CompPower>();
            string net = power?.PowerNet == null ? "none" : "net";
            string transmits = p.Item2?.TryGetComp<CompPower>()?.TransmitsPowerNow.ToString() ?? "-";
            string surface = p.Item2 == null ? "-" : Environment.PowerLinks.HasSurface(p.Item2).ToString();
            string layer = p.Item2 == null ? "-" : (hull != null && hull.TryDecode(p.Item2.Position, out LayerCell lc) ? lc.LayerId.ToString() : "?");
            return $"{p.Item1}@{p.Item2?.Position.ToString() ?? "-"} L{layer}: {net} on={trader?.PowerOn} connected={power?.PowerNet != null} transmits={transmits} surface={surface}";
        });
        Check("sos2-space-power-per-part", false, string.Join(" | ", lines));
    }

    private static object? Field(object? instance, string name)
    {
        if (instance == null) return null;
        return AccessTools.Field(instance.GetType(), name)?.GetValue(instance)
            ?? AccessTools.Field(Sos2Compatibility.ShipMapCompType!, name)?.GetValue(instance);
    }

    private static object? Sos2Ship(int index)
    {
        object? component = Sos2Compatibility.ShipCompOf(space);
        if (component == null) return null;
        var ships = AccessTools.PropertyGetter(Sos2Compatibility.ShipMapCompType, "ShipsOnMap")?.Invoke(component, null) as IDictionary;
        return ships != null && ships.Contains(index) ? ships[index] : null;
    }

    private static int ShipCount()
    {
        object? component = Sos2Compatibility.ShipCompOf(space);
        if (component == null) return 0;
        return (AccessTools.PropertyGetter(Sos2Compatibility.ShipMapCompType, "ShipsOnMap")?.Invoke(component, null) as IDictionary)?.Count ?? 0;
    }
}
