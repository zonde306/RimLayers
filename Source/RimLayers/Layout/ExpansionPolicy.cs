namespace RimLayers.Layout;

public enum ExpansionDenial { None, HardCapacity, UpperLimit, UndergroundLimit, Space, GlobalLimit }
public readonly record struct ExpansionDecision(int Additional,ExpansionDenial Denial)
{
    public bool Allowed=>Denial==ExpansionDenial.None;
}

/// <summary>Checks the whole requested increment without allocating. Existing floors always pass.</summary>
public static class ExpansionPolicy
{
    public static int Additional(int low,int high,int requested)=>Math.Max(0,requested-high)+Math.Max(0,low-requested);
    public static ExpansionDecision Check(int low,int high,int requested,int upperLimit,int undergroundLimit,
        bool space,bool allowSpace,int generated,int reserved,int globalLimit,int committedTop=-1,int coveredReservation=0)
    {
        int additional=Additional(low,high,requested);
        if(additional==0)return new(0,ExpansionDenial.None);
        if(requested<LayerLayout.MinElevation||requested>LayerLayout.MaxElevation)return new(additional,ExpansionDenial.HardCapacity);
        // A transport's existing promise survives later changes to limits. Extra height does not.
        if(requested>=0&&committedTop>=requested)return new(additional,ExpansionDenial.None);
        if(requested>0&&requested>upperLimit)return new(additional,ExpansionDenial.UpperLimit);
        if(requested<0&&-requested>undergroundLimit)return new(additional,ExpansionDenial.UndergroundLimit);
        if(space&&(!allowSpace||requested<0))return new(additional,ExpansionDenial.Space);
        if(generated+reserved+additional-Math.Min(additional,coveredReservation)>globalLimit)return new(additional,ExpansionDenial.GlobalLimit);
        return new(additional,ExpansionDenial.None);
    }
}
