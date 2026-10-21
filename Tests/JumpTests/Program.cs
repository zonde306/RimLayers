using RimLayers.Combat;
using RimLayers.Navigation;

int count=0;
void Check(string name,bool pass){count++;if(!pass)throw new Exception("FAIL: "+name);}
JumpArc Arc(ShotPoint a,ShotPoint b)=>new(a,b,JumpGeometry.RiseFor(b.Height-a.Height),1.8,.22);
var up=Arc(new(50.5,.03,53.5),new(63.5,3.03,53.5));
var down=Arc(up.End,up.Start);
var scene=new Scene();
Check("open air",JumpGeometry.First(up,scene)==null);
for(int x=60;x<=70;x++)for(int z=50;z<=56;z++){scene.Floors.Add((1,x,z));scene.Roofs.Add((0,x,z));}
Check("up over platform edge",JumpGeometry.First(up,scene)==null);
Check("down over platform edge",JumpGeometry.First(down,scene)==null);
Check("floor blocks vertical ascent",JumpGeometry.First(Arc(new(63.5,.03,53.5),up.End),scene)?.Kind==ObstructionKind.Floor);
Check("floor blocks vertical descent",JumpGeometry.First(Arc(up.End,new(63.5,.03,53.5)),scene)?.Kind==ObstructionKind.Floor);
scene.Floors.Clear();
Check("roof blocks independently",JumpGeometry.First(Arc(new(63.5,.03,53.5),up.End),scene)?.Kind==ObstructionKind.Roof);
scene.Roofs.Clear();
scene.Roofs.Add((0,52,53));
var head=JumpGeometry.First(up,scene);
Check("head hits ceiling before feet",head!=null&&up.Point(head.Value.Time).Height<3);
scene.Roofs.Clear();scene.Floors.Add((0,63,53));
var underground=Arc(new(63.5,.03,53.5),new(63.5,-2.97,53.5));
Check("earth blocks descent underground",JumpGeometry.First(underground,scene)?.Layer==0);
scene.Floors.Clear();scene.Floors.Add((0,63,53));
Check("earth blocks emergence",JumpGeometry.First(Arc(underground.End,underground.Start),scene)?.Layer==0);
scene.Floors.Clear();
var longUp=Arc(new(50.5,.03,53.5),new(68.5,6.03,53.5));
for(int x=66;x<=70;x++)for(int z=50;z<=56;z++)scene.Floors.Add((2,x,z));
Check("two-storey edge ascent",JumpGeometry.First(longUp,scene)==null);
scene.Floors.Add((1,53,53));
Check("intermediate floor blocks",JumpGeometry.First(longUp,scene)?.Layer==1);
scene.Floors.Clear();
scene.Walls[(0,53,53)]=(3,7);
var first=JumpGeometry.First(up,scene);
Check("wall has physical height",first?.ThingKey==7);
foreach(int pieces in new[]{1,2,7,60,251})
{
    ShotObstruction? swept=null;
    for(int i=0;i<pieces;i++){swept=JumpGeometry.First(up,scene,(double)i/pieces,(double)(i+1)/pieces);if(swept!=null)break;}
    Check("delta-independent contact "+pieces,swept?.ThingKey==7&&Math.Abs(swept.Value.Time-first!.Value.Time)<1e-8);
}
scene.Walls.Clear();
scene.Floors.Add((1,52,54));
var edgeBody=up with{Start=up.Start with{Z=53.9},End=up.End with{Z=53.9}};
Check("body width catches offset floor",JumpGeometry.First(edgeBody,scene)!=null);
Check("centre ray alone would miss",JumpGeometry.First(edgeBody with{HalfWidth=0},scene)==null);
Check("vertical height costs range",!JumpGeometry.InRange(up,13)&&JumpGeometry.InRange(up,14));
for(int delta=-27;delta<=27;delta+=3)
{
    var arc=Arc(new(.5,.03,.5),new(15.5,delta+.03,.5));
    Check("arrival descends onto floor "+delta,arc.Point(.9999).Height>arc.End.Height);
    Check("endpoints exact "+delta,Math.Abs(arc.Point(0).Height-.03)<1e-9&&Math.Abs(arc.Point(1).Height-arc.End.Height)<1e-9);
}
Console.WriteLine($"PASS: {count} jump geometry assertions");

sealed class Scene:IShotScene
{
    public int Layers=>6;public int MinLayer=>-2;public int MaxLayer=>3;public double LayerHeight=>3;
    public HashSet<(int,int,int)> Floors=new(),Roofs=new();
    public Dictionary<(int,int,int),(double,int)> Walls=new();
    public bool Floor(int l,int x,int z)=>Floors.Contains((l,x,z));
    public bool Roof(int l,int x,int z)=>Roofs.Contains((l,x,z));
    public (double Height,int ThingKey) Solid(int l,int x,int z)=>Walls.GetValueOrDefault((l,x,z));
}
