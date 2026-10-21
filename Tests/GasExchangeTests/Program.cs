using RimLayers.Environment;
static void Require(bool pass,string name) { if(!pass) throw new Exception(name); }
uint[] pair={255u<<16,0};GasExchange.Apply(pair,new[]{true});
Require(pair[0]+pair[1]==255u<<16 && pair[1]>0,"pair conserves and transfers");
uint[] blocked={200u<<8,0};GasExchange.Apply(blocked,new[]{false});Require(blocked[0]==200u<<8 && blocked[1]==0,"closed link");
uint[] smoke={255,0,0};GasExchange.Apply(smoke,new[]{true,true});Require(smoke.SequenceEqual(new uint[]{255,0,0}),"native smoke policy");
var random=new Random(712910);
for(int test=0;test<100000;test++)
{
 int length=random.Next(1,9);var gas=new uint[length];var links=new bool[length-1];
 for(int i=0;i<length;i++) gas[i]=(uint)random.NextInt64(0,1L<<32);
 for(int i=0;i<links.Length;i++) links[i]=random.Next(2)==1;
 var previous=gas.ToArray();GasExchange.Apply(gas,links);
 for(int channel=0;channel<4;channel++)
 {
  int shift=channel*8;
  Require(gas.Sum(g=>(int)((g>>shift)&255))==previous.Sum(g=>(int)((g>>shift)&255)),"mass conservation");
 }
 for(int i=0;i<length;i++) Require((gas[i]&255)==(previous[i]&255),"smoke unchanged");
 var reverse=previous.Reverse().ToArray();GasExchange.Apply(reverse,links.Reverse().ToArray());
 Require(gas.SequenceEqual(reverse.Reverse()),"independent of layer iteration direction");
}
Console.WriteLine("Gas exchange: 3 fixed cases + 100000 randomized chains passed (mass, bounds, smoke, direction).");

foreach(float strength in new[]{0f,.1f,.5f,1f,1.5f,2f,3f,4f})
for(int test=0;test<5000;test++)
{
 int length=random.Next(1,9);var gas=Enumerable.Range(0,length).Select(_=>(uint)random.NextInt64(0,1L<<32)).ToArray();
 var links=Enumerable.Range(0,length-1).Select(_=>random.Next(2)==1).ToArray();var previous=gas.ToArray();
 GasExchange.Apply(gas,links,strength);
 for(int channel=0;channel<4;channel++)
  Require(gas.Sum(g=>(int)((g>>(channel*8))&255))==previous.Sum(g=>(int)((g>>(channel*8))&255)),"scaled mass conservation");
 for(int i=0;i<length;i++)Require((gas[i]&255)==(previous[i]&255),"scaled smoke unchanged");
 if(strength==0)Require(gas.SequenceEqual(previous),"zero exchange paused");
 var reverse=previous.Reverse().ToArray();GasExchange.Apply(reverse,links.Reverse().ToArray(),strength);
 Require(gas.SequenceEqual(reverse.Reverse()),"scaled order independence");
}
Console.WriteLine("Gas exchange: 40000 additional scaled chains passed (0 through 4x).");
