using RimLayers.Structure;

int checks = 0;
void Assert(bool condition, string message)
{
    checks++;
    if (!condition) throw new Exception(message);
}

// Bands of 17 rows per band (a 1-row map is padded to a stride of 17) are the common shape; use a
// 16-row band so the arithmetic is obvious.
const int band = 17;
foreach (int fromElevation in new[] { 0, 1, 2, -1, -2 })
    foreach (int direction in new[] { 1, -1 })
    {
        int fromLayer = fromElevation + 9;              // band index, as LayerMapComponent does it
        int toLayer = fromLayer + direction;
        int fromBand = fromLayer * band;
        int toBand = toLayer * band;
        for (int localZ = 0; localZ < 16; localZ += 5)
        {
            int storageZ = fromBand + localZ;
            int moved = LayerPairMath.ShiftZ(storageZ, fromBand, toBand);
            Assert(moved == toBand + localZ, "Row was not re-based onto the adjacent band.");
            // The tempting bug is `storageZ + toBand`, which counts the target band twice.
            if (toBand != 0) Assert(moved != storageZ + toBand, "Shift double-counted the target band");
            Assert(moved >= toBand && moved < toBand + 16, "Shifted row left its band.");
        }
    }

// Height shifting onto a layer that is two bands away, when only one band exists between them.
{
    int fromBand = 0, toBand = 2 * band;
    Assert(LayerPairMath.ShiftZ(fromBand + 4, fromBand, toBand) == toBand + 4, "Multi-band shift failed.");
}

// A row that is already the last of its band must not spill into the isolation gap.
{
    int fromBand = band, toBand = 2 * band;
    int moved = LayerPairMath.ShiftZ(fromBand + 16, fromBand, toBand);
    Assert(moved == toBand + 16 && moved == 3 * band - 1, "Last row of a band spilled.");
}

Assert(LayerPairMath.IsVertical(1) && LayerPairMath.IsVertical(-1), "Vertical directions rejected.");
Assert(!LayerPairMath.IsVertical(0) && !LayerPairMath.IsVertical(2), "Non-vertical direction accepted.");

// Rotation: a square footprint never rotates; a rectangle keeps its long side on the same axis.
Assert(LayerPairMath.LongSideHorizontal(1, 1), "Square footprint must not rotate.");
Assert(LayerPairMath.LongSideHorizontal(2, 1), "Wide footprint must stay wide.");
Assert(LayerPairMath.LongSideHorizontal(1, 2) == false, "Tall footprint must stay tall.");

Console.WriteLine($"PairingTests: {checks} assertions passed.");
