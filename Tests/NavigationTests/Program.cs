using RimLayers.Navigation;
int count=0;
void Assert(bool value,string name) { count++; if(!value) throw new Exception(name); }
List<int>? Route(Dictionary<int,RouteArc[]> graph) => EndpointSearch.Find(0,99,n=>graph.GetValueOrDefault(n)??Array.Empty<RouteArc>());
var graph=new Dictionary<int,RouteArc[]> {
 [0]=[new(1,1),new(2,2),new(3,3),new(4,4)],
 [1]=[new(0,1)], [2]=[new(1,0)], [3]=[new(2,0)],
 [4]=[new(6,1)], [6]=[new(8,1)], [8]=[new(7,1)], [7]=[new(99,1)]
};
Assert(Route(graph)!.SequenceEqual(new[]{0,4,6,8,7,99}),"fourth connector and non-monotonic route");
graph[0]=[new(1,1),new(99,20)];
graph[1]=[new(2,1),new(99,50)];
graph[2]=[new(1,0),new(99,1)];
Assert(Route(graph)!.SequenceEqual(new[]{0,1,2,99}),"lowest total cost and zero-cost cycle");
graph.Remove(2);
Assert(Route(graph)!.SequenceEqual(new[]{0,99}),"removed edge replan");
graph.Remove(0);
Assert(Route(graph)==null,"disconnected target");
Assert(EndpointSearch.Find(3,3,_=>throw new Exception())!.SequenceEqual(new[]{3}),"already at goal");
foreach(var cost in new[]{-1f,float.NaN,float.PositiveInfinity}) {
 bool rejected=false; try { EndpointSearch.Find(0,1,_=>new[]{new RouteArc(1,cost)}); } catch(ArgumentException){rejected=true;}
 Assert(rejected,"invalid edge cost");
}
Console.WriteLine($"PASS: {count} endpoint-search cases.");

// --- Bounded search: the pruned variant must never change the optimum when the bound is safe, ---
// --- and must report "nothing better" exactly when no cheaper route exists.                    ---
List<int>? Bounded(Dictionary<int,RouteArc[]> g,int s,int t,float bound)=>EndpointSearch.Find(s,t,n=>g.GetValueOrDefault(n)??Array.Empty<RouteArc>(),bound);
var bounded=new Dictionary<int,RouteArc[]> {
 [0]=[new(1,10),new(2,100)],
 [1]=[new(2,10),new(3,1)],
 [2]=[new(3,1)],
};
// Cheapest route is 0->1->3 = 11. 0->1->2->3 costs 21 and is not optimal.
Assert(Bounded(bounded,0,3,float.PositiveInfinity)!.SequenceEqual(new[]{0,1,3}),"unbounded optimal route");
// A bound above the optimum still finds the same optimum.
Assert(Bounded(bounded,0,3,100f)!.SequenceEqual(new[]{0,1,3}),"loose bound keeps optimum");
// A bound at or below the cheapest possible prefix means no strictly better route can exist.
Assert(Bounded(bounded,0,3,10f)==null,"tight bound reports no improvement");
Assert(Bounded(bounded,0,3,5f)==null,"below bound reports no improvement");
// Bound must never prune a route that is strictly cheaper than the bound.
foreach(float bound in new[]{12f,11.5f,11.0001f})
 Assert(Bounded(bounded,0,3,bound)!.SequenceEqual(new[]{0,1,3}),$"bound {bound} pruned the optimum");
// A bound equal to the optimum may return null (nothing strictly better); the caller keeps its own
// route in that case. It must never return a WORSE route.
var tie=Bounded(bounded,0,3,11f);
Assert(tie==null||tie.SequenceEqual(new[]{0,1,3}),"tie bound returned a worse route");
// Unreachable goal stays unreachable regardless of bound.
Assert(Bounded(bounded,0,99,float.PositiveInfinity)==null,"unreachable goal");
Assert(Bounded(bounded,0,99,1000f)==null,"unreachable goal with bound");
Console.WriteLine($"PASS: {count} total navigation cases including bounded search.");

// --- DirectIsOptimal: the exact argument that lets the router skip connector pricing. ---
// Same floor, direct leg costs 200, cheapest crossing is 90 (stairs). Two crossings cost 180 < 200,
// so a connector route COULD be cheaper and the search must run.
Assert(!RouteBounds.DirectIsOptimal(200f,90f),"two crossings cheaper than direct must not prune");
// Direct leg 170 <= 2*90: no connector route can beat it, so pruning is safe.
Assert(RouteBounds.DirectIsOptimal(170f,90f),"direct at exactly two crossings must prune");
Assert(RouteBounds.DirectIsOptimal(100f,90f),"direct cheaper than two crossings must prune");
Assert(RouteBounds.DirectIsOptimal(0f,90f),"zero-cost direct leg must prune");
// Degenerate cheapest crossings make the lower bound useless, so pruning must be refused.
Assert(!RouteBounds.DirectIsOptimal(100f,0f),"zero cheapest crossing must not prune");
Assert(!RouteBounds.DirectIsOptimal(100f,-1f),"negative cheapest crossing must not prune");
Assert(!RouteBounds.DirectIsOptimal(100f,float.PositiveInfinity),"infinite crossing must not prune");
Assert(!RouteBounds.DirectIsOptimal(100f,float.NaN),"NaN crossing must not prune");
Assert(!RouteBounds.DirectIsOptimal(float.PositiveInfinity,90f),"infinite direct must not prune");
Assert(!RouteBounds.DirectIsOptimal(float.NaN,90f),"NaN direct must not prune");
// Monotonic in the direct cost: once it exceeds twice the crossing, pruning stops.
for(float crossing=1f;crossing<=100f;crossing+=7f)
{
 Assert(RouteBounds.DirectIsOptimal(2f*crossing,crossing),$"boundary at crossing {crossing} must prune");
 Assert(!RouteBounds.DirectIsOptimal(2f*crossing+0.001f,crossing),$"just above crossing {crossing} must not prune");
}
Console.WriteLine($"PASS: {count} total navigation cases including route bounds.");
