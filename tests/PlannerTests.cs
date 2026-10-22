using System;
using System.Collections.Generic;
using System.Linq;
using RouteSuggestPlus;

public static class PlannerTests
{
    static int _pass, _fail;
    static readonly List<string> Lines = new();

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Lines.Add($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
    }

    static NodeKind[] P(params NodeKind[] k) => new[] { NodeKind.Other }.Concat(k).Concat(new[] { NodeKind.Boss }).ToArray();
    const NodeKind M = NodeKind.Monster, E = NodeKind.Elite, R = NodeKind.RestSite, S = NodeKind.Shop, T = NodeKind.Treasure, U = NodeKind.Unknown;

    static float Score(NodeKind[] path, PlanContext c, PlanParams p) => Planner.Evaluate(path, c, p).Score;
    static PlanContext Ctx(Action<PlanContext>? f = null) { var c = new PlanContext(); f?.Invoke(c); return c; }

    public static string Run()
    {
        var p = new PlanParams();
        var shopPath = P(M, S, M, R);
        var noShop = P(M, M, M, R);

        // Shop & gold
        float poorShop = Score(shopPath, Ctx(c => c.Gold = 40), p) - Score(noShop, Ctx(c => c.Gold = 40), p);
        float richShop = Score(shopPath, Ctx(c => c.Gold = 320), p) - Score(noShop, Ctx(c => c.Gold = 320), p);
        Check("rich player values the shop more than a poor one", richShop > poorShop + 3, $"poor {poorShop:0.00} rich {richShop:0.00}");
        float shop200 = Planner.Evaluate(P(S), Ctx(c => c.Gold = 200), p).Score;
        float shop300 = Planner.Evaluate(P(S), Ctx(c => c.Gold = 300), p).Score;
        float shop90 = Planner.Evaluate(P(S), Ctx(c => { c.Gold = 90; c.RemovalCost = 175; }), p).Score;
        Check("shop below 100 gold is worth ~0", Math.Abs(shop90) < 0.01f, $"{shop90:0.00}");
        Check("shop value roughly linear 200→300", shop300 > shop200 && shop200 > 3, $"200:{shop200:0.00} 300:{shop300:0.00}");
        float cheapRemoval = Planner.Evaluate(P(S), Ctx(c => { c.Gold = 200; c.RemovalCost = 75; }), p).Score;
        float dearRemoval = Planner.Evaluate(P(S), Ctx(c => { c.Gold = 200; c.RemovalCost = 150; }), p).Score;
        Check("higher removal price lowers shop value", cheapRemoval > dearRemoval, $"75:{cheapRemoval:0.00} 150:{dearRemoval:0.00}");
        float twoShops = Planner.Evaluate(P(S, S), Ctx(c => c.Gold = 300), p).Score;
        Check("second shop in a row adds little", twoShops - shop300 < 1.5f, $"one {shop300:0.00} two {twoShops:0.00}");
        var pOff = new PlanParams { ShopModule = false };
        Check("shop module off uses base weight", Math.Abs(Planner.Evaluate(P(S), Ctx(c => c.Gold = 0), pOff).Score - pOff.Weight(NodeKind.Shop)) < 0.01f);

        // Potions
        var elitePath = P(M, E, R);
        var safePath = P(M, M, R);
        float noPot = Score(elitePath, Ctx(), p) - Score(safePath, Ctx(), p);
        float twoGood = Score(elitePath, Ctx(c => c.Potions = new() { 1f, 1f }), p) - Score(safePath, Ctx(c => c.Potions = new() { 1f, 1f }), p);
        Check("potions make the elite route more attractive", twoGood > noPot + 1.2f, $"none {noPot:0.00} two good {twoGood:0.00}");
        float common2 = Planner.Evaluate(P(E), Ctx(c => c.Potions = new() { 0.5f, 0.5f }), p).Score;
        float good1 = Planner.Evaluate(P(E), Ctx(c => c.Potions = new() { 1f }), p).Score;
        Check("two common potions ≈ one good potion for an elite", Math.Abs(common2 - good1) < 0.3f, $"2 common {common2:0.00} 1 good {good1:0.00}");
        float fullBelt = Planner.Evaluate(P(M, M), Ctx(c => c.Potions = new() { 0.5f, 0.5f, 0.5f }), p).Score;
        float emptyBelt = Planner.Evaluate(P(M, M), Ctx(), p).Score;
        Check("drops into a full belt are worthless", emptyBelt > fullBelt + 0.3f, $"empty {emptyBelt:0.00} full {fullBelt:0.00}");

        // Act
        float a1 = Planner.Evaluate(P(E), Ctx(c => { c.ActIndex = 0; c.Hp = 80; }), p).Score;
        float a3 = Planner.Evaluate(P(E), Ctx(c => { c.ActIndex = 2; c.Hp = 80; }), p).Score;
        Check("elite worth more in act 1 than act 3", a1 > a3, $"a1 {a1:0.00} a3 {a3:0.00}");

        // HP: absolute safety lines 10/20/30
        float hiHp = Score(elitePath, Ctx(c => c.Hp = 70), p) - Score(safePath, Ctx(c => c.Hp = 70), p);
        float loHp = Score(elitePath, Ctx(c => c.Hp = 22), p) - Score(safePath, Ctx(c => c.Hp = 22), p);
        Check("low HP pushes away from elites", loHp < hiHp - 0.5f, $"hp70 {hiHp:0.00} hp22 {loHp:0.00}");
        var r2 = Planner.Evaluate(P(M), Ctx(c => { c.ActIndex = 1; c.Hp = 40; }), p);
        Check("act 2 above 20 HP after a fight has no penalty", r2.HpPenalty == 0, $"end hp {r2.EndHp}");
        var r3 = Planner.Evaluate(P(E), Ctx(c => { c.ActIndex = 2; c.Hp = 40; c.MaxHp = 90; }), p);
        Check("act 3 elite from 40 HP drops below 30 → penalty", r3.HpPenalty > 0, $"end hp {r3.EndHp:0} pen {r3.HpPenalty:0.00}");
        float restFirst = Planner.Evaluate(P(R, E, M), Ctx(c => { c.ActIndex = 1; c.Hp = 30; }), p).HpPenalty;
        float noRest = Planner.Evaluate(P(M, E, M), Ctx(c => { c.ActIndex = 1; c.Hp = 30; }), p).HpPenalty;
        Check("resting before an elite when hurt avoids penalties", restFirst < noRest, $"rest {restFirst:0.00} no rest {noRest:0.00}");
        float hurtGain = Score(P(E, R), Ctx(c => c.Hp = 80), p) - Score(P(M, R), Ctx(c => c.Hp = 80), p);
        float noHealGain = Score(P(E, T), Ctx(c => c.Hp = 80), p) - Score(P(M, T), Ctx(c => c.Hp = 80), p) + p.Weight(T) - p.Weight(R);
        Check("taking damage before a rest is not rewarded", hurtGain <= noHealGain + (p.Weight(R) - p.Weight(T)) + 1e-3f, $"{hurtGain:0.00} vs {noHealGain + p.Weight(R) - p.Weight(T):0.00}");

        // Strength
        float sGood = Planner.StrengthFactor(new List<float> { 0.3f, 0.2f, 0.1f }, p);
        float sBad = Planner.StrengthFactor(new List<float> { 1f, 1.5f, 2.5f }, p);
        float sNone = Planner.StrengthFactor(new List<float>(), p);
        float sLastHeavy = Planner.StrengthFactor(new List<float> { 1f, 1f, 2f }, p);
        float sFirstHeavy = Planner.StrengthFactor(new List<float> { 2f, 1f, 1f }, p);
        Check("strength factor neutral with no fights", Math.Abs(sNone - 1f) < 1e-4, $"{sNone:0.000}");
        Check("strong deck lowers, weak deck raises expected loss", sGood < 1 && sBad > 1, $"good {sGood:0.000} bad {sBad:0.000}");
        Check("last fight weighs most", sLastHeavy > sFirstHeavy, $"last {sLastHeavy:0.000} first {sFirstHeavy:0.000}");
        Check("strength clamped", sBad <= p.StrengthMax && sGood >= p.StrengthMin);
        float weakLoss = Planner.Evaluate(P(E), Ctx(c => { c.Hp = 35; c.Strength = 1.4f; }), p).EndHp;
        float strongLoss = Planner.Evaluate(P(E), Ctx(c => { c.Hp = 35; c.Strength = 0.7f; }), p).EndHp;
        Check("strength scales HP loss", strongLoss > weakLoss, $"strong end {strongLoss:0} weak end {weakLoss:0}");

        // Unknown room uses live odds
        float u1 = Planner.Evaluate(P(U), Ctx(c => { c.UnknownMonster = 0.1f; }), p).Score;
        float u2 = Planner.Evaluate(P(U), Ctx(c => { c.UnknownMonster = 0.6f; }), p).Score;
        Check("unknown with high monster odds scores lower", u2 < u1, $"10% {u1:0.00} 60% {u2:0.00}");

        // Shop value = gold curve × removal discount
        Check("shop value formula", Math.Abs(Planner.ShopValue(200, 75, p) - 4f) < 1e-3 && Math.Abs(Planner.ShopValue(200, 175, p) - 2.8f) < 1e-3
              && Planner.ShopValue(99, 75, p) == 0 && Math.Abs(Planner.ShopValue(400, 75, p) - 8f) < 1e-3,
              $"{Planner.ShopValue(200, 75, p):0.00} / {Planner.ShopValue(200, 175, p):0.00}");

        // Rest site: assumed to heal; arriving hurt never ends better than arriving healthy
        var hurtRest = Planner.Evaluate(P(R, E), Ctx(c => { c.ActIndex = 1; c.Hp = 30; }), p);
        var fineRest = Planner.Evaluate(P(R, E), Ctx(c => { c.ActIndex = 1; c.Hp = 50; }), p);
        Check("rest heals and stays monotonic in HP", hurtRest.Heals == 1 && fineRest.Score >= hurtRest.Score && fineRest.EndHp >= hurtRest.EndHp,
              $"hp30 {hurtRest.Score:0.00}/{hurtRest.EndHp:0} hp50 {fineRest.Score:0.00}/{fineRest.EndHp:0}");

        // Unknown = one averaged state; effects mix linearly
        float mon = Planner.Evaluate(P(M), Ctx(), p).Score;
        float evt = p.Weight(U) + p.Weight(NodeKind.Boss);
        float half = Planner.Evaluate(P(U), Ctx(c => { c.UnknownMonster = 0.5f; c.UnknownTreasure = 0; c.UnknownShop = 0; }), p).Score;
        Check("unknown = probability mix of outcomes", Math.Abs(half - (0.5f * mon + 0.5f * evt)) < 1e-3, $"{half:0.000} vs {0.5f * mon + 0.5f * evt:0.000}");
        float shopThenU = Planner.Evaluate(P(S, U), Ctx(c => { c.Gold = 300; c.UnknownMonster = 0; c.UnknownTreasure = 0; c.UnknownShop = 1; }), p).Score;
        Check("no shop roll right after a shop", Math.Abs(shopThenU - (shop300 + p.Weight(U))) < 1e-3, $"{shopThenU:0.00} vs {shop300 + p.Weight(U):0.00}");

        float uOnlyShop = Planner.Evaluate(P(U), Ctx(c => { c.Gold = 300; c.UnknownMonster = 0; c.UnknownTreasure = 0; c.UnknownShop = 1; }), p,
            new[] { false, true, false }).Score;
        float inShop = Planner.Evaluate(P(U), Ctx(c => { c.Gold = 300; c.InShop = true; c.UnknownMonster = 0; c.UnknownTreasure = 0; c.UnknownShop = 1; }), p).Score;
        Check("no shop roll when all next rooms are shops, or from a rolled shop", Math.Abs(uOnlyShop - p.Weight(U)) < 1e-3 && Math.Abs(inShop - p.Weight(U)) < 1e-3,
              $"{uOnlyShop:0.00} / {inShop:0.00} vs {p.Weight(U):0.00}");

        // Potion belt: exact probability tree (1 slot, 40% chance, pity ±10%)
        var tree = Planner.Evaluate(P(M, M), Ctx(c => { c.PotionSlots = 1; c.PotionChance = 0.4f; }), p);
        Check("potion drops follow the exact tree with a full belt", Math.Abs(tree.PotionsGained - (0.4f + 0.6f * 0.5f)) < 1e-4, $"{tree.PotionsGained:0.0000} vs 0.7000");

        // Unknown → Unknown: a shop rolled by the first lowers the shop chance of the second
        var uu = Ctx(c => { c.Gold = 300; c.UnknownMonster = 0; c.UnknownTreasure = 0; c.UnknownShop = 0.5f; });
        float oneU = Planner.Evaluate(P(U), uu, p).Score, twoU = Planner.Evaluate(P(U, U), uu, p).Score;
        Check("second unknown cannot repeat the first one's shop", twoU - oneU < oneU, $"one {oneU:0.00} two {twoU:0.00}");
        // Odds over 1 are allocated in the game's order (monster, treasure, shop)
        float over = Planner.Evaluate(P(U), Ctx(c => { c.UnknownMonster = 0.7f; c.UnknownTreasure = 0.14f; c.UnknownShop = 0.21f; c.Gold = 0; }), p).Score;
        float expectOver = 0.7f * Planner.Evaluate(P(M), Ctx(c => c.Gold = 0), p).Score + 0.14f * p.Weight(T) + 0.16f * 0f;
        Check("odds over 1 follow the game's cumulative order", Math.Abs(over - expectOver) < 1e-3, $"{over:0.000} vs {expectOver:0.000}");
        // Elite potion use never credits more than the potions held
        var mixed = Ctx(c => { c.Potions = new() { 1f, 0.5f }; c.PotionChance = -1f; });
        var two = Planner.Evaluate(P(E, E), mixed, new PlanParams { EliteDropBonus = 0 });
        var none = Planner.Evaluate(P(E, E), Ctx(c => c.PotionChance = -1f), new PlanParams { EliteDropBonus = 0 });
        Check("potion value is conserved across elites", two.Score - none.Score <= (new PlanParams()).PotionEliteBonus * 1.5f + 1e-3,
              $"bonus {two.Score - none.Score:0.00}");

        // Config repair
        var broken = new PlanParams { SafeHp = new float[] { 10 }, MonsterHpLoss = null!, StrengthMin = 2f, Weights = null! };
        var reset = broken.Normalize();
        Check("broken config values are reset", broken.SafeHp.Length == 3 && broken.MonsterHpLoss.Length == 3 && broken.StrengthMin <= broken.StrengthMax
              && broken.Weights.Count > 0 && reset.Count >= 4, string.Join(",", reset));

        // Winged Boots: a toy map where the elites sit on a column the start cannot reach without flying
        var g = new MapGraph();
        int s0 = g.Add(0, NodeKind.Other);
        int m1 = g.Add(1, M), e1 = g.Add(1, E);
        int m2 = g.Add(2, M), e2 = g.Add(2, R);
        int boss = g.Add(3, NodeKind.Boss);
        g.Nodes[s0].Children = new[] { m1 };
        g.Nodes[m1].Children = new[] { m2 };
        g.Nodes[e1].Children = new[] { e2 };
        g.Nodes[m2].Children = new[] { boss };
        g.Nodes[e2].Children = new[] { boss };
        var fl = FlightPlanner.Plan(g, s0, 3, Ctx(), p);
        Check("no flights = only connected rooms", fl[0]!.Jumps == 0 && fl[0]!.Nodes.SequenceEqual(new[] { s0, m1, m2, boss }));
        Check("one flight reaches the elite column", fl[1]!.Jumps == 1 && fl[1]!.Nodes[1] == e1 && fl[1]!.Plan.Score > fl[0]!.Plan.Score,
              $"{string.Join(">", fl[1]!.Nodes)} {fl[1]!.Plan.Score:0.00} vs {fl[0]!.Plan.Score:0.00}");
        Check("extra flights unused when they do not help", fl[3]!.Jumps == 1);
        var forced = FlightPlanner.Plan(g, s0, 2, Ctx(), p, null, exact: true);
        Check("a chosen count is flown exactly, even when it adds little", forced[2] is { Jumps: 2 } && forced[1] is { Jumps: 1 } && forced[0] is { Jumps: 0 },
              forced[2] is null ? "none" : string.Join(">", forced[2]!.Nodes));
        var pinned = FlightPlanner.Plan(g, s0, 3, Ctx(), p, new Dictionary<int, int> { [2] = m2 });
        Check("flight routes respect pins", pinned[3]!.Nodes[2] == m2, string.Join(">", pinned[3]!.Nodes));
        var keep = new PlanParams { FlightReserve = new float[] { 0, 0, 0, 0, 0, 50, 60, 90, 0, 0, 0, 0 } };
        var (choice, reserve) = FlightPlanner.Choose(fl, 0, 3, keep);
        Check("a valuable later act keeps the flights", choice!.Jumps == 0 && reserve == 90, $"jumps {choice.Jumps} reserve {reserve}");
        var (spend, _) = FlightPlanner.Choose(fl, 0, 3, p);
        Check("with nothing to save for, the flight is used", spend!.Jumps == 1);

        // Old configs keep their weights; only the flight reserve table is re-measured
        var old = new PlanParams { ParamsVersion = null, FlightReserve = new float[] { 0, 7.08f, 12.57f, 17, 0, 6.28f, 10.71f, 13.99f, 0, 5.06f, 7.48f, 9 } };
        old.Normalize();
        Check("v1 configs migrate the reserve table only", old.Weight(NodeKind.RestSite) == 4 && old.FlightReserve[3] != 17 && old.ParamsVersion == PlanParams.CurrentParamsVersion);

        // Rest-site relics: tent +2 (sleep and smith), each other rest relic +1 with the tent
        Check("tent + pumpkin candle makes a rest worth 7", Math.Abs(p.Weight(R) + p.RestBonusFor(new[] { "MiniatureTent", "PumpkinCandle" }) - 7f) < 1e-3);
        Check("rest relics without the tent add nothing by default", p.RestBonusFor(new[] { "PumpkinCandle", "Girya" }) == 0f && p.RestBonusFor(new[] { "MiniatureTent" }) == 2f);
        float tentRest = Planner.Evaluate(P(R), Ctx(c => c.RestBonus = 3), p).Score - Planner.Evaluate(P(R), Ctx(), p).Score;
        Check("rest bonus is added at each rest site", Math.Abs(tentRest - 3f) < 1e-3, $"{tentRest:0.00}");

        // All modules off: nothing but base weights
        var off = new PlanParams { ShopModule = false, PotionModule = false, ActModule = false, HpModule = false, StrengthModule = false };
        float baseSum = Planner.Evaluate(P(M, E, R, S, T), Ctx(c => c.Hp = 1), off).Score;
        float expect = off.Weight(M) + off.Weight(E) + off.Weight(R) + off.Weight(S) + off.Weight(T) + off.Weight(NodeKind.Boss);
        Check("all modules off = plain weight sum", Math.Abs(baseSum - expect) < 1e-3, $"{baseSum:0.00} vs {expect:0.00}");

        Lines.Add($"TOTAL pass={_pass} fail={_fail}");
        return string.Join("\n", Lines);
    }
}
