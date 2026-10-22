using System;
using System.Collections.Generic;
using System.Linq;

namespace RouteSuggestPlus;

/// <summary>One act's map as plain data, so flight planning can be tested and studied without the game.</summary>
public sealed class MapGraph
{
    public sealed class Node
    {
        public int Row;
        public NodeKind Kind;
        public int[] Children = Array.Empty<int>();
        /// <summary>Unknown room whose next rooms are all shops (the game then never rolls a shop).</summary>
        public bool NoShopRoll;
    }

    public readonly List<Node> Nodes = new();
    Dictionary<int, int[]>? _rows;

    public int Add(int row, NodeKind kind)
    {
        Nodes.Add(new Node { Row = row, Kind = kind });
        _rows = null;
        return Nodes.Count - 1;
    }

    public int[] Row(int row)
    {
        _rows ??= Nodes.Select((n, i) => (n.Row, i)).GroupBy(x => x.Row).ToDictionary(g => g.Key, g => g.Select(x => x.i).ToArray());
        return _rows.TryGetValue(row, out var r) ? r : Array.Empty<int>();
    }
}

/// <summary>A route that may use flights: <c>Jumped[i]</c> is true when node i was reached by flying.</summary>
public sealed class FlightRoute
{
    public List<int> Nodes = new();
    public List<bool> Jumped = new();
    public int Jumps;
    public PlanResult Plan = new();
}

/// <summary>
/// Winged Boots: up to 3 flights per run, each moving to any room of the next row. Within the act a beam search
/// over (room, flights used) keeps a few best states per cell, so the work stays around rows × 7 × 4 × beam.
/// Across acts, flights kept for later are worth a precomputed expectation (see <see cref="PlanParams.FlightReserve"/>).
/// </summary>
public static class FlightPlanner
{
    public const int Beam = 4;

    sealed class Entry
    {
        public Planner.State State = null!;
        public int Node, Used;
        public bool Jump;
        public Entry? Parent;
    }

    /// <summary>
    /// Best route using at most j flights, for j = 0..maxFlights (index j). Pins (row → node) must be visited.
    /// Entries can be null when no route exists (e.g. pins that cannot be joined even by flying).
    /// </summary>
    public static FlightRoute?[] Plan(MapGraph g, int start, int maxFlights, PlanContext ctx, PlanParams p,
        IReadOnlyDictionary<int, int>? pinsByRow = null)
    {
        maxFlights = Math.Clamp(maxFlights, 0, 3);
        var frontier = new List<Entry> { new() { State = Planner.Begin(g.Nodes[start].Kind, ctx, p), Node = start } };
        var finished = new List<Entry>();
        while (frontier.Count > 0)
        {
            var next = new Dictionary<(int node, int used), List<Entry>>();
            foreach (var e in frontier)
            {
                var node = g.Nodes[e.Node];
                if (node.Children.Length == 0) { finished.Add(e); continue; }
                foreach (int c in node.Children) Offer(next, Extend(g, e, c, false, ctx, p), pinsByRow, g);
                if (e.Used >= maxFlights) continue;
                foreach (int m in g.Row(node.Row + 1))
                    if (Array.IndexOf(node.Children, m) < 0 && g.Nodes[m].Kind != NodeKind.Boss)
                        Offer(next, Extend(g, e, m, true, ctx, p), pinsByRow, g);
            }
            frontier = next.Values.SelectMany(v => v).ToList();
        }

        var best = new FlightRoute?[maxFlights + 1];
        for (int j = 0; j <= maxFlights; j++)
        {
            var pick = finished.Where(e => e.Used <= j).OrderByDescending(e => e.State.Net).ThenBy(e => e.Used).FirstOrDefault();
            if (pick is not null) best[j] = ToRoute(g, pick);
        }
        return best;
    }

    static Entry Extend(MapGraph g, Entry e, int target, bool jump, PlanContext ctx, PlanParams p)
    {
        var n = g.Nodes[target];
        return new Entry
        {
            State = jump ? Planner.Charge(Planner.Advance(e.State, n.Kind, n.NoShopRoll, ctx, p), p.FlightCost)
                         : Planner.Advance(e.State, n.Kind, n.NoShopRoll, ctx, p),
            Node = target,
            Used = e.Used + (jump ? 1 : 0),
            Jump = jump,
            Parent = e,
        };
    }

    static void Offer(Dictionary<(int, int), List<Entry>> next, Entry e, IReadOnlyDictionary<int, int>? pins, MapGraph g)
    {
        if (pins is not null && pins.TryGetValue(g.Nodes[e.Node].Row, out int pinned) && pinned != e.Node) return;
        var key = (e.Node, e.Used);
        if (!next.TryGetValue(key, out var list)) next[key] = list = new List<Entry>(Beam + 1);
        list.Add(e);
        if (list.Count > Beam)
        {
            list.Sort((a, b) => b.State.Net.CompareTo(a.State.Net));
            list.RemoveAt(list.Count - 1);
        }
    }

    static FlightRoute ToRoute(MapGraph g, Entry end)
    {
        var chain = new List<Entry>();
        for (var e = end; e is not null; e = e.Parent) chain.Add(e);
        chain.Reverse();
        var route = new FlightRoute
        {
            Nodes = chain.Select(e => e.Node).ToList(),
            Jumped = chain.Select(e => e.Jump).ToList(),
            Jumps = end.Used,
        };
        route.Plan = Planner.Summarize(end.State, route.Nodes.Select(i => g.Nodes[i].Kind).ToList());
        return route;
    }

    /// <summary>Value of keeping <paramref name="charges"/> flights for the acts after <paramref name="act"/>.</summary>
    public static float Reserve(int act, int charges, PlanParams p)
    {
        int next = act + 1;
        if (next > 2 || charges <= 0) return 0f;
        int i = next * 4 + Math.Clamp(charges, 0, 3);
        return i < p.FlightReserve.Length ? p.FlightReserve[i] : 0f;
    }

    /// <summary>
    /// How many flights to spend in this act: the j maximizing (route with ≤ j flights) + (value of the rest later).
    /// Returns the chosen route (null if none) and the reserve value it keeps.
    /// </summary>
    public static (FlightRoute? route, float reserve) Choose(FlightRoute?[] best, int act, int charges, PlanParams p)
    {
        FlightRoute? pick = null;
        float pickTotal = float.NegativeInfinity, pickReserve = 0f;
        for (int j = 0; j < best.Length && j <= charges; j++)
        {
            if (best[j] is not { } r) continue;
            float reserve = Reserve(act, charges - r.Jumps, p);
            float total = r.Plan.Score + reserve;
            if (total > pickTotal + 1e-4f) { pick = r; pickTotal = total; pickReserve = reserve; }
        }
        return (pick, pickReserve);
    }
}
