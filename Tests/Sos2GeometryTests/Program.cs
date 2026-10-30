using RimLayers.Compatibility;

// Mirrors the real storage geometry of a 120x120 map with three layers:
// stride = CalculateStride(120) = 136, storage length = 136 * 3 = 408.
const int Width = 120, Height = 120, Stride = 136, Layers = 3, StorageLength = Stride * Layers;

int checks = 0;
void Check(string name, bool ok)
{
    if (!ok) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

// --- band decoding -------------------------------------------------------------------------
Check("layer of first row", Sos2Geometry.LayerOf(0, Stride, Layers) == 0);
Check("layer of last row in band 0", Sos2Geometry.LayerOf(135, Stride, Layers) == 0);
Check("layer of first row in band 1", Sos2Geometry.LayerOf(136, Stride, Layers) == 1);
Check("layer of band 2", Sos2Geometry.LayerOf(272, Stride, Layers) == 2);
Check("layer of padding past the storage", Sos2Geometry.LayerOf(StorageLength, Stride, Layers) == -1);
Check("local z inside band 0", Sos2Geometry.LocalZ(10, Stride) == 10);
Check("local z inside band 1", Sos2Geometry.LocalZ(146, Stride) == 10);
Check("logical length is the layout height, not the storage", Sos2Geometry.LogicalLength(StorageLength, Height, Stride, Layers) == Height);
Check("unlayered map keeps its own size", Sos2Geometry.LogicalLength(120, 120, 136, 1) == 120);
Check("single-band map keeps its own size even at one layer", Sos2Geometry.LogicalLength(90, 120, 136, 1) == 90);
Check("last storage cell is in bounds", Sos2Geometry.InStorage(119, 407, Width, Stride, Layers));
Check("one past the width is out of bounds", !Sos2Geometry.InStorage(120, 0, Width, Stride, Layers));
Check("one past the storage is out of bounds", !Sos2Geometry.InStorage(0, 408, Width, Stride, Layers));

// --- adjacency -----------------------------------------------------------------------------
var mid = Sos2Geometry.Neighbors(5, 0, 10, Width, Height, Stride, Layers).ToArray();
Check("ground cell has four same-layer neighbours", mid.Count(n => n.Z is 9 or 11 || n.X is 4 or 6) == 4);
Check("ground cell reaches the deck above", mid.Contains((5, 0, 146)));
Check("ground cell has no neighbour below", !mid.Any(n => n.Z < 0));
Check("ground cell does not reach two bands up", !mid.Contains((5, 0, 282)));

var corner = Sos2Geometry.Neighbors(0, 0, 0, Width, Height, Stride, Layers).ToArray();
Check("map corner has three neighbours", corner.Length == 3);
Check("map corner never wraps west", !corner.Any(n => n.X < 0));

var topRow = Sos2Geometry.Neighbors(5, 0, 119, Width, Height, Stride, Layers).ToArray();
Check("last real row does not step into stride padding", !topRow.Contains((5, 0, 120)));
Check("last real row still reaches the deck above", topRow.Contains((5, 0, 136 + 119)));

var deck = Sos2Geometry.Neighbors(5, 0, 136, Width, Height, Stride, Layers).ToArray();
Check("middle deck reaches the deck below", deck.Contains((5, 0, 0)));
Check("middle deck reaches the deck above", deck.Contains((5, 0, 272)));

var top = Sos2Geometry.Neighbors(5, 0, 272, Width, Height, Stride, Layers).ToArray();
Check("top deck reaches the deck below", top.Contains((5, 0, 136)));
Check("top deck has no neighbour in the next band", !top.Any(n => n.Z >= 272 + Stride));

Check("padding band yields no neighbours", !Sos2Geometry.Neighbors(5, 0, 408, Width, Height, Stride, Layers).Any());

// --- rotation ------------------------------------------------------------------------------
Check("mirror keeps the band", Sos2Geometry.Mirror(10, 0, 5, Width, Height, Stride, Layers, 2) == (110, 0, 115));
Check("mirror of a deck cell stays on that deck",
    Sos2Geometry.Mirror(10, 0, 141, Width, Height, Stride, Layers, 2) == (110, 0, 251));
Check("quarter turn keeps the band", Sos2Geometry.Mirror(10, 0, 5, Width, Height, Stride, Layers, 3) == (115, 0, 10));
Check("identity rotation is a no-op", Sos2Geometry.Mirror(10, 0, 141, Width, Height, Stride, Layers, 1) == (10, 0, 141));

// --- centring ------------------------------------------------------------------------------
var centred = Sos2Geometry.CenterOnMap(120, StorageLength, Height, Stride, Layers, 20, 0, new[] { 0, 20 });
Check("centring uses the logical length", centred == (50, 0, 50));
Check("centring does not land in the storage middle", centred.Z != 194);

// The real hull from the failed launch: 250x250 layout, stride 255, 2 bands, storage 510.
// Ground cells sit at z=90, the upper deck at z=255+90=345. Measuring the storage span (255) as if
// it were the hull's height gave z = 125 - 127 - 90 = -92, so every transferred cell landed on z=-1
// and Area_Home threw IndexOutOfRangeException.
const int RealStride = 255, RealLayers = 2, RealHeight = 250, RealStorage = 510;
var real = Sos2Geometry.CenterOnMap(250, RealStorage, RealHeight, RealStride, RealLayers, 20, 0,
    new[] { 90, RealStride + 90 });
Check("centring a two-band hull does not go negative", real.Z == 35);
Check("centring ignores which band a cell lives in",
    Sos2Geometry.CenterOnMap(250, RealStorage, RealHeight, RealStride, RealLayers, 20, 0, new[] { 90, RealStride + 90 })
    == Sos2Geometry.CenterOnMap(250, RealStorage, RealHeight, RealStride, RealLayers, 20, 0, new[] { 90, 90 }));
Check("a two-band hull lands inside the destination",
    new[] { 90, RealStride + 90 }.All(z => real.Z + z >= 0 && real.Z + z < RealStorage));

// --- turret edge ---------------------------------------------------------------------------
Check("north edge of a ground turret", Sos2Geometry.EdgeCell(50, 0, 10, Width, Height, Stride, Layers, 0, 0) == (50, 0, 119));
Check("north edge stays on the turret's own deck",
    Sos2Geometry.EdgeCell(50, 0, 272 + 10, Width, Height, Stride, Layers, 0, 0) == (50, 0, 272 + 119));
Check("north edge never lands on the storage top",
    Sos2Geometry.EdgeCell(50, 0, 10, Width, Height, Stride, Layers, 0, 0).Z != 407);
Check("south edge is row zero", Sos2Geometry.EdgeCell(50, 0, 272 + 10, Width, Height, Stride, Layers, 0, 2) == (50, 0, 272));
Check("east edge is the last column", Sos2Geometry.EdgeCell(50, 0, 10, Width, Height, Stride, Layers, 0, 1) == (119, 0, 10));
Check("west edge is column zero", Sos2Geometry.EdgeCell(50, 0, 10, Width, Height, Stride, Layers, 0, 3) == (0, 0, 10));
Check("edge miss is clamped inside the map",
    Sos2Geometry.EdgeCell(118, 0, 10, Width, Height, Stride, Layers, 5, 0) == (119, 0, 119));
Check("negative miss is clamped too",
    Sos2Geometry.EdgeCell(2, 0, 10, Width, Height, Stride, Layers, -5, 0) == (0, 0, 119));
Check("edge cell of a padding turret is untouched",
    Sos2Geometry.EdgeCell(50, 0, 408, Width, Height, Stride, Layers, 0, 0) == (50, 0, 408));

Console.WriteLine($"{checks} sos2 geometry checks passed.");

// --- deck identity -------------------------------------------------------------------------
Check("two cells in band 0 share a deck", Sos2Geometry.SameLayer(0, 119, Stride, Layers));
Check("a ground cell and a deck cell do not", !Sos2Geometry.SameLayer(10, 146, Stride, Layers));
Check("two cells in band 2 share a deck", Sos2Geometry.SameLayer(272, 407, Stride, Layers));
Check("an unmanaged map has one deck", Sos2Geometry.SameLayer(0, 999, 136, 1));
Check("a padding cell never vetoes", Sos2Geometry.SameLayer(10, 408, Stride, Layers));

// --- logical size --------------------------------------------------------------------------
Check("logical size drops the bands",
    Sos2Geometry.LogicalSize(120, StorageLength, Height, Stride, Layers) == (120, 0, 120));
Check("logical size of an unlayered map is unchanged",
    Sos2Geometry.LogicalSize(120, 120, 120, 136, 1) == (120, 0, 120));

// --- projectile side -----------------------------------------------------------------------
Check("advancing ship fires from its engine edge",
    Sos2Geometry.ProjectileSide(50, 10, Width, Height, Stride, Layers, 1, 0) == 0);
Check("retreating ship fires from the opposite edge",
    Sos2Geometry.ProjectileSide(50, 10, Width, Height, Stride, Layers, -1, 0) == 2);
Check("retreating wrap-around stays a valid side",
    Sos2Geometry.ProjectileSide(50, 10, Width, Height, Stride, Layers, -1, 3) == 1);
// The side is the edge the projectile spawns at, which is the side the target sits on (native
// behaviour, kept verbatim: only the band bounds change).
Check("a northern target draws fire from the north edge",
    Sos2Geometry.ProjectileSide(60, 272 + 110, Width, Height, Stride, Layers, 0, 0) == 0);
Check("a southern target draws fire from the south edge",
    Sos2Geometry.ProjectileSide(60, 10, Width, Height, Stride, Layers, 0, 0) == 2);
Check("a western target draws fire from the west edge",
    Sos2Geometry.ProjectileSide(10, 60, Width, Height, Stride, Layers, 0, 0) == 3);
Check("an eastern target draws fire from the east edge",
    Sos2Geometry.ProjectileSide(110, 60, Width, Height, Stride, Layers, 0, 0) == 1);
// The deck a shot is aimed at must not change which edge it comes from: a ship on band 2 with the
// same local position fires from the same side as one on band 0.
Check("the deck does not change the firing edge",
    Sos2Geometry.ProjectileSide(60, 272 + 10, Width, Height, Stride, Layers, 0, 0) ==
    Sos2Geometry.ProjectileSide(60, 10, Width, Height, Stride, Layers, 0, 0));

// --- a multi-deck ship must fit a multi-deck destination --------------------------------
// MoveShip mirrors every source cell onto the target and then writes it into MapShipCells /
// Home without a bounds guard. A flat destination therefore throws mid-launch, which is what
// a space map generated at the logical size used to do. Every deck must survive the transform.
const int ShipX = 40, ShipZ = 30;
foreach (int rot in new[] { 1, 2, 3 })
    for (int layer = 0; layer < Layers; layer++)
        Check($"rot {rot} keeps deck {layer} inside the destination", Inside(Sos2Geometry.Mirror(
            ShipX, 0, layer * Stride + ShipZ, Width, Height, Stride, Layers, rot)));

// The failure this guards against, stated directly: a single-layer destination has no room for
// a second deck, so the move would address a z past the end of the map.
Check("a flat destination cannot store a second deck",
    Sos2Geometry.LayerOf(Stride + ShipZ, Stride, 1) < 0);
Check("a multi-deck destination can", Sos2Geometry.LayerOf(Stride + ShipZ, Stride, Layers) == 1);

bool Inside((int X, int Y, int Z) cell) =>
    cell.X is >= 0 and < Width &&
    Sos2Geometry.LayerOf(cell.Z, Stride, Layers) is >= 0 and < Layers &&
    Sos2Geometry.LocalZ(cell.Z, Stride) < Height;

Console.WriteLine($"{checks} sos2 geometry checks passed (subsystems).");
