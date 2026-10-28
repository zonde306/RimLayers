using RimLayers.Areas;
using RimLayers.Layout;

int checks = 0;
void Assert(bool condition, string message)
{
    checks++;
    if (!condition) throw new Exception(message);
}

// ---------------------------------------------------------------------------
// Designator gate: every painted area/zone cell must be on the viewed layer and
// must carry a real floor.
// ---------------------------------------------------------------------------
for (int viewed = 0; viewed < 3; viewed++)
    for (int layer = 0; layer < 3; layer++)
        for (int surface = 0; surface < 2; surface++)
            for (int decoded = 0; decoded < 2; decoded++)
            {
                var gate = AreaLayerRules.Evaluate(decoded != 0, layer, viewed, surface != 0);
                var expected = decoded == 0 || layer != viewed
                    ? AreaLayerRules.Gate.OffLayer
                    : surface == 0 ? AreaLayerRules.Gate.NoSurface : AreaLayerRules.Gate.Allow;
                Assert(gate == expected,
                    $"Gate mismatch: decoded={decoded} layer={layer} viewed={viewed} surface={surface} -> {gate}");
            }

// The isolation band is never decodable, so an undecoded cell must be refused
// before the layer comparison is reached.
Assert(AreaLayerRules.Evaluate(false, 0, 0, true) == AreaLayerRules.Gate.OffLayer,
    "Undecoded cell accepted on the viewed ground layer.");
Assert(AreaLayerRules.Evaluate(false, 9, 0, false) == AreaLayerRules.Gate.OffLayer,
    "Undecoded cell accepted with a bogus layer id.");

// Open air on the viewed layer is refused, but for a different reason than
// "off layer" -- the two messages differ for the player.
Assert(AreaLayerRules.Evaluate(true, 1, 1, false) == AreaLayerRules.Gate.NoSurface,
    "Open air accepted on the viewed layer.");
Assert(AreaLayerRules.Evaluate(true, 1, 1, false) != AreaLayerRules.Evaluate(true, 1, 0, false),
    "Off-layer and no-floor must not share a gate value.");

// ---------------------------------------------------------------------------
// Automatic spread (home area around buildings/zone cells, automatic roof):
// stay on the anchor layer, never leave the floor.
// ---------------------------------------------------------------------------
for (int anchor = 0; anchor < 3; anchor++)
    for (int layer = 0; layer < 3; layer++)
        for (int surface = 0; surface < 2; surface++)
        {
            bool accepted = AreaLayerRules.AcceptsSpread(layer, anchor, surface != 0);
            Assert(accepted == (surface != 0 && layer == anchor),
                $"Spread mismatch: layer={layer} anchor={anchor} surface={surface}");
        }

Assert(!AreaLayerRules.AcceptsSpread(2, 1, true), "Spread crossed into a neighbouring layer.");
Assert(!AreaLayerRules.AcceptsSpread(1, 1, false), "Spread seeded open air on the anchor layer.");
Assert(AreaLayerRules.AcceptsSpread(1, 1, true), "Spread refused the anchor layer's own floor.");

// ---------------------------------------------------------------------------
// Inversion: only floor cells participate, and a second invert restores.
// ---------------------------------------------------------------------------
Assert(AreaLayerRules.InvertValue(false, true), "Floor cell was not switched on by invert.");
Assert(!AreaLayerRules.InvertValue(true, true), "Floor cell was not switched off by invert.");
Assert(!AreaLayerRules.InvertValue(false, false), "Non-floor cell was switched on by invert.");
Assert(!AreaLayerRules.InvertValue(true, false), "Non-floor cell was not cleared by invert.");
Assert(!AreaLayerRules.InvertValue(AreaLayerRules.InvertValue(false, true), true),
    "Double invert did not restore a floor cell.");
Assert(!AreaLayerRules.InvertValue(AreaLayerRules.InvertValue(false, false), false),
    "Double invert introduced a non-floor cell.");

// ---------------------------------------------------------------------------
// Full-storage sweep: simulate the real grid (every layer plus its isolation
// rows) and confirm the gates accept exactly the viewed layer's floor cells.
// ---------------------------------------------------------------------------
foreach (var (width, height) in new[] { (40, 30), (120, 120), (17, 17), (75, 44) })
{
    int stride = LayerLayout.CalculateStride(height);
    var layout = new LayerLayout(width, height, 3, stride);
    // Layer 1 has open air on every third column so the surface test matters.
    bool Surface(int layer, int x) => layer != 1 || x % 3 != 0;

    for (int viewed = 0; viewed < 3; viewed++)
    {
        int gateAccepted = 0, spreadAccepted = 0, invertAccepted = 0;
        int expected = 0;
        for (int z = 0; z < layout.StorageHeight; z++)
            for (int x = 0; x < layout.Width; x++)
            {
                var storage = new StorageCell(x, 0, z);
                bool decoded = layout.TryDecode(storage, out var logical);
                if (decoded && logical.LayerId == viewed && Surface(logical.LayerId, logical.X))
                    expected++;
                bool surface = decoded && Surface(logical.LayerId, logical.X);
                if (AreaLayerRules.Evaluate(decoded, decoded ? logical.LayerId : -1, viewed, surface)
                    == AreaLayerRules.Gate.Allow) gateAccepted++;
                if (AreaLayerRules.AcceptsSpread(decoded ? logical.LayerId : -1, viewed, surface))
                    spreadAccepted++;
                // The invert gate only ever walks the viewed layer's own rect, so a floor cell
                // on any other layer is outside its reach even though the rule itself is
                // layer-agnostic.
                if (AreaLayerRules.InvertValue(false, surface) && decoded && logical.LayerId == viewed)
                    invertAccepted++;
            }
        Assert(gateAccepted == expected,
            $"Gate accepted {gateAccepted} cells, expected {expected} on layer {viewed} ({width}x{height}).");
        Assert(spreadAccepted == expected,
            $"Spread accepted {spreadAccepted} cells, expected {expected} on layer {viewed} ({width}x{height}).");
        Assert(invertAccepted == expected,
            $"Invert touched {invertAccepted} cells, expected {expected} on layer {viewed} ({width}x{height}).");
        // Nothing outside the viewed layer may ever be accepted.
        Assert(gateAccepted <= width * height, "Gate accepted more cells than one layer holds.");
    }

    // Regression: the vanilla home-area blob is a fixed 9x9 rect indexed in flat
    // storage space, so a building sitting on the last row of a layer band used to
    // seed home area past the band's end. Use a radius that is guaranteed to reach
    // both the isolation rows and the next layer's first rows.
    int anchorLayer = 1;
    int gap = stride - height;
    int radius = gap + 3;
    int anchorZ = anchorLayer * stride + height - 1;
    var anchor = new StorageCell(width / 2, 0, anchorZ);
    Assert(layout.TryDecode(anchor, out var anchorCell) && anchorCell.LayerId == anchorLayer,
        "Fixture anchor is not on the intended layer.");
    int offLayerReached = 0, gapReached = 0, seeded = 0;
    foreach (var offset in CellRectOffsets(radius))
    {
        var candidate = new StorageCell(anchor.X + offset.x, 0, anchor.Z + offset.z);
        if (candidate.X < 0 || candidate.X >= layout.Width) continue;
        if (candidate.Z < 0 || candidate.Z >= layout.StorageHeight) continue;
        bool decoded = layout.TryDecode(candidate, out var logical);
        if (!decoded) { gapReached++; continue; }
        if (logical.LayerId != anchorLayer) offLayerReached++;
        if (AreaLayerRules.AcceptsSpread(logical.LayerId, anchorLayer, true))
        {
            seeded++;
            Assert(logical.LayerId == anchorLayer, "Spread seeded a cell on another layer.");
        }
    }
    Assert(offLayerReached > 0,
        $"Fixture did not reach a neighbouring layer ({width}x{height}, gap {gap}); test is vacuous.");
    Assert(gapReached > 0,
        $"Fixture did not reach the isolation rows ({width}x{height}, gap {gap}); test is vacuous.");
    Assert(seeded > 0, "Spread seeded nothing around the anchor.");
}

static IEnumerable<(int x, int z)> CellRectOffsets(int radius)
{
    for (int dx = -radius; dx <= radius; dx++)
        for (int dz = -radius; dz <= radius; dz++)
            yield return (dx, dz);
}

Console.WriteLine($"PASS: {checks} area layer checks.");
