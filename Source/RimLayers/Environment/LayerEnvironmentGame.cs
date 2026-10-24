using Verse;

namespace RimLayers.Environment;

/// <summary>One activation flag per save. Defaults differ intentionally for a new game and an absent old-save node.</summary>
public sealed class LayerEnvironmentGame : GameComponent
{
    public bool Enabled;
    private readonly Game owner;
    public LayerEnvironmentGame(Game game) => owner = game;
    public override void StartedNewGame(){Enabled=true;}
    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref Enabled,"rimLayersEnvironmentEnabled",false,forceSave:true);
        if(Scribe.mode==LoadSaveMode.PostLoadInit)Invalidate();
    }
    // Game.GetComponent<T>() scans Game.components linearly. This flag is read from per-cell and
    // per-plant/pawn stat paths (HeatLinks.Conductance, Concentration, LocalEcology, LocalWeather),
    // so the scan must not repeat. The component instance is stable for a game; only the flag moves.
    private static LayerEnvironmentGame? cached;
    public static void Invalidate() => cached = null;
    public static bool Active
    {
        get
        {
            Game? game = Current.Game;
            if (game == null) return false;
            if (cached == null || !ReferenceEquals(cached.owner, game))
                cached = game.GetComponent<LayerEnvironmentGame>();
            // Re-read the field every call: the settings menu mutates it directly and the component
            // is the only owner of that state, so caching the value would go stale.
            return cached != null && cached.Enabled;
        }
    }
}
