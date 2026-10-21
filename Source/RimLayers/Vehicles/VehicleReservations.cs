namespace RimLayers.Vehicles;

/// <summary>Atomic, FIFO claims for whole corridors. Call only from the simulation thread.</summary>
public sealed class VehicleReservations
{
    private sealed record Request(int Owner,long Ticket,HashSet<string> Claims,bool Active);
    private readonly Dictionary<int,Request> requests=new();
    public void Clear()=>requests.Clear();
    public void Release(int owner)=>requests.Remove(owner);
    public bool ClaimedByOther(int owner,string claim)=>requests.Values.Any(r=>r.Owner!=owner&&r.Active&&r.Claims.Contains(claim));
    public bool IsActive(int owner)=>requests.TryGetValue(owner,out var request)&&request.Active;
    public void Enqueue(int owner,long ticket,IEnumerable<string> claims)
    {
        if(ticket<1)throw new ArgumentOutOfRangeException(nameof(ticket));
        var set=new HashSet<string>(claims,StringComparer.Ordinal);
        if(set.Count==0||set.Any(string.IsNullOrEmpty))throw new ArgumentException("A reservation needs nonempty claims.",nameof(claims));
        if(requests.TryGetValue(owner,out var old)&&old.Active)
        {
            if(old.Ticket!=ticket||!old.Claims.SetEquals(set))throw new InvalidOperationException("Release an active reservation before changing its corridor.");
            return;
        }
        requests[owner]=new(owner,ticket,set,false);
    }
    public bool TryAcquire(int owner)
    {
        if(!requests.TryGetValue(owner,out var request))return false;
        if(request.Active)return true;
        foreach(var other in requests.Values)
        {
            if(other.Owner==owner||!request.Claims.Overlaps(other.Claims))continue;
            if(other.Active||Earlier(other,request))return false;
        }
        requests[owner]=request with{Active=true};return true;
    }
    /// <summary>Restore already occupied corridors before adding waiting requests. Never evict their owners.</summary>
    public bool RestoreActive(int owner,long ticket,IEnumerable<string> claims)
    {
        Enqueue(owner,ticket,claims);
        var request=requests[owner];
        if(requests.Values.Any(other=>other.Owner!=owner&&other.Active&&request.Claims.Overlaps(other.Claims)))return false;
        requests[owner]=request with{Active=true};return true;
    }
    private static bool Earlier(Request a,Request b)=>a.Ticket<b.Ticket||a.Ticket==b.Ticket&&a.Owner<b.Owner;
}
