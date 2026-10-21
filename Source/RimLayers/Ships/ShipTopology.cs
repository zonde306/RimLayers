using RimLayers.Layout;
namespace RimLayers.Ships;

/// <summary>Pure geometry. Roof legality is supplied by the caller, never inferred from ownership.</summary>
public static class ShipTopology
{
    public sealed record Result(Dictionary<int,HashSet<LayerCell>> Ships,HashSet<int> Conflicts);
    public static Result Resolve(IReadOnlyDictionary<LayerCell,int> decks,ISet<LayerCell> barriers,
        IReadOnlyDictionary<int,HashSet<LayerCell>> seeds)
    {
        var edges=decks.Keys.ToDictionary(c=>c,_=>new HashSet<LayerCell>());
        bool Compatible(LayerCell a,LayerCell b)=>decks[a]==0||decks[b]==0||decks[a]==decks[b];
        void Link(LayerCell a,LayerCell b)
        {
            if(!decks.ContainsKey(b)||!Compatible(a,b))return;
            edges[a].Add(b);edges[b].Add(a);
        }
        foreach(var c in decks.Keys)
        {
            Link(c,new(c.LayerId,c.X+1,c.Z));Link(c,new(c.LayerId,c.X,c.Z+1));
            for(int l=c.LayerId-1;l>=0;l--)
            {
                var below=new LayerCell(l,c.X,c.Z);
                if(decks.ContainsKey(below)){Link(c,below);break;}
                if(barriers.Contains(below))break;
            }
        }
        HashSet<LayerCell> Reach(int id,ISet<LayerCell>? contested=null)
        {
            var result=new HashSet<LayerCell>();var queue=new Queue<LayerCell>(seeds[id]);
            while(queue.TryDequeue(out var c))
            {
                if(!decks.TryGetValue(c,out int claim)||claim!=0&&claim!=id||
                    contested?.Contains(c)==true||!result.Add(c))continue;
                foreach(var n in edges[c])queue.Enqueue(n);
            }
            return result;
        }
        var first=seeds.Keys.ToDictionary(id=>id,id=>Reach(id));
        var contested=first.SelectMany(s=>s.Value).GroupBy(c=>c).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
        var conflicts=first.Where(s=>s.Value.Overlaps(contested)).Select(s=>s.Key).ToHashSet();
        return new(seeds.Keys.ToDictionary(id=>id,id=>Reach(id,contested)),conflicts);
    }
}
