using RimLayers.Combat;

int checks=0;
void Check(string name,bool pass) { checks++; if(!pass) throw new Exception("FAIL: "+name); }
var scene=new Scene();
Check("open diagonal",ShotGeometry.First(new(0.5,7.35,0.5),new(8.5,1.35,8.5),scene)==null);
scene.Floors.Add((1,2,2));
var hit=ShotGeometry.First(new(2.5,7.35,2.5),new(2.5,1.35,2.5),scene);
Check("middle slab blocks",hit?.Kind==ObstructionKind.Floor && hit?.Layer==1);
scene.Floors.Add((2,2,2));
hit=ShotGeometry.First(new(2.5,7.35,2.5),new(2.5,1.35,2.5),scene);
Check("nearest slab first",hit?.Layer==2);
scene.Floors.Clear();
for(int x=0;x<=3;x++) scene.Floors.Add((1,x,2));
Check("edge fire clears slab",ShotGeometry.First(new(3.5,4.35,2.5),new(8.5,1.35,2.5),scene)==null);
Check("interior fire strikes slab",ShotGeometry.First(new(2.5,4.35,2.5),new(4.5,1.35,2.5),scene)?.Kind==ObstructionKind.Floor);
scene.Floors.Clear();scene.Roofs.Add((0,2,2));
Check("roof independently blocks",ShotGeometry.First(new(2.5,4.35,2.5),new(2.5,1.35,2.5),scene)?.Kind==ObstructionKind.Roof);
scene.Roofs.Clear();scene.Walls[(0,3,2)]=(2,77);
hit=ShotGeometry.First(new(0.5,1.35,2.5),new(8.5,1.35,2.5),scene);
Check("wall boundary exact",hit?.ThingKey==77 && Math.Abs(hit.Value.Time-2.5/8)<1e-9);
Check("above wall clears",ShotGeometry.First(new(0.5,2.5,2.5),new(8.5,2.5,2.5),scene)==null);
scene.Walls.Clear();scene.Walls[(1,3,2)]=(3,88);
hit=ShotGeometry.First(new(0.5,7.35,2.5),new(8.5,1.35,2.5),scene);
Check("descending shot hits upper wall",hit?.ThingKey==88 && hit?.Layer==1);
ShotPoint start=new(0.5,7.35,2.5),end=new(8.5,1.35,2.5),mid=start.At(end,0.2);
var sub=ShotGeometry.First(mid,end,scene);
Check("swept subsegment agrees",sub?.ThingKey==hit?.ThingKey && Math.Abs((0.2+0.8*sub!.Value.Time)-hit!.Value.Time)<1e-9);
bool rejected=false;
try { ShotGeometry.First(new(double.NaN,1,0),end,scene); } catch(ArgumentException) { rejected=true; }
Check("invalid geometry rejected",rejected);
Check("finite cylinder side entry",Math.Abs(ShotGeometry.CylinderEntry(new(0,1,0),new(10,1,0),5,0,1,0,3)!.Value-0.4)<1e-9);
Check("finite cylinder cap entry",Math.Abs(ShotGeometry.CylinderEntry(new(5,5,0),new(5,1,0),5,0,1,0,3)!.Value-0.5)<1e-9);
Check("other-layer cylinder misses",ShotGeometry.CylinderEntry(new(0,4,0),new(10,4,0),5,0,1,0,3)==null);
Check("grid spans include actual middle layer",ShotGeometry.Cells(new(0.5,7.35,0.5),new(8.5,1.35,0.5),3).Select(c=>c.Layer).Distinct().Order().SequenceEqual(new[]{0,1,2}));
var below=ShotGeometry.Approach(new(2.5,1,2.5),new(2.5,5,2.5),0.5);
var above=ShotGeometry.Approach(new(2.5,5,2.5),new(2.5,1,2.5),0.5);
Check("underside contact stays below slab",below.Height<3 && Math.Floor(below.Height/3)==0);
Check("top-side contact stays above slab",above.Height>3 && Math.Floor(above.Height/3)==1);
Check("topmost roof impact can remain in sky",ShotGeometry.Approach(new(2.5,12,2.5),new(2.5,6,2.5),0.5).Height>9);
Check("wall contact remains outside solid footprint",ShotGeometry.Approach(new(0.5,1,2.5),new(8.5,1,2.5),2.5/8).X<3);
// Height-dependent silhouette exposure, distinct from native fillPercent.
scene.Walls.Clear();scene.Floors.Clear();scene.Roofs.Clear();
scene.Walls[(1,4,2)]=(1,99);
ShotPoint highTarget=new(3.5,4.35,2.5),lowShooter=new(25.5,1.35,2.5),levelShooter=new(25.5,4.35,2.5);
int Exposed(ShotPoint from,ShotPoint center,double bottom,double height)=>
    ExposureGeometry.Body(from,center,bottom,height,0.22).Count(p=>ShotGeometry.First(from,p,scene)==null);
int lowExposure=Exposed(lowShooter,highTarget,3,1.8),levelExposure=Exposed(levelShooter,highTarget,3,1.8);
Check("nine deterministic body samples",ExposureGeometry.Body(lowShooter,highTarget,3,1.8,0.22).Length==9);
Check("uphill cover exposes less body",lowExposure>0 && lowExposure<levelExposure);
Check("muzzle above own cover may fire downhill",ShotGeometry.First(highTarget,lowShooter,scene)==null);
Check("approaching high cover steepens the blind angle",Exposed(new(6.5,1.35,2.5),highTarget,3,1.8)<lowExposure);
Check("side approach bypasses front cover",Exposed(new(3.5,1.35,15.5),highTarget,3,1.8)==9);
Check("steep downward shot hits own cover",ShotGeometry.First(highTarget,new(6.5,1.35,2.5),scene)?.ThingKey==99);
Check("prone body can be completely hidden",Exposed(lowShooter,highTarget,3,0.45)==0);
scene.Walls.Clear();scene.Walls[(0,5,2)]=(1,98);
Check("downward angle clears more low cover",Exposed(new(7.5,4.35,2.5),new(3.5,1.35,2.5),0,1.8)>Exposed(new(7.5,1.35,2.5),new(3.5,1.35,2.5),0,1.8));
scene.Walls.Clear();scene.Walls[(1,2,2)]=(1,100);
Check("cover behind target provides no protection",Exposed(lowShooter,highTarget,3,1.8)==9);
scene.Walls.Clear();scene.Walls[(0,9,2)]=(3,101);
var direct=ShotGeometry.First(new(3.5,1.35,2.5),new(14.5,1.35,2.5),scene);
Check("solid geometry is reciprocal",direct!=null && ShotGeometry.First(new(14.5,1.35,2.5),new(3.5,1.35,2.5),scene)!=null);
Check("curve clears wall",ExposureGeometry.First(new(3.5,1.35,2.5),new(14.5,1.35,2.5),5,scene)==null);
scene.Floors.Add((1,4,2));scene.Floors.Add((1,5,2));
Check("same-floor curve hits upper slab",ExposureGeometry.First(new(3.5,1.35,2.5),new(14.5,1.35,2.5),5,scene)?.Kind==ObstructionKind.Floor);

scene.Walls.Clear();scene.Floors.Clear();scene.Roofs.Clear();scene.MinLayer=-2;
scene.Walls[(-1,3,2)]=(3,201);
Check("underground wall hit uses negative physical level",ShotGeometry.First(new(.5,-1.65,2.5),new(8.5,-1.65,2.5),scene)?.ThingKey==201);
scene.Walls.Clear();scene.Floors.Add((0,2,2));
Check("surface seals underground fire",ShotGeometry.First(new(2.5,-1.65,2.5),new(2.5,1.35,2.5),scene)?.Kind==ObstructionKind.Floor);
scene.Floors.Clear();scene.Roofs.Add((-2,2,2));
Check("B2 roof blocks B1 shot",ShotGeometry.First(new(2.5,-1.65,2.5),new(2.5,-4.65,2.5),scene)?.Layer==-2);
Check("spans preserve both underground levels",ShotGeometry.Cells(new(.5,-4.65,.5),new(.5,1.35,.5),3).Select(s=>s.Layer).Distinct().Order().SequenceEqual(new[]{-2,-1,0}));
Console.WriteLine($"PASS: {checks} shot-geometry checks.");

sealed class Scene:IShotScene
{
    public int Layers=>3;
    public int MinLayer {get;set;}
    public int MaxLayer=>2;
    public double LayerHeight=>3;
    public HashSet<(int,int,int)> Floors=new(),Roofs=new();
    public Dictionary<(int,int,int),(double,int)> Walls=new();
    public bool Floor(int l,int x,int z)=>Floors.Contains((l,x,z));
    public bool Roof(int l,int x,int z)=>Roofs.Contains((l,x,z));
    public (double Height,int ThingKey) Solid(int l,int x,int z)=>Walls.GetValueOrDefault((l,x,z));
}
