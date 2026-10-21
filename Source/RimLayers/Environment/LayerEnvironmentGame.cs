using Verse;

namespace RimLayers.Environment;

/// <summary>One activation flag per save. Defaults differ intentionally for a new game and an absent old-save node.</summary>
public sealed class LayerEnvironmentGame : GameComponent
{
    public bool Enabled;
    public LayerEnvironmentGame(Game game) {}
    public override void StartedNewGame(){Enabled=true;}
    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref Enabled,"rimLayersEnvironmentEnabled",false,forceSave:true);
    }
    public static bool Active=>Current.Game?.GetComponent<LayerEnvironmentGame>()?.Enabled==true;
}
