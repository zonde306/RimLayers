using RimLayers.Layout;
using RimLayers.Ships;
int checks=0;
void Check(string name,bool ok){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
ShipTopology.Result Shape(params string[] rows)
{
    var d=new Dictionary<LayerCell,int>();var b=new HashSet<LayerCell>();
    for(int i=0;i<rows.Length;i++)for(int x=0;x<rows[i].Length;x++)
    {
        var c=new LayerCell(rows.Length-1-i,x,0);
        if(rows[i][x]=='S')d[c]=0;else if(rows[i][x]=='C')b.Add(c);
    }
    var seed=d.Keys.Where(c=>c.LayerId==0).OrderBy(c=>c.X).First();d[seed]=1;
    return ShipTopology.Resolve(d,b,new Dictionary<int,HashSet<LayerCell>>{{1,new(){seed}}});
}
Check("disconnected upper islands share lower ship",Shape(".S.S.",".SSS.").Ships[1].Count==5);
Check("hollow middle remains hollow",Shape(".SSS.",".S.S.",".SSS.").Ships[1].Count==8);
Check("overhang inherits horizontal component",Shape("SS.SS","CSSSC").Ships[1].Count==7);
var isolated=Shape("SCS.SCS","CCSSSCC").Ships[1];
Check("ordinary floor cannot bridge outer islands",isolated.Count==5&&!isolated.Contains(new(1,0,0))&&!isolated.Contains(new(1,6,0)));
Check("skip entirely empty intermediate floor",Shape("..S..",".....",".SSS.").Ships[1].Count==4);
Check("ordinary intermediate floor blocks vertical search",Shape("..S..","..C..",".SSS.").Ships[1].Count==3);
Check("no diagonal lateral connection",Shape("S.",".S").Ships[1].Count==1);
var decks=new Dictionary<LayerCell,int>{{new(0,0,0),1},{new(0,1,0),1},{new(0,2,0),2},{new(0,3,0),2},{new(1,1,0),1},{new(1,2,0),2}};
var seeds=new Dictionary<int,HashSet<LayerCell>>{{1,new(){new(0,0,0)}},{2,new(){new(0,3,0)}}};
var two=ShipTopology.Resolve(decks,new HashSet<LayerCell>(),seeds);
Check("touching two ships remain independent",two.Conflicts.Count==0&&two.Ships[1].Count==3&&two.Ships[2].Count==3&&!two.Ships[1].Overlaps(two.Ships[2]));
decks[new(1,1,0)]=2;
two=ShipTopology.Resolve(decks,new HashSet<LayerCell>(),seeds);
Check("foreign ship blocks vertical ownership",!two.Ships[1].Contains(new(1,1,0)));
var loose=new Dictionary<LayerCell,int>{{new(0,0,0),1},{new(0,1,0),0},{new(0,2,0),2}};
var collision=ShipTopology.Resolve(loose,new HashSet<LayerCell>(),new Dictionary<int,HashSet<LayerCell>>{{1,new(){new(0,0,0)}},{2,new(){new(0,2,0)}}});
Check("ambiguous unclaimed bridge is not merged",collision.Conflicts.SetEquals(new[]{1,2})&&collision.Ships.Values.All(s=>!s.Contains(new(0,1,0))));
Console.WriteLine($"{checks} topology checks passed.");
