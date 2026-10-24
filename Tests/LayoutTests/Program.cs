using RimLayers.Layout;

int checks = 0;
void Assert(bool condition, string message)
{
    checks++;
    if (!condition) throw new Exception(message);
}
void Reject(Action action)
{
    try { action(); }
    catch (ArgumentException) { checks++; return; }
    catch (OverflowException) { checks++; return; }
    throw new Exception("Expected invalid layout rejection.");
}
foreach (var (width, height) in new[] { (1, 1), (17, 16), (18, 17), (50, 18), (120, 120), (250, 250), (251, 249) })
{
    int stride = LayerLayout.CalculateStride(height);
    Assert(stride > height && stride % 17 == 0, "Missing aligned isolation rows.");
    var layout = new LayerLayout(width, height, 3, stride);
    int logical = 0, isolation = 0;
    for (int z = 0; z < layout.StorageHeight; z++)
        for (int x = 0; x < width; x++)
        {
            var storage = new StorageCell(x, 0, z);
            bool expected = z % stride < height;
            bool decoded = layout.TryDecode(storage, out var cell);
            Assert(decoded == expected, "Storage/gap classification failed.");
            if (decoded)
            {
                logical++;
                Assert(layout.Contains(cell) && layout.ToStorage(cell) == storage, "Address roundtrip failed.");
            }
            else isolation++;
        }
    Assert(logical == width * height * 3, "Logical area mismatch.");
    Assert(isolation == width * (stride - height) * 3, "Isolation area mismatch.");
    Assert(layout.MatchesStorage(width, stride * 3), "Storage extent mismatch.");
    foreach (var outside in new[] { new StorageCell(-1, 0, 0), new StorageCell(width, 0, 0),
        new StorageCell(0, 1, 0), new StorageCell(0, 0, -1), new StorageCell(0, 0, layout.StorageHeight) })
        Assert(!layout.TryDecode(outside, out _), "Accepted out-of-bounds cell.");
    Reject(() => layout.ToStorage(new LayerCell(3, 0, 0)));
    Reject(() => layout.ToStorage(new LayerCell(0, width, 0)));
    Reject(() => layout.ToStorage(new LayerCell(0, 0, height)));
}
Reject(() => LayerLayout.CalculateStride(0));
Reject(() => LayerLayout.CalculateStride(int.MaxValue));
Reject(() => new LayerLayout(0, 120, 3, 136));
Reject(() => new LayerLayout(120, 120, 1, 136));
Reject(() => new LayerLayout(120, 120, 3, 120));
Reject(() => new LayerLayout(int.MaxValue, 120, 3, 136));
Console.WriteLine($"PASS: {checks} layout checks including all cells, separators, bounds and overflow.");

foreach(var levels in new[]{new[]{0,-1,-2,1,2},new[]{0,1,-1,2,-2},new[]{0,1,2,-1,-2}})
{
    var mixed=new LayerLayout(120,120,levels,136);
    Assert(mixed.OrderedLayers.Select(mixed.ElevationOf).SequenceEqual(new[]{-2,-1,0,1,2}),"Physical order differs from allocation order.");
    for(int id=0;id<5;id++)
    {
        Assert(mixed.ToStorage(new(id,5,7))==new StorageCell(5,0,id*136+7),"Existing bands moved.");
        Assert(mixed.ProjectionLayer(mixed.ElevationOf(id)*3+1)==id,"Negative-height projection failed.");
        if(mixed.ElevationOf(id)<2)Assert(mixed.TryGetAdjacentLayer(id,1,out int up)&&mixed.ElevationOf(up)==mixed.ElevationOf(id)+1,"Wrong upper neighbor.");
        if(mixed.ElevationOf(id)>-2)Assert(mixed.TryGetAdjacentLayer(id,-1,out int down)&&mixed.ElevationOf(down)==mixed.ElevationOf(id)-1,"Wrong lower neighbor.");
    }
}
var firstDown=new LayerLayout(120,120,new[]{0,-1},136);
var withUp=firstDown.Append(1);
Assert(withUp.Extends(firstDown)&&withUp.ToStorage(new(1,8,9))==firstDown.ToStorage(new(1,8,9)),"Appending changed stable addresses.");
Assert(firstDown.LayerOrFuture(1)==2&&firstDown.ElevationOrFuture(2)==1,"Future upper preview aliases underground.");
Reject(()=>new LayerLayout(120,120,new[]{0,-2},136));
Reject(()=>new LayerLayout(120,120,new[]{0,-1,-1},136));
Reject(()=>new LayerLayout(120,120,Enumerable.Range(0,11).ToArray(),136));

// Above/Below are now precomputed tables. They must match the previous LINQ projection exactly:
// Above ascending by elevation, Below descending (nearest first), for every allocation order.
foreach(var levels in new[]{new[]{0,1,2},new[]{0,1},new[]{0,-1},new[]{0,-1,-2},new[]{0,-1,-2,1,2},
    new[]{0,2,1,-1,-2},new[]{0,-3,-1,-2,1,3,2}})
{
    var layout=new LayerLayout(120,120,levels,136);
    foreach(int id in Enumerable.Range(0,layout.LayerCount))
    {
        int here=layout.ElevationOf(id);
        var expectedAbove=Enumerable.Range(0,layout.LayerCount)
            .OrderBy(layout.ElevationOf).Where(x=>layout.ElevationOf(x)>here).ToArray();
        var expectedBelow=Enumerable.Range(0,layout.LayerCount)
            .OrderBy(layout.ElevationOf).Reverse().Where(x=>layout.ElevationOf(x)<here).ToArray();
        Assert(layout.Above(id).SequenceEqual(expectedAbove),$"Above({id}) differs from the ordered projection.");
        Assert(layout.Below(id).SequenceEqual(expectedBelow),$"Below({id}) differs from the reversed projection.");
        Assert(layout.Above(id).All(x=>layout.ElevationOf(x)>here),"Above contained a non-upper layer.");
        Assert(layout.Below(id).All(x=>layout.ElevationOf(x)<here),"Below contained a non-lower layer.");
    }
    // OrderedLayers must remain the ascending physical order and must not be reallocated per call.
    Assert(layout.OrderedLayers.Select(layout.ElevationOf).SequenceEqual(
        Enumerable.Range(0,layout.LayerCount).Select(layout.ElevationOf).OrderBy(x=>x)),"OrderedLayers order changed.");
    Assert(ReferenceEquals(layout.OrderedLayers,layout.OrderedLayers),"OrderedLayers reallocated per access.");
    Assert(ReferenceEquals(layout.Above(0),layout.Above(0))&&ReferenceEquals(layout.Below(0),layout.Below(0)),
        "Above/Below reallocated per access.");
}
// Append must rebuild the tables for the extended layout, not share the old ones.
var appended=new LayerLayout(120,120,new[]{0,-1},136).Append(1);
Assert(appended.Above(appended.LayerAt(0)).Select(appended.ElevationOf).SequenceEqual(new[]{1}),
    "Appended layout Above() was not recomputed.");
Assert(appended.Below(appended.LayerAt(0)).Select(appended.ElevationOf).SequenceEqual(new[]{-1}),
    "Appended layout Below() was not recomputed.");
Console.WriteLine($"PASS: {checks} total layout checks, including mixed underground allocation.");
