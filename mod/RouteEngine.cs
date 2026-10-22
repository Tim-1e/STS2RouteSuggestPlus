using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace RouteSuggestPlus;

/// <summary>Reads live run state into a <see cref="PlanContext"/>. Every read is guarded: a failed read keeps the default.</summary>
public static class GameContext
{
    public static Player? Me(RunState run)
    {
        try { return LocalContext.GetMe(run.Players) ?? run.Players.FirstOrDefault(); }
        catch { return run.Players.FirstOrDefault(); }
    }

    public static PlanContext Build(RunState run, RouteSettings settings)
    {
        var ctx = new PlanContext { ActIndex = run.CurrentActIndex };
        Try(() =>
        {
            ctx.StrengthSamples = StrengthHistory.Ratios(run, settings.Plan);
            ctx.Strength = Planner.StrengthFactor(ctx.StrengthSamples, settings.Plan);
        });
        var me = Me(run);
        if (me is null) return ctx;
        Try(() => { ctx.Hp = me.Creature.CurrentHp; ctx.MaxHp = me.Creature.MaxHp; });
        Try(() => ctx.Gold = me.Gold);
        Try(() =>
        {
            ctx.RemovalStep = MerchantCardRemovalEntry.PriceIncrease;
            int baseCost = AscensionHelper.GetValueIfAscension(AscensionLevel.Inflation, 100, 75);
            ctx.RemovalCost = baseCost + ctx.RemovalStep * me.ExtraFields.CardShopRemovalsUsed;
        });
        Try(() => ctx.Poverty = AscensionHelper.HasAscension(AscensionLevel.Poverty));
        Try(() =>
        {
            ctx.PotionSlots = me.MaxPotionCount;
            ctx.Potions = me.Potions.Select(p => PotionValue(p, settings)).ToList();
        });
        Try(() => ctx.PotionChance = me.PlayerOdds.PotionReward.CurrentValue);
        Try(() =>
        {
            var odds = run.Odds.UnknownMapPoint;
            ctx.UnknownMonster = Math.Max(0, odds.MonsterOdds);
            ctx.UnknownTreasure = Math.Max(0, odds.TreasureOdds);
            ctx.UnknownShop = Math.Max(0, odds.ShopOdds);
        });
        Try(() => ctx.RestHeal = (int)HealRestSiteOption.GetHealAmount(me));
        Try(() => ctx.RestBonus = settings.Plan.RestBonusFor(me.Relics.Where(r => !r.IsUsedUp).Select(r => r.GetType().Name)));
        Try(() => ctx.InShop = run.CurrentMapPointHistoryEntry?.HasRoomOfType(RoomType.Shop) == true);
        Try(() =>
        {
            var boots = me.GetRelic<MegaCrit.Sts2.Core.Models.Relics.WingedBoots>();
            ctx.FlightCharges = boots is null || boots.IsUsedUp ? 0 : Math.Clamp(3 - boots.TimesUsed, 0, 3);
        });
        Try(() =>
        {
            // Visited rooms of this act: any step that does not follow a map line was a flight. Survives reloads.
            var visited = run.VisitedMapCoords;
            int used = 0;
            for (int i = 1; i < visited.Count; i++)
            {
                var prev = run.Map.GetPoint(visited[i - 1]);
                var cur = run.Map.GetPoint(visited[i]);
                if (prev is not null && cur is not null && !prev.Children.Contains(cur)) used++;
            }
            ctx.FlightsUsedThisAct = Math.Clamp(used, 0, 3);
        });
        return ctx;
    }

    public static float PotionValue(PotionModel potion, RouteSettings s)
    {
        string id = potion.Id.Entry;
        foreach (var kv in s.PotionValueOverrides)
            if (string.Equals(kv.Key, id, StringComparison.OrdinalIgnoreCase) || string.Equals(kv.Key, potion.GetType().Name, StringComparison.Ordinal))
                return kv.Value;
        return s.PotionRarityValues.TryGetValue(potion.Rarity.ToString(), out var v) ? v : 0.5f;
    }

    static void Try(Action read)
    {
        try { read(); }
        catch (Exception ex) { Plugin.Log("state read: " + ex.Message); }
    }

    public static NodeKind Kind(MapPointType t) => t switch
    {
        MapPointType.Monster => NodeKind.Monster,
        MapPointType.Elite => NodeKind.Elite,
        MapPointType.RestSite => NodeKind.RestSite,
        MapPointType.Shop => NodeKind.Shop,
        MapPointType.Treasure => NodeKind.Treasure,
        MapPointType.Unknown => NodeKind.Unknown,
        MapPointType.Boss => NodeKind.Boss,
        _ => NodeKind.Other,
    };
}

/// <summary>
/// Recent-fight HP loss relative to the expected loss for that fight type, read from the run's own map history
/// (so it survives save and reload). Elites and normal fights are compared against their own expectation.
/// </summary>
public static class StrengthHistory
{
    public static List<float> Ratios(RunState run, PlanParams p, int count = 3)
    {
        var ratios = new List<float>();
        ulong? me = null;
        try { me = GameContext.Me(run)?.NetId; } catch { }
        if (me is null) return ratios;
        var current = run.CurrentMapPointHistoryEntry;
        // A fight still in progress has not recorded its damage yet.
        bool unfinished = run.CurrentRoom is CombatRoom { IsPreFinished: false };
        var history = run.MapPointHistory;
        for (int act = history.Count - 1; act >= 0 && ratios.Count < count; act--)
        {
            var entries = history[act];
            for (int i = entries.Count - 1; i >= 0 && ratios.Count < count; i--)
            {
                var entry = entries[i];
                if (unfinished && ReferenceEquals(entry, current)) continue;
                bool elite = entry.Rooms.Any(r => r.RoomType == RoomType.Elite);
                bool monster = entry.Rooms.Any(r => r.RoomType == RoomType.Monster);
                if (!elite && !monster) continue; // bosses end the act and are not comparable
                var stats = entry.PlayerStats.FirstOrDefault(s => s.PlayerId == me.Value);
                if (stats is null) continue;
                float expected = PlanParams.ByAct(elite ? p.EliteHpLoss : p.MonsterHpLoss, act);
                if (expected <= 0) continue;
                ratios.Add((float)stats.DamageTaken / expected);
            }
        }
        ratios.Reverse(); // oldest first
        return ratios;
    }
}

public sealed class RouteResult
{
    public PathConfig Config = null!;
    public List<List<MapPoint>> Paths = new();
    /// <summary>For a Winged Boots plan: Jumped[i] is true when Paths[0][i] is reached by flying.</summary>
    public List<bool>? Jumped;
    /// <summary>Flights kept for later acts and their expected value (Winged Boots plans only).</summary>
    public int FlightsKept;
    public float FlightReserve;
    public float Score;
    public PlanResult? Plan;
}

/// <summary>Enumerates routes to the boss and picks the best one per enabled path config.</summary>
public static class RouteEngine
{
    const int MaxPaths = 200000;

    /// <summary>The act's map as plain data (for flight planning). <paramref name="index"/> maps game points to node ids.</summary>
    public static MapGraph ToGraph(ActMap map, out Dictionary<MapPoint, int> index)
    {
        var g = new MapGraph();
        var ids = new Dictionary<MapPoint, int>();
        index = ids;
        var points = map.GetAllMapPoints().Append(map.StartingMapPoint).Append(map.BossMapPoint);
        if (map.SecondBossMapPoint is { } second) points = points.Append(second);
        foreach (var point in points.Where(pt => pt is not null).Distinct())
            ids[point] = g.Add(point.coord.row, GameContext.Kind(point.PointType));
        foreach (var (point, id) in ids)
        {
            var node = g.Nodes[id];
            node.Children = point.Children.Where(ids.ContainsKey).OrderBy(c => c.coord).Select(c => ids[c]).ToArray();
            node.NoShopRoll = point.PointType == MapPointType.Unknown && point.Children.Count > 0
                              && point.Children.All(c => c.PointType == MapPointType.Shop);
        }
        return g;
    }

    /// <summary>
    /// With Winged Boots charges left, re-plans the Recommend route allowing flights and decides how many to spend
    /// in this act. The classic routes are left as they are (they cannot fly).
    /// </summary>
    /// <param name="allowedNow">Flights the player allows for the rest of this act (null = decide automatically).</param>
    public static void ApplyFlights(List<RouteResult> results, RunState run, MapPoint start, IReadOnlyCollection<MapCoord> pins,
        RouteSettings s, PlanContext ctx, int? allowedNow = null)
    {
        if (!s.Plan.FlightModule || ctx.FlightCharges <= 0 || run.Map is null) return;
        if (s.Recommend is not { Enabled: true } config) return;
        var graph = ToGraph(run.Map, out var ids);
        if (!ids.TryGetValue(start, out int from)) return;
        var points = ids.ToDictionary(kv => kv.Value, kv => kv.Key);
        var pinRows = new Dictionary<int, int>();
        foreach (var pin in pins)
        {
            var pt = run.Map.GetPoint(pin);
            if (pt is not null && ids.TryGetValue(pt, out int id)) pinRows[pin.row] = id;
        }
        int limit = allowedNow is int a ? Math.Clamp(a, 0, ctx.FlightCharges) : ctx.FlightCharges;
        var best = FlightPlanner.Plan(graph, from, limit, ctx, s.Plan, pinRows, exact: allowedNow is not null);
        FlightRoute? route;
        float reserve;
        if (allowedNow is null) (route, reserve) = FlightPlanner.Choose(best, ctx.ActIndex, ctx.FlightCharges, s.Plan);
        else
        {
            // The player chose: fly exactly `limit` times here, or as many as the map allows.
            route = best.LastOrDefault(r => r is not null);
            reserve = route is null ? 0f : FlightPlanner.Reserve(ctx.ActIndex, ctx.FlightCharges - route.Jumps, s.Plan);
        }
        if (route is null) return;
        var rec = results.FirstOrDefault(r => r.Config.Dynamic);
        if (rec is null) results.Insert(0, rec = new RouteResult { Config = config }); // pins only reachable by flying
        rec.FlightsKept = ctx.FlightCharges - route.Jumps;
        rec.FlightReserve = reserve;
        if (route.Jumps == 0 && rec.Paths.Count > 0) return; // walking is best: keep the exact no-flight route
        rec.Paths = new List<List<MapPoint>> { route.Nodes.Select(i => points[i]).ToList() };
        rec.Jumped = route.Jumped;
        rec.Plan = route.Plan;
        rec.Score = route.Plan.Score;
    }

    public static List<List<MapPoint>> AllPaths(MapPoint start) => Enumerate(start, Array.Empty<MapCoord>()).Paths;

    /// <summary>Coordinates reachable from <paramref name="start"/> (inclusive).</summary>
    public static HashSet<MapCoord> Reachable(MapPoint start)
    {
        var seen = new HashSet<MapCoord>();
        var stack = new Stack<MapPoint>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node.coord)) continue;
            foreach (var child in node.Children) stack.Push(child);
        }
        return seen;
    }

    /// <summary>
    /// Routes to the boss that pass every pin. Branches that can no longer reach a pending pin are pruned, so
    /// pins keep working on maps with too many routes to list. <c>Truncated</c> means the listing hit the cap.
    /// </summary>
    public static (List<List<MapPoint>> Paths, bool Truncated) Enumerate(MapPoint start, IReadOnlyCollection<MapCoord> pins)
    {
        var all = new List<List<MapPoint>>();
        var current = new List<MapPoint>();
        var pending = pins.OrderBy(c => c.row).ToList();
        var reach = new Dictionary<MapPoint, HashSet<MapCoord>>();
        HashSet<MapCoord> ReachOf(MapPoint node)
        {
            if (!reach.TryGetValue(node, out var set))
            {
                set = new HashSet<MapCoord> { node.coord };
                foreach (var child in node.Children) set.UnionWith(ReachOf(child));
                reach[node] = set;
            }
            return set;
        }
        bool truncated = false;
        void Walk(MapPoint node, int nextPin)
        {
            if (all.Count >= MaxPaths) { truncated = true; return; }
            while (nextPin < pending.Count && pending[nextPin] == node.coord) nextPin++;
            for (int k = nextPin; k < pending.Count; k++)
                if (!ReachOf(node).Contains(pending[k])) return;
            current.Add(node);
            if (node.PointType == MapPointType.Boss) { if (nextPin == pending.Count) all.Add(new List<MapPoint>(current)); }
            else foreach (var child in node.Children.OrderBy(c => c.coord)) Walk(child, nextPin);
            current.RemoveAt(current.Count - 1);
        }
        Walk(start, 0);
        return (all, truncated);
    }

    public static List<RouteResult> Compute(List<List<MapPoint>> paths, RouteSettings s, PlanContext ctx)
    {
        var results = new List<RouteResult>();
        if (paths.Count == 0) return results;
        var kinds = paths.Select(path => (IReadOnlyList<NodeKind>)path.Select(p => GameContext.Kind(p.PointType)).ToList()).ToList();
        // Game rule: an unknown room whose next rooms are all shops never rolls a shop.
        var noShop = paths.Select(path => (IReadOnlyList<bool>)path.Select(p => p.PointType == MapPointType.Unknown
            && p.Children.Count > 0 && p.Children.All(c => c.PointType == MapPointType.Shop)).ToList()).ToList();
        foreach (var config in s.Paths.Where(c => c.Enabled))
        {
            float best = float.NegativeInfinity;
            var bestPaths = new List<List<MapPoint>>();
            PlanResult? bestPlan = null;
            for (int i = 0; i < paths.Count; i++)
            {
                PlanResult? plan = null;
                float score;
                if (config.Dynamic)
                {
                    plan = Planner.Evaluate(kinds[i], ctx, s.Plan, noShop[i]);
                    score = plan.Score;
                }
                else score = config.CalculateScore(paths[i], s.ExpertMode);

                if (score > best + 1e-4f)
                {
                    best = score;
                    bestPaths.Clear();
                    bestPaths.Add(paths[i]);
                    bestPlan = plan;
                }
                else if (Math.Abs(score - best) <= 1e-4f) bestPaths.Add(paths[i]);
            }
            if (!s.HighlightAll && bestPaths.Count > 1) bestPaths.RemoveRange(1, bestPaths.Count - 1);
            results.Add(new RouteResult { Config = config, Paths = bestPaths, Score = best, Plan = bestPlan });
        }
        return results;
    }
}
