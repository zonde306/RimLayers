using RimLayers.Presentation;

int checks = 0;
void Check(bool yes, string name) { checks++; if (!yes) throw new Exception(name); }
void Near(float got, float want, string name) => Check(Math.Abs(got - want) < .00001f, $"{name}: got={got} want={want}");

const float W = 226f, Margin = PanelDragMath.Margin, Handle = PanelDragMath.HandleHeight;

// 1024x768 is the narrowest resolution the acceptance runner uses, and is where the native
// upper-right anchor (screenWidth-238) plus a leftward drag can actually collide with the edge.
foreach (var (sw, sh) in new[] { (1024f, 768f), (1280f, 720f), (1920f, 1080f), (3840f, 2160f) })
{
    float nativeX = sw - 238f, nativeY = 160f;
    // The native anchor must survive the clamp untouched, or every existing player sees the panel jump.
    var (x0, y0) = PanelDragMath.Clamp(nativeX, nativeY, W, 106f, sw, sh);
    Near(x0, nativeX, "native anchor x is unchanged");
    Near(y0, nativeY, "native anchor y is unchanged");
    Check(x0 >= Margin && x0 + W <= sw, "native anchor fits the screen");
    // Any offset a player can reach must stay fully on screen with the handle still grabbable.
    for (float dx = -4000; dx <= 4000; dx += 137)
        for (float dy = -4000; dy <= 4000; dy += 211)
        {
            var (x, y) = PanelDragMath.Clamp(nativeX + dx, nativeY + dy, W, 106f, sw, sh);
            Check(x >= Margin && x + W <= sw, $"x escapes at {sw}x{sh}: dx={dx} -> {x}");
            Check(y >= Margin + Handle && y + 106f <= sh, $"y escapes at {sw}x{sh}: dy={dy} -> {y}");
            Check(y - Handle >= Margin, $"handle leaves the screen: dy={dy} -> {y}");
        }
}
// A panel taller than the screen has no valid range; the near margin must win rather than throwing.
foreach (var (h, sh) in new[] { (200f, 180f), (400f, 200f), (106f, 106f) })
{
    float y = PanelDragMath.ClampAxis(0, h, sh, Handle);
    Check(float.IsFinite(y) && y >= Margin + Handle, $"oversized panel keeps its handle visible: {y}");
}
// Non-finite input must not propagate into Rect (Unity throws on NaN geometry).
foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
{
    Near(PanelDragMath.ClampAxis(bad, W, 1024f), Margin, "non-finite x falls back to the near margin");
    Near(PanelDragMath.ClampAxis(bad, W, 1024f, Handle), Margin + Handle, "non-finite y keeps the handle on screen");
}
// Offsets are stored relative to the anchor, so an unchanged position round-trips exactly; this is
// what stops a drag across many MouseDrag frames from accumulating drift.
foreach (var (x, y) in new[] { (786f, 160f), (500.5f, 300.25f), (4f, 18f) })
{
    var (dx, dy) = PanelDragMath.OffsetFor(x, y, 786f, 160f);
    Near(786f + dx, x, "offset roundtrip x");
    Near(160f + dy, y, "offset roundtrip y");
}
// A smaller screen must shrink the valid range, never widen it: the stale-offset case.
Near(PanelDragMath.ClampAxis(1800f, W, 1280f), 1280f - W - Margin, "shrunken screen pulls the panel back");
Near(PanelDragMath.ClampAxis(0f, W, 1280f), Margin, "left edge keeps the margin");
// Dragging by a delta moves by exactly that delta while inside the range.
foreach (float delta in new[] { -300f, -37.5f, 0f, 41f, 260f })
{
    float start = PanelDragMath.ClampAxis(500f, W, 1920f);
    float moved = PanelDragMath.ClampAxis(start + delta, W, 1920f);
    Near(moved - start, delta, "in-range drag tracks the pointer");
}

Console.WriteLine($"PASS: {checks} panel drag geometry assertions.");
