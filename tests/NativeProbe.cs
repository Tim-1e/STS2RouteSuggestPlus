using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using RouteSuggestPlus;

/// <summary>Isolated native test for RouteSuggestPlus. Test-only: never shipped.</summary>
[ModInitializer(nameof(Initialize))]
public static class RouteProbe
{
    public static void Initialize() { _ = Run(); }

    static async Task Run()
    {
        string output = System.Environment.GetEnvironmentVariable("ROUTEPLUS_PROBE_OUTPUT")!;
        var checks = new List<string>();
        var notes = new List<string>();
        var game = NGame.Instance!;
        void Check(bool value, string label)
        {
            if (!value) throw new Exception(label);
            checks.Add(label);
            GD.Print("[RouteProbe] " + label);
        }
        async Task Frames(int n) { for (int i = 0; i < n; i++) await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Wait(Func<bool> condition, string label)
        {
            ulong start = Time.GetTicksMsec();
            while (!condition())
            {
                if (Time.GetTicksMsec() - start > 25000) throw new TimeoutException(label);
                await Frames(1);
            }
        }
        async Task Capture(string name)
        {
            await Task.Delay(700);
            await game.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            game.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(output, name + ".png"));
        }
        async Task MouseClick(Control node, MouseButton button)
        {
            var center = node.GetScreenTransform() * (node.Size * 0.5f);
            Input.ParseInputEvent(new InputEventMouseMotion { Position = center, GlobalPosition = center });
            await Frames(1);
            Input.ParseInputEvent(new InputEventMouseButton { Position = center, GlobalPosition = center, ButtonIndex = button, Pressed = true });
            await Frames(1);
            Input.ParseInputEvent(new InputEventMouseButton { Position = center, GlobalPosition = center, ButtonIndex = button, Pressed = false });
            await Frames(3);
        }
        async Task MapRightPress(NMapScreen screen, NMapPoint node)
        {
            var canvas = node.GetGlobalTransformWithCanvas() * (node.Size * 0.5f);
            var local = screen.GetGlobalTransformWithCanvas().AffineInverse() * canvas;
            screen._GuiInput(new InputEventMouseButton { Position = local, GlobalPosition = canvas, ButtonIndex = MouseButton.Right, Pressed = true });
            await Frames(2);
            screen._GuiInput(new InputEventMouseButton { Position = local, GlobalPosition = canvas, ButtonIndex = MouseButton.Right, Pressed = false });
            await Frames(3);
        }
        Dictionary<MapCoord, NMapPoint> Nodes(NMapScreen screen) =>
            (Dictionary<MapCoord, NMapPoint>)AccessTools.Field(typeof(NMapScreen), "_mapPointDictionary").GetValue(screen)!;
        void Report(string status, string? error = null) => File.WriteAllText(Path.Combine(output, "native-report.json"),
            JsonSerializer.Serialize(new { status, checks, notes, error }, new JsonSerializerOptions { WriteIndented = true }));

        Report("running");
        try
        {
            Check(OS.GetUserDataDir().Replace('\\', '/').EndsWith("CelestialAnimationGameProbe-RouteSuggestPlus") && !SteamInitializer.Initialized
                  && CommandLineHelper.GetValue("force-steam") == "off", "isolated user directory; Steam off");
            await game.GameStartupComplete;
            // Test profile only: tutorial popups are modal and would swallow synthetic clicks.
            foreach (var ftue in new[] { "combat_rules_ftue", "map_select_ftue", "combat_reward_ftue", "obtain_potion_ftue", "relic_reward_ftue" })
                SaveManager.Instance.MarkFtueAsComplete(ftue);
            var c = Plugin.Controller!;
            Check(c is not null && MapOverlay.Available, "mod loaded; map reflection fields found");
            Check(File.Exists(ConfigStore.ConfigPath), "config written next to the DLL: " + ConfigStore.ConfigPath);

            var state = await game.StartNewSingleplayerRun(ModelDb.Character<Ironclad>(), false,
                new ActModel[] { ModelDb.Act<Overgrowth>(), ModelDb.Act<Hive>(), ModelDb.Act<Glory>() },
                Array.Empty<ModifierModel>(), "ROUTEPLUS1", GameMode.Standard);
            var screen = NMapScreen.Instance!;
            var nodes = Nodes(screen);

            // Finish the starting ancient so the map shows routes from a real position.
            if (!state.CurrentMapCoord.HasValue)
            {
                await Wait(() => screen.IsTravelEnabled && !screen.IsTraveling, "initial map enabled");
                await RunManager.Instance.EnterMapCoord(state.Map.StartingMapPoint.coord);
                await Wait(() => (state.CurrentRoom is EventRoom || (state.CurrentRoom is CombatRoom && CombatManager.Instance.IsInProgress && !CombatManager.Instance.PlayerActionsDisabled)) && !screen.IsTraveling, "starting room");
            }
            if (state.CurrentRoom is EventRoom ancient)
            {
                await ancient.LocalMutableEvent.CurrentOptions[0].Chosen();
                await Wait(() => ancient.LocalMutableEvent.IsFinished, "ancient finished");
                await NEventRoom.Proceed();
                notes.Add("start: ancient event");
            }
            else
            {
                // A fresh profile's first run starts with a fight instead of an ancient.
                await CreatureCmd.Kill(((CombatRoom)state.CurrentRoom!).Enemies.ToArray(), true);
                await Wait(() => !CombatManager.Instance.IsInProgress, "starting fight won");
                notes.Add("start: tutorial fight");
            }
            await Wait(() => screen.IsTravelEnabled, "travel enabled after start");
            screen.Open();
            await Task.Delay(4000); // let the act banner finish
            await Frames(6);

            var rec = c.Results.FirstOrDefault(r => r.Config.Dynamic);
            Check(rec is not null && rec.Paths.Count == 1 && rec.Paths[0].Last().PointType == MapPointType.Boss, "Recommend route computed to the boss");
            Check(c.Results.Count(r => r.Paths.Count > 0) >= 3, "three routes computed (Recommend, Safe, Aggressive)");
            var ctx = c.LastContext!;
            var me = LocalContext.GetMe(state.Players)!;
            Check(ctx.Hp == me.Creature.CurrentHp && ctx.MaxHp == me.Creature.MaxHp && ctx.Gold == me.Gold, $"live HP/gold read ({ctx.Hp}/{ctx.MaxHp}, {ctx.Gold}g)");
            Check(ctx.RemovalCost == 75 && ctx.PotionSlots == me.MaxPotionCount && Math.Abs(ctx.PotionChance - me.PlayerOdds.PotionReward.CurrentValue) < 1e-5, $"removal {ctx.RemovalCost}, slots {ctx.PotionSlots}, potion chance {ctx.PotionChance:0.00}");
            Check(ctx.RestHeal > 0, "rest heal read from game: " + ctx.RestHeal);
            Check(c.Overlay.Halos > 0, "Recommend glow drawn: " + c.Overlay.Halos + " halos");

            // Shared edges: lanes must hide the native dots and draw parallel copies.
            var edges = c.Results.Where(r => r.Paths.Count > 0)
                .SelectMany(r => r.Paths[0].Zip(r.Paths[0].Skip(1), (a, b) => (a.coord, b.coord)).Distinct().Select(e => (e, r.Config.Name)))
                .GroupBy(x => x.e).Where(g => g.Count() >= 2).ToList();
            notes.Add($"shared edges at start: {edges.Count}; spawned lane dots {c.Overlay.SpawnedDots}; hidden native {c.Overlay.Hidden}; recolored {c.Overlay.Recolored}");
            if (edges.Count > 0) Check(c.Overlay.SpawnedDots > 0 && c.Overlay.Hidden > 0, "shared edges drawn as parallel lanes");
            await Capture("map-start");

            // Right-click pin on a node the Recommend route avoids.
            var recPath = rec!.Paths[0];
            int row = state.CurrentMapPoint!.coord.row;
            var reachable = RouteEngine.AllPaths(state.CurrentMapPoint).SelectMany(p => p).Select(p => p.coord).ToHashSet();
            var target = nodes.Keys.Where(k => k.row >= row + 3 && k.row <= row + 6 && reachable.Contains(k) && !recPath.Any(p => p.coord == k)
                                                && nodes[k].Point.PointType != MapPointType.Boss)
                .OrderBy(k => k.row).ThenBy(k => k.col).First();
            await MapRightPress(screen, nodes[target]);
            await Frames(4);
            Check(c.Pins.Contains(target), $"right-click pinned {target}");
            Check(c.Results.Where(r => r.Paths.Count > 0).All(r => r.Paths.All(p => p.Any(n => n.coord == target))), "every route now passes the pinned node");
            Check(c.Overlay.PinMarkers == 1, "pin ring drawn");
            await Task.Delay(500); // let the pen stroke finish drawing
            await Frames(2);
            var pinNode = nodes[target].GetNodeOrNull<Control>("RouteSuggestPlusPin");
            var strokes = pinNode?.GetChildren().OfType<Line2D>().ToList() ?? new List<Line2D>();
            Check(strokes.Count == 2 && strokes.All(l => l.Points.Length > 50), $"hand-drawn ring strokes: {strokes.Count}, points {string.Join("/", strokes.Select(l => l.Points.Length))}");
            Check(pinNode?.GetNodeOrNull<Control>("PinPortrait")?.GetChildCount() == 1, "character portrait at the ring's lower left");
            var all = RouteEngine.AllPaths(state.CurrentMapPoint);
            var (viaPin, capped) = RouteEngine.Enumerate(state.CurrentMapPoint, new[] { target });
            Check(!capped && viaPin.Count == all.Count(p => p.Any(n => n.coord == target)) && viaPin.Count > 0,
                  $"pruned listing matches filtered listing ({viaPin.Count} of {all.Count})");
            await Capture("map-pinned");

            // A second pin in the same row cannot be combined: no routes are drawn, only the pins and a toast.
            var conflict = nodes.Keys.First(k => k.row == target.row && k != target && reachable.Contains(k));
            await MapRightPress(screen, nodes[conflict]);
            await Frames(4);
            Check(c.Pins.Count == 2 && c.LastToast is not null, "impossible pins show a toast: " + c.LastToast);
            Check(c.Results.Count == 0 && c.Overlay.PinMarkers == 2 && c.Overlay.Halos == 0 && c.Overlay.Recolored == 0 && c.Overlay.SpawnedDots == 0,
                  "conflicting pins: no fallback routes drawn");
            Check(screen.FindChildren("RouteSuggestPlusToast*", "", true, false).Count > 0 || screen.GetChildren().Any(n => n.Name.ToString().Contains("Toast")), "toast label on map");
            await Capture("map-pin-conflict");
            await MapRightPress(screen, nodes[conflict]);
            await MapRightPress(screen, nodes[target]);
            Check(c.Pins.Count == 0, "right-click again removes pins");

            // Settings panel and module switches.
            c.Panel!.SetOpen(true);
            await Frames(4);
            var root = (Control)c.Panel;
            var panelRect = ((Control)root.GetChild(0)).GetGlobalRect();
            notes.Add($"settings panel rect {panelRect}, canvas {root.GetViewportRect().Size}");
            Check(panelRect.End.Y <= root.GetViewportRect().Size.Y + 1, "settings panel fits on screen");
            await Capture("settings");
            // The map button can be dragged too; a drag is remembered and does not count as a click.
            var mapButton = screen.GetNode<Button>("RouteSuggestPlusButton");
            var btnFrom = mapButton.GetGlobalRect().GetCenter();
            var btnBefore = mapButton.Position;
            mapButton.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = btnFrom, Position = btnFrom });
            mapButton.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { GlobalPosition = btnFrom + new Vector2(-300, -200), Position = btnFrom });
            mapButton.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = btnFrom + new Vector2(-300, -200), Position = btnFrom });
            await Frames(2);
            Check((mapButton.Position - (btnBefore + new Vector2(-300, -200))).Length() < 2f && Math.Abs(c.Settings.Display.ButtonX - mapButton.Position.X) < 0.5f,
                  $"map button dragged {btnBefore} -> {mapButton.Position}, saved ({c.Settings.Display.ButtonX:0},{c.Settings.Display.ButtonY:0})");
            await Capture("map-button-moved");
            // Drag the panel by its title bar; the new spot is saved.
            var frame = (Control)root.GetChild(0);
            var titleBar = root.FindChildren("*", "HBoxContainer", true, false).OfType<Control>().First(x => x.TooltipText == "按住标题栏可拖动");
            var from = titleBar.GetGlobalRect().GetCenter();
            var before = frame.Position;
            titleBar.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = from, Position = from });
            titleBar.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { GlobalPosition = from + new Vector2(-400, 60), Position = from });
            titleBar.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = from + new Vector2(-400, 60), Position = from });
            await Frames(2);
            Check((frame.Position - (before + new Vector2(-400, 60))).Length() < 2f && Math.Abs(c.Settings.Display.PanelX - frame.Position.X) < 0.5f,
                  $"panel dragged {before} -> {frame.Position}, saved ({c.Settings.Display.PanelX:0},{c.Settings.Display.PanelY:0})");
            c.Settings.Display.Lanes = false;
            c.OnSettingsChanged();
            await Frames(4);
            Check(c.Overlay.SpawnedDots == 0 && c.Overlay.Hidden == 0, "lanes off: priority recolor only");
            c.Settings.Display.Lanes = true;
            c.OnSettingsChanged();
            c.Panel.SetOpen(false);
            await Frames(4);

            // Gold changes the plan input immediately.
            me.Gold = 320;
            c.Recompute();
            Check(c.LastContext!.Gold == 320, "gold change picked up on recompute");
            me.Gold = 99;

            // Winged Boots: with charges the Recommend route may fly (gold arc) and the panel explains the split.
            await RelicCmd.Obtain(ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.WingedBoots>().ToMutable(), me);
            await Wait(() => screen.FindChildren("*RouteSuggestPlusToast*", "", true, false).Count == 0, "toasts fade out");
            Check(true, "earlier toasts faded out and were freed");
            var savedReserve = c.Settings.Plan.FlightReserve;
            c.Settings.Plan.FlightReserve = new float[12]; // nothing to save for: any useful flight is taken now
            screen.Open();
            c.Recompute();
            await Frames(4);
            var flightRec = c.Results.FirstOrDefault(r => r.Config.Dynamic);
            Check(c.LastContext!.FlightCharges == 3, "Winged Boots charges read: " + c.LastContext.FlightCharges);
            int flights = flightRec?.Jumped?.Count(j => j) ?? 0;
            Check(flights > 0 && flightRec!.Paths[0].Last().PointType == MapPointType.Boss, $"Recommend route uses {flights} flight(s) to the boss");
            notes.Add("flight route: " + string.Join(" > ", flightRec!.Paths[0].Select((pt, i) => (flightRec.Jumped![i] ? "✈" : "") + pt.coord.col + "," + pt.coord.row)));
            int arcs = c.Overlay.FlightDots;
            Check(arcs >= 2 * flights && arcs < 60, "flight arcs drawn: " + arcs + " dots");
            for (int w = 0; w < 30; w++) // scroll up to the flights
            {
                screen._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Position = screen.Size * 0.5f, GlobalPosition = screen.Size * 0.5f });
                await Frames(2);
            }
            await Task.Delay(800);
            await Capture("map-flight");
            // Player's choice: total flights in this act (already flown ones are counted against it).
            c.ChooseFlights(0);
            await Frames(2);
            Check((c.Results.First(r => r.Config.Dynamic).Jumped?.Count(j => j) ?? 0) == 0, "choosing 0 flights walks only");
            c.ChooseFlights(1);
            await Frames(2);
            Check((c.Results.First(r => r.Config.Dynamic).Jumped?.Count(j => j) ?? 0) <= 1, "choosing 1 flight uses at most one");
            var chip = c.LiveFlightChip!;
            var flightButtons = chip.Buttons.Select(x => x.button).ToList();
            Check(chip.Visible && flightButtons.Count == 5 && flightButtons[2].ButtonPressed && !flightButtons.Any(b => b.Disabled),
                  "flight chip: 自动/0/1/2/3, '1' selected, all allowed with 3 charges and none used");
            notes.Add($"flight chip rect {chip.GetGlobalRect()}");
            await Capture("flight-chip");
            flightButtons[1].EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(2);
            Check(c.FlightChoice == 0, "clicking '0' on the chip chooses no flights: " + c.FlightChoice);
            var chipFrom = flightButtons[3].GetGlobalRect().GetCenter();
            var chipBefore = chip.Position;
            flightButtons[3].EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = chipFrom, Position = chipFrom });
            flightButtons[3].EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { GlobalPosition = chipFrom + new Vector2(200, -300), Position = chipFrom });
            flightButtons[3].EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = chipFrom + new Vector2(200, -300), Position = chipFrom });
            flightButtons[3].EmitSignal(BaseButton.SignalName.Pressed); // the release after a drag is not a choice
            await Frames(2);
            Check((chip.Position - (chipBefore + new Vector2(200, -300))).Length() < 2f && Math.Abs(c.Settings.Display.FlightX - chip.Position.X) < 0.5f && c.FlightChoice == 0,
                  $"flight chip dragged {chipBefore} -> {chip.Position} without choosing, saved ({c.Settings.Display.FlightX:0},{c.Settings.Display.FlightY:0})");
            c.ChooseFlights(null);
            await Frames(2);
            c.Settings.Plan.FlightReserve = savedReserve;
            c.Recompute();
            await Frames(4);
            notes.Add($"with study reserve: {(c.Results.FirstOrDefault(r => r.Config.Dynamic)?.Jumped?.Count(j => j) ?? 0)} flight(s) now, keep {c.Results.FirstOrDefault(r => r.Config.Dynamic)?.FlightsKept}");

            // Travel: our overlay comes off first, and a dot the game recolored meanwhile keeps the game's color.
            var travelPatch = HarmonyLib.Harmony.GetPatchInfo(typeof(NMapScreen).GetMethod(nameof(NMapScreen.TravelToMapCoord)));
            Check(travelPatch?.Prefixes.Any(x => x.owner == "RouteSuggestPlus") == true, "travel prefix installed");
            var monster = state.CurrentMapPoint!.Children.First(ch => ch.PointType == MapPointType.Monster);
            var dots = ((System.Collections.IDictionary)typeof(NMapScreen).GetField("_paths", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(screen)!);
            var touched = dots.Values.Cast<IReadOnlyList<TextureRect>>().SelectMany(t => t).Where(t => t.Visible && t.Scale.X > 1.2f).ToList();
            Check(touched.Count >= 2, "recolored dots found: " + touched.Count);
            var traveled = new Color(0.3f, 0.3f, 0.3f);
            touched[0].Modulate = traveled; // what the travel animation does to the walked edge
            c.OnTravel();
            Check(touched[0].Modulate == traveled && touched[0].Scale == Vector2.One, "game's traveled color kept, our scale removed");
            Check(touched[1].Scale == Vector2.One && c.Overlay.Halos == 0, "overlay cleared on travel");

            // Real fight: lose 12 HP, win, expect a strength sample of 12 / expected act-1 monster loss in the run history.
            float strengthBefore = c.LastContext!.Strength;
            await RunManager.Instance.EnterMapCoord(monster.coord);
            await Wait(() => state.CurrentRoom is CombatRoom && CombatManager.Instance.IsInProgress && !CombatManager.Instance.PlayerActionsDisabled && !screen.IsTraveling, "combat ready");
            var combat = (CombatRoom)state.CurrentRoom!;
            int hpBefore = me.Creature.CurrentHp;
            // Real damage goes through the game's damage pipeline, which records it in the map history.
            await CreatureCmd.Damage(new MegaCrit.Sts2.Core.GameActions.Multiplayer.BlockingPlayerChoiceContext(), me.Creature, 12,
                MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered, combat.Enemies.First());
            int lost = hpBefore - me.Creature.CurrentHp;
            notes.Add($"fight damage: {lost} HP");
            await CreatureCmd.Kill(combat.Enemies.ToArray(), true);
            await Wait(() => screen.IsTravelEnabled && !CombatManager.Instance.IsInProgress, "combat victory");
            float expected = c.Settings.Plan.MonsterHpLoss[0];
            screen.Open();
            await Frames(6);
            var samples = c.LastContext!.StrengthSamples;
            Check(samples.Count >= 1 && lost > 0 && Math.Abs(samples[^1] - lost / expected) < 0.01f, $"strength sample from history {string.Join("/", samples.Select(x => x.ToString("0.00")))}, last = {lost}/{expected}");
            Check(c.LastContext.Strength > strengthBefore, $"strength factor raised after a bad fight: {strengthBefore:0.000} → {c.LastContext.Strength:0.000}");
            await Capture("map-after-fight");

            // Lifecycle: a freed panel is forgotten, and recomputing without it is harmless.
            c.Panel!.QueueFree();
            await Frames(3);
            Check(c.Panel is null, "freed settings panel is released");
            c.Recompute();
            await Frames(3);
            Check(c.Results.Count > 0, "recompute still works without a panel");

            Report("passed");
            game.GetTree().Quit();
        }
        catch (Exception ex)
        {
            GD.PrintErr(ex);
            Report("failed", ex.ToString());
            try { await Capture("failure"); } catch { }
            game.GetTree().Quit(1);
        }
    }
}
