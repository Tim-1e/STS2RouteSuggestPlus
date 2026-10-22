using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Random;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using RouteSuggestPlus;

/// <summary>
/// Offline study for Winged Boots (test-only, never shipped): generates many maps with the game's own generator,
/// measures how much 0..3 flights improve the planned route per act, and derives the value of keeping flights
/// for later acts by a small dynamic program over the samples.
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class FlightStudy
{
    const int MapsPerAct = 300;

    public static void Initialize() { _ = Run(); }

    static async Task Run()
    {
        string output = System.Environment.GetEnvironmentVariable("ROUTEPLUS_PROBE_OUTPUT")!;
        var game = NGame.Instance!;
        void Report(string status, object? data = null, string? error = null) => File.WriteAllText(Path.Combine(output, "native-report.json"),
            JsonSerializer.Serialize(new { status, checks = new[] { "flight study" }, notes = Array.Empty<string>(), error, data }, new JsonSerializerOptions { WriteIndented = true }));
        Report("running");
        try
        {
            await game.GameStartupComplete;
            // A run sets up the ascension/unlock context the map generator reads.
            await game.StartNewSingleplayerRun(ModelDb.Character<Ironclad>(), false,
                new ActModel[] { ModelDb.Act<Overgrowth>(), ModelDb.Act<Hive>(), ModelDb.Act<Glory>() },
                Array.Empty<ModifierModel>(), "FLIGHTSTUDY", GameMode.Standard);

            var p = new PlanParams();
            var acts = new (int act, ActModel model)[]
            {
                (0, ModelDb.Act<Overgrowth>()), (0, ModelDb.Act<Underdocks>()), (1, ModelDb.Act<Hive>()), (2, ModelDb.Act<Glory>()),
            };
            // gains[act] = list of (score with ≤ j flights − score with none, flights actually used) per map.
            var gains = new List<(float[] g, int[] used)>[3] { new(), new(), new() };
            var timing = new List<double>();
            var rows = new List<int>[3] { new(), new(), new() };
            foreach (var (act, model) in acts)
            {
                for (int i = 0; i < MapsPerAct; i++)
                {
                    var map = new StandardActMap(new Rng((uint)(1000 + i * 7919 + act * 104729), $"study_{model.GetType().Name}_{i}"), model, false, false);
                    var graph = RouteEngine.ToGraph(map, out var index);
                    var ctx = Context(act);
                    var sw = Stopwatch.StartNew();
                    var best = FlightPlanner.Plan(graph, index[map.StartingMapPoint], 3, ctx, p);
                    timing.Add(sw.Elapsed.TotalMilliseconds);
                    if (best[0] is null) continue;
                    var g = new float[4];
                    var used = new int[4];
                    for (int j = 0; j <= 3; j++)
                    {
                        g[j] = (best[j] ?? best[0])!.Plan.Score - best[0]!.Plan.Score;
                        used[j] = (best[j] ?? best[0])!.Jumps;
                    }
                    gains[act].Add((g, used));
                    rows[act].Add(map.GetRowCount());
                }
            }

            // V[a][r]: expected gain from holding r flights at the start of act a, playing optimally afterwards.
            var V = new float[4, 4];
            var policy = new Dictionary<string, int[]>();
            for (int a = 2; a >= 0; a--)
                for (int r = 0; r <= 3; r++)
                {
                    float sum = 0;
                    var picks = new int[4];
                    foreach (var (g, used) in gains[a])
                    {
                        float bestTotal = float.NegativeInfinity;
                        int bestJ = 0;
                        for (int j = 0; j <= r; j++)
                        {
                            float total = g[j] + V[a + 1, r - used[j]];
                            if (total > bestTotal + 1e-4f) { bestTotal = total; bestJ = used[j]; }
                        }
                        sum += bestTotal;
                        picks[bestJ]++;
                    }
                    V[a, r] = gains[a].Count > 0 ? sum / gains[a].Count : 0;
                    policy[$"act{a + 1}_with{r}"] = picks;
                }

            object Stats(int a, int j) // marginal gain of the j-th flight
            {
                var xs = gains[a].Select(x => x.g[j] - x.g[j - 1]).OrderBy(x => x).ToArray();
                float Q(float q) => xs.Length == 0 ? 0 : xs[(int)Math.Clamp(q * (xs.Length - 1), 0, xs.Length - 1)];
                return new { mean = xs.Length == 0 ? 0 : xs.Average(), p10 = Q(0.1f), p25 = Q(0.25f), p50 = Q(0.5f), p75 = Q(0.75f), p90 = Q(0.9f), zero = xs.Count(x => x < 0.05f) };
            }
            var data = new
            {
                maps = gains.Select(x => x.Count).ToArray(),
                rows = rows.Select(r => r.Count == 0 ? 0 : r.Average()).ToArray(),
                ms = new { mean = timing.Average(), max = timing.Max() },
                marginal = Enumerable.Range(0, 3).Select(a => new { act = a + 1, first = Stats(a, 1), second = Stats(a, 2), third = Stats(a, 3) }).ToArray(),
                reserve = Enumerable.Range(0, 3).Select(a => Enumerable.Range(0, 4).Select(r => MathF.Round(V[a, r], 2)).ToArray()).ToArray(),
                flight_reserve_param = Enumerable.Range(0, 12).Select(i => MathF.Round(V[i / 4, i % 4], 2)).ToArray(),
                policy,
            };
            File.WriteAllText(Path.Combine(output, "flight-study.json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            Report("passed", data);
            game.GetTree().Quit();
        }
        catch (Exception ex)
        {
            GD.PrintErr(ex);
            Report("failed", null, ex.ToString());
            game.GetTree().Quit(1);
        }
    }

    // A typical state entering each act (single-player A10 wins are healthier; keep it middling).
    static PlanContext Context(int act) => act switch
    {
        0 => new PlanContext { ActIndex = 0, Hp = 70, MaxHp = 80, Gold = 100, RestHeal = 24, Potions = new() { 0.5f } },
        1 => new PlanContext { ActIndex = 1, Hp = 65, MaxHp = 85, Gold = 150, RestHeal = 25, Potions = new() { 0.5f, 1f } },
        _ => new PlanContext { ActIndex = 2, Hp = 65, MaxHp = 90, Gold = 200, RestHeal = 27, Potions = new() { 0.5f, 1f } },
    };
}
