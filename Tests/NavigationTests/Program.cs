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
