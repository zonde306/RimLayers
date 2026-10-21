using RimLayers;
using RimLayers.Layout;

int checks=0;
void Check(bool yes,string name){checks++;if(!yes)throw new Exception(name);}
void Near(float got,float want,string name)=>Check(Math.Abs(got-want)<.00001f,name);
for(int delta=-18;delta<=18;delta++)
{
    Near(SettingsMath.RangeMultiplier(delta,false,0),1,"Zero height strength restores range");
    Near(SettingsMath.RangeMultiplier(delta,true,2),1,"Indirect fire remains native");
    Near(SettingsMath.RangeMultiplier(delta,false,1),1+Math.Clamp(delta*.1f,-.2f,.2f),"Default range preserves old effect");
    Near(SettingsMath.RangeMultiplier(delta,false,2)-1,2*(SettingsMath.RangeMultiplier(delta,false,1)-1),"Strength scales the effect");
}
foreach(float multiplier in new[]{.25f,1f,4f})foreach(int baseline in new[]{25,30,45,90,120,180,480})
    Check(SettingsMath.TravelTicks(baseline,multiplier)==(int)Math.Round(baseline*multiplier,MidpointRounding.AwayFromZero),"Travel time rounding");
Check(SettingsMath.MaterialCount(0,4)==0,"No invented material requirements");
Check(SettingsMath.MaterialCount(1,.25f)==1,"Positive costs stay positive");
Check(SettingsMath.MaterialCount(80,.25f)==20,"Material scaling");

ExpansionDecision Policy(int target,int low=0,int high=0,int up=2,int down=2,bool space=false,bool allowSpace=true,int used=0,int reserved=0,int cap=20,int promise=-1,int covered=0)=>
    ExpansionPolicy.Check(low,high,target,up,down,space,allowSpace,used,reserved,cap,promise,covered);
Check(Policy(2).Allowed&&Policy(2).Additional==2,"Batch increment includes both floors");
Check(Policy(3).Denial==ExpansionDenial.UpperLimit,"Upper limit");
Check(Policy(-3).Denial==ExpansionDenial.UndergroundLimit,"Underground limit");
Check(Policy(1,up:0).Denial==ExpansionDenial.UpperLimit,"Zero upper limit");
Check(Policy(-1,down:0).Denial==ExpansionDenial.UndergroundLimit,"Zero underground limit");
Check(Policy(2,used:19).Denial==ExpansionDenial.GlobalLimit,"Batch rejected before any partial allocation");
Check(Policy(1,used:19).Allowed,"Exact global boundary");
Check(Policy(1,used:19,reserved:1).Denial==ExpansionDenial.GlobalLimit,"Reservations count");
Check(Policy(2,used:19,reserved:2,cap:21,covered:2).Allowed,"Reserved space converts into occupancy");
Check(Policy(1,space:true,allowSpace:false).Denial==ExpansionDenial.Space,"Space addition disabled");
Check(Policy(-1,space:true).Denial==ExpansionDenial.Space,"Space excavation stays forbidden");
Check(Policy(2,low:-5,high:5,up:0,down:0,space:true,allowSpace:false,used:40,cap:0).Allowed,"Existing floors survive every lowered limit");
Check(Policy(3,up:9,space:true,allowSpace:false,promise:3,used:40,cap:0).Allowed,"Promised journey survives changed settings");
Check(Policy(4,up:9,space:true,allowSpace:false,promise:3,covered:3).Denial==ExpansionDenial.Space,"Higher landing cannot borrow a space exemption");
Check(Policy(4,up:9,promise:3,used:19,reserved:3,cap:22,covered:3).Denial==ExpansionDenial.GlobalLimit,"Higher landing checks extra global quota");
Check(Policy(10,up:9,promise:10).Denial==ExpansionDenial.HardCapacity,"Promises never exceed hard capacity");
Check(Policy(-10,down:9).Denial==ExpansionDenial.HardCapacity,"Negative hard capacity");
Check(Policy(0,cap:0).Allowed,"Native base map is not quota-limited");

int sequences=0;
void AppendOrders(List<int> elevations,int up,int down)
{
    if(up==5&&down==5)
    {
        sequences++;
        var layout=new LayerLayout(250,250,elevations,LayerLayout.CalculateStride(250));
        Check(layout.LayerCount==11&&layout.LowestElevation==-5&&layout.HighestElevation==5,"Eleven-floor capacity");
        for(int id=0;id<11;id++)
        {
            var cell=new LayerCell(id,149,173);var storage=layout.ToStorage(cell);
            Check(storage==new StorageCell(149,0,id*255+173),"Storage addresses do not depend on append order");
            Check(layout.TryDecode(storage,out var decoded)&&decoded==cell,"Eleven-floor roundtrip");
            Check(layout.ProjectionLayer(layout.ElevationOf(id)*3+1)==id,"Physical elevation projection");
        }
        return;
    }
    if(up<5)AppendOrders(elevations.Append(up+1).ToList(),up+1,down);
    if(down<5)AppendOrders(elevations.Append(-down-1).ToList(),up,down+1);
}
AppendOrders(new(){0},0,0);
Check(sequences==252,"Every eleven-floor up/down append interleaving covered");
var maximum=new LayerLayout(250,250,new[]{0}.Concat(Enumerable.Range(1,9)).Concat(Enumerable.Range(1,9).Select(x=>-x)).ToArray(),255);
Check(maximum.LayerCount==19&&maximum.ProjectionLayer(-26)==maximum.LayerAt(-9),"Full hard capacity and negative projection");
for(int depth=1;depth<=18;depth++)Check(SettingsMath.BackgroundQueueBase(depth)>=0&&SettingsMath.BackgroundQueueBase(depth)+80<SettingsMath.BackgroundQueueBase(depth-1),"Render queue bands stay nonnegative and disjoint");
Console.WriteLine($"PASS: {checks} settings/policy checks; {sequences} complete 11-floor append orders.");
