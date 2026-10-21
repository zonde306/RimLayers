using RimLayers.Environment;
int checks=0;
void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
void Near(float got,float expected,string why)=>Check(Math.Abs(got-expected)<.00001,why);
Near(EnvironmentMath.RockTarget(-12,1,0,.25f),-11.75f,"cold biome is not 15 C");
Near(EnvironmentMath.RockTarget(34,9,2,.25f),38.25f,"hot biome and gradient");
Near(EnvironmentMath.MineralBudgetFactor(1,.05f,1.4f),1,"B1 budget");
Near(EnvironmentMath.MineralBudgetFactor(5,.05f,1.4f),1.2f,"B5 budget");
Near(EnvironmentMath.MineralBudgetFactor(9,.05f,1.4f),1.4f,"B9 budget");
Near(EnvironmentMath.DepthWeight(1,.7f,.35f),1,"B1 industrial");
Near(EnvironmentMath.DepthWeight(5,.7f,.35f),.7f,"B5 industrial");
Near(EnvironmentMath.DepthWeight(9,.7f,.35f),.35f,"B9 industrial");
Near(EnvironmentMath.LavaFraction(5,6,.005f,2,.04f),0,"no lava before threshold");
Near(EnvironmentMath.LavaFraction(6,6,.005f,2,.04f),.005f,"B6");
Near(EnvironmentMath.LavaFraction(9,6,.005f,2,.04f),.04f,"lava capped");
Near(EnvironmentMath.LavaFraction(9,1,0,3,.1f),0,"zero lava stays zero");
Near(EnvironmentMath.Decay(1,2500,1),.5f,"one game hour half life");
Near(EnvironmentMath.TemperatureScore(-17,-17,60),0,"native lower boundary");
Near(EnvironmentMath.TemperatureScore(60,-17,60),0,"upper boundary");
Near(EnvironmentMath.TemperatureScore(55,-17,60),.5f,"hot ramp");
Near(EnvironmentMath.Wind(9,0),1,"zero enhancement");
for(int depth=1;depth<=9;depth++)
{
    float after=EnvironmentMath.Follow(20,40,120,3,depth);
    Check(after>20&&after<21,"no instantaneous temperature reset");
    if(depth>1)Check(after<EnvironmentMath.Follow(20,40,120,3,depth-1),"deeper buffers stronger");
}
var rnd=new Random(731);
for(int n=0;n<20000;n++)
{
    int x=rnd.Next(250),z=rnd.Next(250),seed=rnd.Next();
    double a=EnvironmentMath.Band(seed,x,z,5),b=EnvironmentMath.Band(seed,x,z,5),c=EnvironmentMath.Band(seed,x,z,6);
    Check(a==b,"same inputs deterministic");
    Check(a>=.08&&a<=1,"bounded band");
    Check(a!=c,"adjacent layers correlated, not copied");
}
for(int n=0;n<10000;n++)
{
    var v=Enumerable.Range(0,rnd.Next(2,30)).Select(i=>new HeatVolume(rnd.NextDouble(),rnd.Next(1,400))).ToArray();
    var e=new List<HeatEdge>();
    for(int i=0;i<v.Length;i++)for(int j=i+1;j<v.Length;j++)if(rnd.Next(4)==0)e.Add(new(i,j,rnd.NextDouble()*1000));
    double[] delta=HeatExchange.Calculate(v,e.ToArray());Check(Math.Abs(delta.Sum())<1e-8,"air mass conservation before deposition");
    for(int i=0;i<v.Length;i++)Check(v[i].Temperature+delta[i]/v[i].Capacity>=0&&v[i].Temperature+delta[i]/v[i].Capacity<=1,"no negative/overflow concentrations");
}
Console.WriteLine($"PASS {checks} climate, geology and concentration checks.");
