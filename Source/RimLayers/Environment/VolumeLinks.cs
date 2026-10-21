using RimLayers.Core;
using Verse;

namespace RimLayers.Environment;

internal static class VolumeLinks
{
    internal static bool Open(LayerMapComponent owner,IntVec3 lower,IntVec3 upper,LayerChangeKind medium)
    {
        if(!owner.TryDecode(lower,out var a) || !owner.TryDecode(upper,out var b) ||
            a.X!=b.X || a.Z!=b.Z || owner.Elevation(b.LayerId)!=owner.Elevation(a.LayerId)+1 ||
            !owner.map.gasGrid.GasCanMoveTo(lower) || !owner.map.gasGrid.GasCanMoveTo(upper))return false;
        return owner.Connectors.HasTransferEdge(lower,upper,medium) ||
            !owner.Structure.HasSurface(upper) && owner.map.roofGrid.RoofAt(lower)==null;
    }
}
