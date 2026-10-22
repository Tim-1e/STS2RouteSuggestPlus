using System;
using System.Collections.Generic;
using System.Linq;

namespace RouteSuggestPlus;

/// <summary>Room kinds the planner understands. Kept independent of game types so it can be tested offline.</summary>
public enum NodeKind { Other, Monster, Elite, RestSite, Shop, Treasure, Unknown, Boss }

/// <summary>Snapshot of the run when planning starts. Built from live game state by <see cref="GameContext"/>.</summary>
public sealed class PlanContext
{
    public int ActIndex;
    public int Hp = 80;
    public int MaxHp = 80;
    public int Gold = 99;
    public int RemovalCost = 75;
    public int RemovalStep = 25;
    public bool Poverty;
    /// <summary>Value of each held potion (already rarity-weighted).</summary>
    public List<float> Potions = new();
    public int PotionSlots = 3;
    /// <summary>Current potion reward chance for a normal fight (game pity system).</summary>
    public float PotionChance = 0.4f;
    /// <summary>Current unknown-room odds; event takes the remainder.</summary>
    public float UnknownMonster = 0.1f, UnknownTreasure = 0.02f, UnknownShop = 0.03f;
    public int RestHeal = 24;
    /// <summary>Winged Boots flights left (0 when the relic is absent or used up).</summary>
    public int FlightCharges;
    /// <summary>Extra rest-site value from relics (Miniature Tent and rest-site relics); added to every rest site.</summary>
    public float RestBonus;
    /// <summary>Flights already taken in this act (moves that did not follow a map line).</summary>
    public int FlightsUsedThisAct;
    /// <summary>The room the player is in turned out to be a shop (also when it was an unknown room).</summary>
    public bool InShop;
    /// <summary>Multiplier on expected HP loss from recent fights (1 = average).</summary>
    public float Strength = 1f;
    /// <summary>Recent fight samples (actual / expected HP loss), oldest first; informational.</summary>
    public List<float> StrengthSamples = new();
}

/// <summary>Tunable parameters. Every module can be switched off independently.</summary>
public sealed class PlanParams
{
    public bool ShopModule = true, PotionModule = true, ActModule = true, HpModule = true, StrengthModule = true;
    /// <summary>Plan Winged Boots flights when the player has charges left.</summary>
    public bool FlightModule = true;
    /// <summary>
    /// Rest-site relics. With Miniature Tent every option can be taken: the tent adds RestTentBonus (sleep and smith
    /// both), each other rest-site relic adds RestRelicBonus. Without the tent only one option is chosen, so relics
    /// add RestRelicBonusNoTent (0 by default).
    /// </summary>
    public float RestTentBonus = 2f, RestRelicBonus = 1f, RestRelicBonusNoTent = 0f;
    /// <summary>Score cost of each flight, so marginal flights are not taken.</summary>
    public float FlightCost = 1.5f;
    /// <summary>Parameter set version; older saved configs are migrated in <see cref="Normalize"/>.</summary>
    public int? ParamsVersion;
    public const int CurrentParamsVersion = 2;
    /// <summary>
    /// Expected route gain of holding r flights at the start of an act, index act * 4 + r (act 0..2, r 0..3).
    /// Measured on 1200 maps from the game's generator (tests/FlightStudy.cs); used to decide how many flights to keep.
    /// </summary>
    public float[] FlightReserve = { 0f, 5.63f, 9.69f, 12.69f, 0f, 4.86f, 7.9f, 9.82f, 0f, 3.73f, 4.92f, 5.43f };

    // Base value of each room (shared with the upstream weight table; Shop is replaced when ShopModule is on).
    public Dictionary<NodeKind, float> Weights = DefaultWeights();

    // Shop: value grows linearly from ShopGoldMin to ShopGoldFull; a pricier card removal discounts the whole shop.
    public float ShopValueMax = 8f, ShopGoldMin = 100f, ShopGoldFull = 300f, ShopSpend = 325f;
    public float RemovalCostBase = 75f, RemovalCostWorst = 175f, RemovalDiscount = 0.3f;
    public float MonsterGold = 15f, EliteGold = 40f, PovertyGoldMultiplier = 0.75f;

    // Potions: an elite consumes up to PotionPerElite points; drops into a full belt are wasted.
    public float PotionPerElite = 1f, PotionEliteBonus = 2f, PotionDamageReduction = 0.4f;
    public float PotionDropValue = 0.65f, PotionGainWeight = 1.5f, EliteDropBonus = 0.125f;

    // Act: elite reward multiplier per act.
    public float[] ActEliteMultiplier = { 1.3f, 1.0f, 0.8f };

    // HP: loss on a bad draw = p85 of damage_taken in 240 single-player A10 wins (profile 3).
    // Safety lines are absolute HP per act; dropping below costs a soft, quadratic penalty. Kept low weight on purpose.
    public float[] MonsterHpLoss = { 9, 13, 13 };
    public float[] EliteHpLoss = { 25, 28, 23 };
    public float[] SafeHp = { 10, 20, 30 };
    public float[] BossSafeHp = { 20, 25, 40 };
    /// <summary>Penalty per (10 HP below the line)^2.</summary>
    public float HpWeight = 0.5f, HpPenaltyScale = 1.5f, DeathPenalty = 30f;

    // Strength: how much recent fights move expected HP loss.
    public float StrengthInfluence = 0.4f, StrengthMin = 0.7f, StrengthMax = 1.4f;

    public float Weight(NodeKind kind) => Weights.TryGetValue(kind, out var w) ? w : 0f;
    /// <summary>Relics that add to rest sites (Miniature Tent is counted separately).</summary>
    public static readonly string[] RestRelics =
    {
        "PumpkinCandle", "Girya", "MeatCleaver", "PaelsGrowth", "Shovel", "DreamCatcher",
        "RegalPillow", "StoneHumidifier", "TinyMailbox", "EternalFeather",
    };

    /// <summary>Rest-site bonus for the given relic class names.</summary>
    public float RestBonusFor(IEnumerable<string> relics)
    {
        var held = relics.ToHashSet();
        int others = RestRelics.Count(held.Contains);
        return held.Contains("MiniatureTent") ? RestTentBonus + RestRelicBonus * others : RestRelicBonusNoTent * others;
    }

    public static float ByAct(float[] values, int act) => values.Length == 0 ? 0 : values[Math.Clamp(act, 0, values.Length - 1)];

    static Dictionary<NodeKind, float> DefaultWeights() => new()
    {
        [NodeKind.RestSite] = 4, [NodeKind.Treasure] = 2, [NodeKind.Shop] = 2, [NodeKind.Monster] = -1,
        [NodeKind.Elite] = 4, [NodeKind.Unknown] = 1, [NodeKind.Boss] = 0,
    };

    /// <summary>Repairs values from a hand-edited config. Returns the names of fields that were reset to defaults.</summary>
    public List<string> Normalize()
    {
        if (ParamsVersion is null || ParamsVersion < CurrentParamsVersion)
        {
            // v2: flights now carry a cost (FlightCost); the reserve table was re-measured with it.
            var fresh = new PlanParams();
            FlightReserve = (float[])fresh.FlightReserve.Clone();
            ParamsVersion = CurrentParamsVersion;
        }
        var reset = new List<string>();
        var d = new PlanParams();
        float[] Arr(float[]? v, float[] def, string name, float min, float max)
        {
            if (v is { Length: 3 } && v.All(x => float.IsFinite(x) && x >= min && x <= max)) return v;
            reset.Add(name);
            return (float[])def.Clone();
        }
        float Num(float v, float def, string name, float min, float max)
        {
            if (float.IsFinite(v) && v >= min && v <= max) return v;
            reset.Add(name);
            return def;
        }
        ActEliteMultiplier = Arr(ActEliteMultiplier, d.ActEliteMultiplier, "act_elite_multiplier", 0, 5);
        MonsterHpLoss = Arr(MonsterHpLoss, d.MonsterHpLoss, "monster_hp_loss", 0, 200);
        EliteHpLoss = Arr(EliteHpLoss, d.EliteHpLoss, "elite_hp_loss", 0, 200);
        SafeHp = Arr(SafeHp, d.SafeHp, "safe_hp", 0, 999);
        BossSafeHp = Arr(BossSafeHp, d.BossSafeHp, "boss_safe_hp", 0, 999);
        if (FlightReserve is not { Length: 12 } || !FlightReserve.All(x => float.IsFinite(x) && x >= 0 && x <= 100))
        {
            FlightReserve = (float[])d.FlightReserve.Clone();
            reset.Add("flight_reserve");
        }
        if (Weights is null) { Weights = DefaultWeights(); reset.Add("weights"); }
        foreach (var (k, v) in DefaultWeights())
            if (!Weights.TryGetValue(k, out var w) || !float.IsFinite(w)) Weights[k] = v;
        ShopValueMax = Num(ShopValueMax, d.ShopValueMax, "shop_value_max", 0, 100);
        ShopGoldMin = Num(ShopGoldMin, d.ShopGoldMin, "shop_gold_min", 0, 2000);
        ShopGoldFull = Num(ShopGoldFull, Math.Max(d.ShopGoldFull, ShopGoldMin + 1), "shop_gold_full", ShopGoldMin + 1, 5000);
        ShopSpend = Num(ShopSpend, d.ShopSpend, "shop_spend", 0, 5000);
        RemovalCostBase = Num(RemovalCostBase, d.RemovalCostBase, "removal_cost_base", 0, 2000);
        RemovalCostWorst = Num(RemovalCostWorst, Math.Max(d.RemovalCostWorst, RemovalCostBase + 1), "removal_cost_worst", RemovalCostBase + 1, 5000);
        RemovalDiscount = Num(RemovalDiscount, d.RemovalDiscount, "removal_discount", 0, 1);
        MonsterGold = Num(MonsterGold, d.MonsterGold, "monster_gold", 0, 1000);
        EliteGold = Num(EliteGold, d.EliteGold, "elite_gold", 0, 1000);
        PovertyGoldMultiplier = Num(PovertyGoldMultiplier, d.PovertyGoldMultiplier, "poverty_gold_multiplier", 0, 2);
        PotionPerElite = Num(PotionPerElite, d.PotionPerElite, "potion_per_elite", 0.01f, 10);
        PotionEliteBonus = Num(PotionEliteBonus, d.PotionEliteBonus, "potion_elite_bonus", 0, 100);
        PotionDamageReduction = Num(PotionDamageReduction, d.PotionDamageReduction, "potion_damage_reduction", 0, 1);
        PotionDropValue = Num(PotionDropValue, d.PotionDropValue, "potion_drop_value", 0, 10);
        PotionGainWeight = Num(PotionGainWeight, d.PotionGainWeight, "potion_gain_weight", 0, 100);
        EliteDropBonus = Num(EliteDropBonus, d.EliteDropBonus, "elite_drop_bonus", 0, 1);
        HpWeight = Num(HpWeight, d.HpWeight, "hp_weight", 0, 10);
        FlightCost = Num(FlightCost, d.FlightCost, "flight_cost", 0, 20);
        RestTentBonus = Num(RestTentBonus, d.RestTentBonus, "rest_tent_bonus", 0, 20);
        RestRelicBonus = Num(RestRelicBonus, d.RestRelicBonus, "rest_relic_bonus", 0, 20);
        RestRelicBonusNoTent = Num(RestRelicBonusNoTent, d.RestRelicBonusNoTent, "rest_relic_bonus_no_tent", 0, 20);
        HpPenaltyScale = Num(HpPenaltyScale, d.HpPenaltyScale, "hp_penalty_scale", 0, 100);
        DeathPenalty = Num(DeathPenalty, d.DeathPenalty, "death_penalty", 0, 1000);
        StrengthInfluence = Num(StrengthInfluence, d.StrengthInfluence, "strength_influence", 0, 2);
        StrengthMin = Num(StrengthMin, d.StrengthMin, "strength_min", 0.1f, 1);
        StrengthMax = Num(StrengthMax, d.StrengthMax, "strength_max", 1, 5);
        return reset;
    }
}

/// <summary>Per-path explanation, shown in the settings panel and the log.</summary>
public sealed class PlanResult
{
    public float Score;
    public float HpPenalty;
    public float EndHp;
    public float EndGold;
    /// <summary>Expected potions picked up (drops into a full belt excluded).</summary>
    public float PotionsGained;
    public int Elites;
    public int Shops;
    public int Rests;
    /// <summary>Rest sites where the best plan heals rather than upgrades.</summary>
    public int Heals;
}

/// <summary>
/// Walks a candidate route node by node in expectation, as one averaged state (no branching): unknown rooms
/// add each outcome's share of the effect, rest sites are assumed to heal; the potion belt is a joint distribution over (potions held, pity chance) so a full belt wastes drops exactly.
/// </summary>
public static class Planner
{
    /// <summary>Planner state after some prefix of a route. Opaque outside the planner; cheap to copy.</summary>
    public sealed class State
    {
        internal float Hp, Gold, Removal, Avg;
        internal float Um, Ut, Us;
        internal float Score, Penalty, Gained;
        internal int Heals;
        /// <summary>Probability that the previous room was a shop (the game never rolls a shop right after one).</summary>
        internal float ShopBefore;
        /// <summary>(potions held, pity chance in tenths) → probability.</summary>
        internal Dictionary<(int n, int c), float> Belt = new();

        public float Net => Score - Penalty;
        public float EndHp => Hp;

        internal State Clone()
        {
            var s = (State)MemberwiseClone();
            s.Belt = new Dictionary<(int n, int c), float>(Belt);
            return s;
        }
    }

    /// <param name="noShopRoll">Per node: an unknown room that cannot roll a shop because all its children are shops.</param>
    public static PlanResult Evaluate(IReadOnlyList<NodeKind> path, PlanContext ctx, PlanParams p, IReadOnlyList<bool>? noShopRoll = null)
    {
        var start = Begin(path.Count > 0 ? path[0] : NodeKind.Other, ctx, p);
        return Summarize(Walk(path, noShopRoll, 1, start, ctx, p), path);
    }

    /// <summary>State in the room the player is in (<paramref name="current"/> is never scored).</summary>
    public static State Begin(NodeKind current, PlanContext ctx, PlanParams p)
    {
        var start = new State
        {
            Hp = Math.Clamp(ctx.Hp, 0, ctx.MaxHp),
            Gold = ctx.Gold,
            Removal = ctx.RemovalCost,
            Avg = ctx.Potions.Count > 0 ? Math.Max(0.01f, ctx.Potions.Average()) : p.PotionDropValue,
            Um = ctx.UnknownMonster, Ut = ctx.UnknownTreasure, Us = ctx.UnknownShop,
            ShopBefore = ctx.InShop || current == NodeKind.Shop ? 1f : 0f,
        };
        int held = Math.Clamp(ctx.Potions.Count, 0, Math.Max(0, ctx.PotionSlots));
        start.Belt[(held, Tenths(ctx.PotionChance))] = 1f;
        return start;
    }

    /// <summary>A new state one room further; <paramref name="s"/> is left untouched.</summary>
    public static State Advance(State s, NodeKind kind, bool noShopRoll, PlanContext ctx, PlanParams p)
    {
        var next = s.Clone();
        Step(kind, next, ctx, p, noShopRoll);
        next.ShopBefore = kind == NodeKind.Shop ? 1f : kind == NodeKind.Unknown ? next.ShopBefore : 0f;
        return next;
    }

    /// <summary>Subtracts a fixed cost (e.g. a Winged Boots flight) from a state; returns the same state.</summary>
    public static State Charge(State s, float cost)
    {
        s.Score -= cost;
        return s;
    }

    /// <summary>Summary for a finished route; <paramref name="path"/> includes the starting room.</summary>
    public static PlanResult Summarize(State end, IReadOnlyList<NodeKind> path)
    {
        var r = new PlanResult
        {
            Score = end.Net,
            HpPenalty = end.Penalty,
            EndHp = end.Hp,
            EndGold = end.Gold,
            PotionsGained = end.Gained,
            Heals = end.Heals,
        };
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i] == NodeKind.Elite) r.Elites++;
            else if (path[i] == NodeKind.Shop) r.Shops++;
            else if (path[i] == NodeKind.RestSite) r.Rests++;
        }
        return r;
    }

    // Pity state in tenths. The game lets it drop below zero (only the roll itself is clamped to 0..1).
    const int PityMin = -10, PityMax = 10;
    static int Tenths(float chance) => Math.Clamp((int)MathF.Round(chance * 10f), PityMin, PityMax);

    // path[0] is the room the player is in (or the act start); it is never re-scored.
    static State Walk(IReadOnlyList<NodeKind> path, IReadOnlyList<bool>? noShopRoll, int index, State s, PlanContext ctx, PlanParams p)
    {
        for (int i = index; i < path.Count; i++)
        {
            var kind = path[i];
            bool noShop = noShopRoll is not null && i < noShopRoll.Count && noShopRoll[i];
            Step(kind, s, ctx, p, noShop);
            s.ShopBefore = kind == NodeKind.Shop ? 1f : kind == NodeKind.Unknown ? s.ShopBefore : 0f;
        }
        return s;
    }

    static void Step(NodeKind kind, State s, PlanContext ctx, PlanParams p, bool noShop)
    {
        switch (kind)
        {
            case NodeKind.Monster:
                s.Score += p.Weight(NodeKind.Monster);
                Fight(s, false, ctx, p);
                break;
            case NodeKind.Elite:
                s.Score += p.Weight(NodeKind.Elite) * (p.ActModule ? PlanParams.ByAct(p.ActEliteMultiplier, ctx.ActIndex) : 1f);
                Fight(s, true, ctx, p);
                break;
            case NodeKind.Shop:
                Shop(s, ctx, p);
                break;
            case NodeKind.Treasure:
                s.Score += p.Weight(NodeKind.Treasure);
                break;
            case NodeKind.RestSite:
                s.Score += p.Weight(NodeKind.RestSite) + ctx.RestBonus + (p.ActModule && ctx.ActIndex >= 2 ? 1f : 0f);
                // Assume the player sleeps. Healing is not scored, so arriving hurt is never rewarded.
                if (p.HpModule && s.Hp < ctx.MaxHp) { s.Hp = Math.Min(ctx.MaxHp, s.Hp + ctx.RestHeal); s.Heals++; }
                break;
            case NodeKind.Unknown:
                Unknown(s, ctx, p, noShop);
                break;
            case NodeKind.Boss:
                s.Score += p.Weight(NodeKind.Boss);
                CheckHp(s, PlanParams.ByAct(p.BossSafeHp, ctx.ActIndex), ctx, p);
                break;
        }
    }

    /// <summary>Gold curve (0 below ShopGoldMin, full at ShopGoldFull) times a discount for an expensive removal.</summary>
    public static float ShopValue(float gold, float removalCost, PlanParams p)
    {
        if (!p.ShopModule) return p.Weight(NodeKind.Shop);
        float curve = Math.Clamp((gold - p.ShopGoldMin) / Math.Max(1f, p.ShopGoldFull - p.ShopGoldMin), 0f, 1f);
        float pricey = Math.Clamp((removalCost - p.RemovalCostBase) / Math.Max(1f, p.RemovalCostWorst - p.RemovalCostBase), 0f, 1f);
        return p.ShopValueMax * curve * (1f - p.RemovalDiscount * pricey);
    }

    static void Shop(State s, PlanContext ctx, PlanParams p, float w = 1f)
    {
        s.Score += w * ShopValue(s.Gold, s.Removal, p);
        if (!p.ShopModule) return;
        if (s.Gold >= s.Removal) s.Removal += w * ctx.RemovalStep; // an expected removal raises the next price
        s.Gold -= w * Math.Min(s.Gold, p.ShopSpend);
    }

    static void Fight(State s, bool elite, PlanContext ctx, PlanParams p, float w = 1f)
    {
        float strength = p.StrengthModule ? Math.Clamp(ctx.Strength, p.StrengthMin, p.StrengthMax) : 1f;
        float baseLoss = PlanParams.ByAct(elite ? p.EliteHpLoss : p.MonsterHpLoss, ctx.ActIndex) * strength;
        float loss = baseLoss;
        if (p.PotionModule)
        {
            var next = new Dictionary<(int n, int c), float>();
            float expectedLoss = 0f, gained = 0f, heldAfter = 0f;
            int slots = Math.Max(0, ctx.PotionSlots);
            float avg = Math.Max(0.01f, s.Avg);
            foreach (var ((n, c), prob) in s.Belt)
            {
                int left = n;
                float branchLoss = baseLoss;
                // In act 3, keep potions for the boss instead of spending them on elites.
                if (elite && n > 0 && ctx.ActIndex < 2)
                {
                    float used = Math.Min(n * avg, p.PotionPerElite);
                    float share = used / p.PotionPerElite;
                    branchLoss *= 1f - p.PotionDamageReduction * share;
                    s.Score += w * prob * p.PotionEliteBonus * share;
                    left = Math.Max(0, n - Math.Max(1, (int)MathF.Ceiling(used / avg - 1e-4f)));
                }
                expectedLoss += prob * branchLoss;
                // The reward rolls even with a full belt (the potion just cannot be kept); pity moves either way.
                float roll = Math.Clamp(c / 10f + (elite ? p.EliteDropBonus : 0f), 0f, 1f);
                bool room = left < slots;
                Add(next, (room ? left + 1 : left, Math.Max(PityMin, c - 1)), w * prob * roll);
                Add(next, (left, Math.Min(PityMax, c + 1)), w * prob * (1f - roll));
                Add(next, (n, c), (1f - w) * prob); // the fight did not happen (unknown room rolled something else)
                if (room) gained += w * prob * roll;
                heldAfter += prob * (w * left + (1f - w) * n);
            }
            s.Belt = next;
            if (heldAfter + gained > 0) s.Avg = (heldAfter * avg + gained * p.PotionDropValue) / (heldAfter + gained);
            s.Gained += gained;
            s.Score += gained * p.PotionDropValue * p.PotionGainWeight;
            loss = expectedLoss;
        }
        s.Hp -= w * loss;
        s.Gold += w * (elite ? p.EliteGold : p.MonsterGold) * (ctx.Poverty ? p.PovertyGoldMultiplier : 1f);
        CheckHp(s, PlanParams.ByAct(p.SafeHp, ctx.ActIndex), ctx, p);
    }

    static void Add(Dictionary<(int n, int c), float> d, (int n, int c) key, float prob)
    {
        if (prob <= 0f) return;
        d[key] = d.TryGetValue(key, out var v) ? v + prob : prob;
    }

    static void CheckHp(State s, float safeHp, PlanContext ctx, PlanParams p)
    {
        if (!p.HpModule) return;
        float deficit = (Math.Min(safeHp, ctx.MaxHp) - s.Hp) / 10f;
        float pen = 0f;
        if (deficit > 0) pen += p.HpPenaltyScale * deficit * deficit;
        if (s.Hp <= 0) pen += p.DeathPenalty;
        s.Penalty += p.HpWeight * pen;
    }

    /// <summary>Every outcome starts from the same incoming state; the results are mixed by probability.</summary>
    static void Unknown(State s, PlanContext ctx, PlanParams p, bool noShop)
    {
        // Same order as the game's cumulative roll: monster, treasure, shop; event takes what is left.
        float m = Math.Clamp(s.Um, 0f, 1f);
        float t = Math.Clamp(s.Ut, 0f, 1f - m);
        // The game never rolls a shop right after a shop, or when every next room is a shop.
        bool shopBlocked = noShop || s.ShopBefore >= 1f;
        float sh = shopBlocked ? 0f : Math.Clamp(s.Us, 0f, 1f - m - t) * (1f - s.ShopBefore);
        float ev = Math.Max(0f, 1f - m - t - sh);

        // One averaged state, no branching: each outcome adds its share of the effect. The shop is applied
        // before the fight so it sees the incoming gold, not the fight's reward.
        if (sh > 0) Shop(s, ctx, p, sh);
        if (t > 0) s.Score += t * p.Weight(NodeKind.Treasure);
        if (ev > 0) s.Score += ev * p.Weight(NodeKind.Unknown);
        if (m > 0) { s.Score += m * p.Weight(NodeKind.Monster); Fight(s, false, ctx, p, m); }

        // Odds of the types that were not rolled grow by their base; a rolled type resets; a blocked type is untouched.
        s.Um = m * 0.1f + (1f - m) * (s.Um + 0.1f);
        s.Ut = t * 0.02f + (1f - t) * (s.Ut + 0.02f);
        if (!shopBlocked) s.Us = sh * 0.03f + (1f - sh) * (s.Us + 0.03f);
        s.ShopBefore = sh;
    }

    /// <summary>Recent-fight strength: weighted ratio of actual to expected HP loss, shrunk toward 1 and clamped.</summary>
    public static float StrengthFactor(IReadOnlyList<float> ratiosOldestFirst, PlanParams p, float shrink = 0.5f)
    {
        float[] w = { 0.5f, 0.3f, 0.2f };
        float sum = 0f, wsum = 0f;
        for (int k = 0; k < w.Length && k < ratiosOldestFirst.Count; k++)
        {
            float ratio = Math.Clamp(ratiosOldestFirst[ratiosOldestFirst.Count - 1 - k], 0f, 3f);
            sum += w[k] * ratio;
            wsum += w[k];
        }
        float avg = (sum + shrink) / (wsum + shrink);
        return Math.Clamp(1f + p.StrengthInfluence * (avg - 1f), p.StrengthMin, p.StrengthMax);
    }
}
