using RimLayers.Environment;
static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
var exact=HeatExchange.Calculate(new[]{new HeatVolume(80,9),new HeatVolume(20,9)},new[]{new HeatEdge(0,1,0.054)});
Check(Math.Abs(exact[0]+3.24)<1e-10 && Math.Abs(exact[0]+exact[1])<1e-10,"native units and conservation");
Check(HeatExchange.OutdoorChange(20,100,0.0005f,false)==0,"native tiny-coverage threshold");
Check(Math.Abs(HeatExchange.OutdoorChange(20,30,1,true)-0.06f)<1e-6,"native thin ceiling rate");
Check(Math.Abs(HeatExchange.OutdoorChange(0,150,1,true)-2.1f)<1e-6,"native extreme temperature adjustment");
var random=new Random(184972);
for(int trial=0;trial<100000;trial++)
{
    int count=random.Next(2,20);var volumes=Enumerable.Range(0,count).Select(i=>new HeatVolume(random.NextDouble()*1273.15-273.15,random.Next(1,1000))).ToArray();
    var edges=new List<HeatEdge>();
    for(int i=0;i<count;i++)for(int j=i+1;j<count;j++)if(random.Next(3)==0)edges.Add(new(i,j,random.NextDouble()*100000+0.001));
    var forward=HeatExchange.Calculate(volumes,edges.ToArray());edges.Reverse();var reverse=HeatExchange.Calculate(volumes,edges.ToArray());
    Check(Math.Abs(forward.Sum())<1e-7,"closed graph energy");
    double min=volumes.Min(v=>v.Temperature),max=volumes.Max(v=>v.Temperature);
    for(int i=0;i<count;i++)
    {
        double after=volumes[i].Temperature+forward[i]/volumes[i].Capacity;
        Check(after>=min-1e-9 && after<=max+1e-9,"no overshoot or clamp energy loss");
        Check(Math.Abs(forward[i]-reverse[i])<1e-7,"snapshot edge order independence");
    }
}
var reservoir=HeatExchange.Calculate(new[]{new HeatVolume(100,1,true),new HeatVolume(0,1)},new[]{new HeatEdge(0,1,1000)});
Check(Math.Abs(reservoir[1]-25)<1e-10,"outdoor reservoir remains bounded");
Console.WriteLine("HeatExchange: units, reservoir and 100000 conservative bounded order-independent graphs passed");
