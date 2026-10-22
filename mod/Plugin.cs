using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace RouteSuggestPlus;

[ModInitializer(nameof(Initialize))]
public static class Plugin
{
    public const string Version = "0.1.1";
    public static RouteController? Controller { get; private set; }

    public static void Initialize()
    {
        new Harmony("RouteSuggestPlus").PatchAll(Assembly.GetExecutingAssembly());
        Controller = new RouteController();
        Log($"{Version} loaded; map overlay {(MapOverlay.Available ? "ready" : "unavailable")}");
    }

    public static void Log(string message) => GD.Print("[RouteSuggestPlus] " + message);
}

[HarmonyPatch(typeof(NGlobalUi), nameof(NGlobalUi.Initialize))]
static class AttachPanelPatch
{
    static void Postfix(NGlobalUi __instance)
    {
        try { Plugin.Controller?.AttachUi(__instance); }
        catch (Exception ex) { GD.PrintErr("[RouteSuggestPlus] settings panel: " + ex); }
    }
}

/// <summary>Right-press on a map node toggles a pin instead of starting a native pen stroke; elsewhere drawing is untouched.</summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen._GuiInput))]
static class MapRightClickPatch
{
    static bool Prefix(NMapScreen __instance, InputEvent inputEvent)
    {
        try { return Plugin.Controller?.TryPinFromMapClick(__instance, inputEvent) != true; }
        catch (Exception ex) { Plugin.Log("pin click: " + ex.Message); return true; }
    }
}

/// <summary>The game recolors the traveled edge during the travel animation; take our overlay off first.</summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.TravelToMapCoord))]
static class TravelPatch
{
    static void Prefix()
    {
        try { Plugin.Controller?.OnTravel(); }
        catch (Exception ex) { Plugin.Log("travel: " + ex.Message); }
    }
}

/// <summary>Owns settings, pins and the overlay; recomputes on run events and redraws when the map is open.</summary>
public sealed class RouteController
{
    public RouteSettings Settings { get; }
    readonly MapOverlay _overlay;
    readonly HashSet<MapCoord> _pins = new();
    public IReadOnlyList<RouteResult> Results => _results;
    public IReadOnlyCollection<MapCoord> Pins => _pins;
    public PlanContext? LastContext { get; private set; }
    public string? LastToast { get; private set; }
    public MapOverlay Overlay => _overlay;
    public SettingsPanel? Panel => _panel;
    RunState? _run;
    List<RouteResult> _results = new();
    string? _note;
    string? _pendingToast;
    bool _truncated;
    // Winged Boots: the player's choice of total flights in this act (null = auto); reset when a new act begins.
    int? _flightChoice;
    int _flightAct = -1;
    public int? FlightChoice => _flightChoice;
    public int FlightsUsedThisAct { get; private set; }

    public void ChooseFlights(int? total)
    {
        _flightChoice = total;
        Recompute();
    }
    bool _dirty = true, _wasOpen, _hotkeyDown;
    SettingsPanel? _panel;
    Button? _mapButton;
    FlightChip? _flightChip;
    Node? _topBar;
    SceneTree? _tree;
    readonly Key _hotkey;

    public RouteController()
    {
        Settings = ConfigStore.Load(out _note);
        if (_note is not null) Plugin.Log(_note);
        _overlay = new MapOverlay
        {
            Portrait = () => _run is { } run ? GameContext.Me(run)?.Character.Icon : null,
        };
        _hotkey = Enum.TryParse<Key>(Settings.Display.Hotkey, true, out var k) ? k : Key.F7;
        var manager = RunManager.Instance;
        manager.RunStarted += run => { _run = run; _pins.Clear(); Recompute(); };
        manager.ActEntered += () => { _pins.Clear(); Recompute(); };
        manager.RoomEntered += Recompute;
        manager.RoomExited += Recompute;
    }

    public void AttachUi(NGlobalUi root)
    {
        if (_panel is not null && GodotObject.IsInstanceValid(_panel)) _panel.QueueFree();
        var panel = new SettingsPanel { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.Initialize(Settings, OnSettingsChanged, ClearPins, ResetPositions);
        // The global UI is rebuilt when returning to the main menu; never keep a freed panel around.
        panel.TreeExiting += () => { if (ReferenceEquals(_panel, panel)) _panel = null; };
        root.AddChild(panel);
        _topBar = root.TopBar;
        AttachFlightChip(root, panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _panel = panel;
        if (_tree is null)
        {
            _tree = root.GetTree();
            _tree.ProcessFrame += Tick;
            _hotkeyDown = Input.IsPhysicalKeyPressed(_hotkey);
        }
        UpdateStatus();
    }

    public void OnSettingsChanged()
    {
        try { ConfigStore.Save(Settings); _note = null; }
        catch (Exception ex) { _note = "设置未能保存，仅本次生效：" + ex.Message; }
        Recompute();
    }

    public void OnTravel()
    {
        _overlay.Clear();
        _dirty = true;
    }

    SettingsPanel? LivePanel => _panel is not null && GodotObject.IsInstanceValid(_panel) ? _panel : null;

    void ClearPins()
    {
        _pins.Clear();
        Recompute();
    }

    /// <summary>Returns true when the click was used as a pin toggle (the pen stroke is then skipped).</summary>
    public bool TryPinFromMapClick(NMapScreen screen, InputEvent e)
    {
        if (!Settings.Display.PinsEnabled || _run is null) return false;
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } press) return false;
        // _GuiInput positions are local to the map screen; node rects are in canvas space.
        var at = screen.GetGlobalTransformWithCanvas() * press.Position;
        var node = MapOverlay.PointAt(screen, at);
        if (node?.Point is not { } point) return false;
        int currentRow = _run.CurrentMapPoint?.coord.row ?? -1;
        if (point.PointType == MapPointType.Boss || point.coord.row <= currentRow) return false;
        if (!_pins.Remove(point.coord)) _pins.Add(point.coord);
        Recompute();
        return true;
    }

    public void Recompute()
    {
        _dirty = true;
        try { _run = RunManager.Instance?.DebugOnlyGetState(); }
        catch { _run = null; }
        var run = _run;
        _results = new List<RouteResult>();
        if (run is null) return;
        var start = run.CurrentMapPoint ?? run.Map?.StartingMapPoint;
        if (start is null) return;
        try
        {
            int row = start.coord.row;
            _pins.RemoveWhere(p => p.row <= row);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var ctx = GameContext.Build(run, Settings);
            LastContext = ctx;
            if (ctx.ActIndex != _flightAct)
            {
                _flightAct = ctx.ActIndex;
                _flightChoice = null; // a new act starts on auto
            }
            FlightsUsedThisAct = ctx.FlightsUsedThisAct;
            if (_flightChoice is int chosen) _flightChoice = Math.Clamp(chosen, FlightsUsedThisAct, Math.Min(3, FlightsUsedThisAct + ctx.FlightCharges));
            int? allowedNow = _flightChoice is int total ? Math.Clamp(total - FlightsUsedThisAct, 0, ctx.FlightCharges) : null;
            bool flying = Settings.Plan.FlightModule && ctx.FlightCharges > 0;
            // Pins left behind on another branch can never be reached on foot: drop them once, with a notice
            // (with Winged Boots charges they may still be reached by flying, so they stay).
            if (!flying)
            {
                var reachable = RouteEngine.Reachable(start);
                int dropped = _pins.RemoveWhere(p => !reachable.Contains(p));
                if (dropped > 0) _pendingToast = $"{dropped} 个固定节点已无法到达，已自动取消";
            }
            var (paths, truncated) = RouteEngine.Enumerate(start, _pins);
            _truncated = truncated;
            if (truncated) Plugin.Log($"route listing capped at {paths.Count} paths");
            _results = RouteEngine.Compute(paths, Settings, ctx);
            if (flying) RouteEngine.ApplyFlights(_results, run, start, _pins, Settings, ctx, allowedNow);
            if (_results.All(r => r.Paths.Count == 0) && _pins.Count > 0)
            {
                // Show only the pins so the conflict is obvious; never fall back to routes that ignore them.
                _results = new List<RouteResult>();
                _pendingToast = "没有同时经过所有固定节点的路线，请右键取消其中一个";
                UpdateStatus(ctx, null);
                return;
            }
            var rec = _results.FirstOrDefault(r => r.Config.Dynamic);
            Plugin.Log($"act {run.CurrentActIndex + 1} row {row}: {paths.Count} paths{(truncated ? " (capped)" : "")}, pins {_pins.Count}, {timer.ElapsedMilliseconds} ms"
                       + (rec?.Plan is { } p ? $", recommend score {p.Score:0.0} (hp penalty {p.HpPenalty:0.0}, elites {p.Elites}, shops {p.Shops}, end hp {p.EndHp:0}, end gold {p.EndGold:0})" : ""));
            UpdateStatus(ctx, rec);
        }
        catch (Exception ex) { Plugin.Log("recompute failed: " + ex); }
    }

    void UpdateStatus(PlanContext? ctx = null, RouteResult? rec = null)
    {
        SyncFlightChip(ctx);
        var panel = LivePanel;
        if (panel is null) return;
        if (ctx is null)
        {
            panel.SetStatus("进入对局后显示。", _note);
            return;
        }
        string ratios = ctx.StrengthSamples.Count == 0 ? "暂无" : string.Join(" / ", ctx.StrengthSamples.Select(r => r.ToString("0.00")));
        string text = $"第 {ctx.ActIndex + 1} 层 · 血量 {ctx.Hp}/{ctx.MaxHp} · 金币 {ctx.Gold} · 删卡 {ctx.RemovalCost}\n"
                      + $"药水 {ctx.Potions.Count}/{ctx.PotionSlots}（价值 {ctx.Potions.Sum():0.0}）· 掉药率 {ctx.PotionChance:P0} · 火堆回血 {ctx.RestHeal}{(ctx.RestBonus > 0 ? $" · 火堆遗物加成 +{ctx.RestBonus:0}" : "")}\n"
                      + $"近期受伤比 {ratios} → 战力系数 {ctx.Strength:0.00}"
                      + (_pins.Count > 0 ? $" · 固定节点 {_pins.Count}" : "");
        if (rec?.Plan is { } p)
            text += $"\n精修路线：评分 {p.Score:0.0}（血量扣分 {p.HpPenalty:0.0}）· 精英 {p.Elites} · 商店 {p.Shops} · 火堆回血 {p.Heals}/{p.Rests} · 预计到 Boss 血量 {p.EndHp:0} · 金币 {p.EndGold:0}";
        if (ctx.FlightCharges > 0)
        {
            if (!Settings.Plan.FlightModule) text += $"\n飞鞋剩 {ctx.FlightCharges} 次（飞鞋规划已关闭）";
            else if (rec is not null)
            {
                int now = rec.Jumped?.Count(j => j) ?? 0;
                text += $"\n飞鞋剩 {ctx.FlightCharges} 次：本层{(_flightChoice is null ? "建议" : "按你的选择")}再飞 {now} 次，保留 {rec.FlightsKept} 次"
                        + (rec.FlightsKept > 0 && ctx.ActIndex < 2 ? $"（后续期望 +{rec.FlightReserve:0.0}）" : ctx.ActIndex >= 2 && rec.FlightsKept > 0 ? "（本层已无更好的飞法）" : "");
            }
        }
        if (_pins.Count > 0 && _results.Count == 0)
            text += "\n固定节点之间没有可行路线，暂不显示推荐。";
        if (_truncated) text += "\n路线过多，只比较了前一部分；推荐仅供参考。";
        panel.SetStatus(text, _note);
    }

    void Tick()
    {
        bool down = Input.IsPhysicalKeyPressed(_hotkey);
        if (down && !_hotkeyDown) LivePanel?.Toggle();
        _hotkeyDown = down;

        var screen = NMapScreen.Instance;
        bool open = screen is not null && GodotObject.IsInstanceValid(screen) && screen.IsOpen;
        if (open && !_wasOpen) Recompute(); // HP, gold and potions may have changed since the last room event
        if (LiveFlightChip is { } chip)
        {
            if (open && !_chipPlaced) PlaceFlightChip(false);
            chip.SetMapOpen(open);
        }
        if (open && screen!.IsTraveling) { _wasOpen = open; return; } // redraw once travel has finished
        if (open && (!_wasOpen || _dirty))
        {
            _dirty = false;
            try
            {
                _overlay.Render(screen!, _results, Settings, _pins);
                EnsureMapButton(screen!);
                if (_pendingToast is not null) { MapOverlay.Toast(screen!, _pendingToast); LastToast = _pendingToast; }
            }
            catch (Exception ex) { Plugin.Log("render failed: " + ex); }
            _pendingToast = null;
        }
        _wasOpen = open;
        _overlay.Tick(Time.GetTicksMsec() / 1000.0);
    }

    void EnsureMapButton(NMapScreen screen)
    {
        if (_mapButton is not null && GodotObject.IsInstanceValid(_mapButton) && _mapButton.GetParent() == screen) return;
        var button = SettingsPanel.MakeButton("◆ 路线设置");
        _mapButton = button;
        button.Name = "RouteSuggestPlusButton";
        button.AddThemeFontSizeOverride("font_size", 15);
        button.TooltipText = "点击打开路线设置；按住拖动可移动";
        var drag = new Dragger(button, at =>
        {
            Settings.Display.ButtonX = at.X;
            Settings.Display.ButtonY = at.Y;
            SaveQuietly();
        });
        drag.Watch(button);
        button.Pressed += () =>
        {
            if (drag.TakeClick()) return; // a drag is not a click
            LivePanel?.Toggle();
        };
        screen.AddChild(button);
        button.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        button.OffsetLeft = -226;
        button.OffsetTop = -164;
        button.OffsetRight = -60;
        button.OffsetBottom = -124;
        if (Settings.Display.ButtonX >= 0 && Settings.Display.ButtonY >= 0)
        {
            Dragger.Free(button);
            button.Position = Dragger.Clamp(button, new Vector2(Settings.Display.ButtonX, Settings.Display.ButtonY));
        }
    }

    public FlightChip? LiveFlightChip => _flightChip is not null && GodotObject.IsInstanceValid(_flightChip) ? _flightChip : null;

    /// <summary>
    /// The Winged Boots chip is a child of the top bar, so the bar never hides it and game popups that cover the bar
    /// cover the chip too (falls back to the global UI under the settings panel if the bar is a layout container).
    /// Its default spot is the bar's empty middle; it is shown only while the map is open.
    /// </summary>
    void AttachFlightChip(NGlobalUi root, Control panel)
    {
        if (LiveFlightChip is { } old) old.QueueFree();
        var chip = FlightChip.Create(_topBar, ChooseFlights, at =>
        {
            Settings.Display.FlightX = at.X;
            Settings.Display.FlightY = at.Y;
            SaveQuietly();
        }, ResetFlightChip);
        _flightChip = chip;
        _chipPlaced = false;
        if (_topBar is Control bar && bar is not Container) bar.AddChild(chip);
        else
        {
            root.AddChild(chip);
            root.MoveChild(chip, panel.GetIndex()); // below the panel, above the top bar
        }
        SyncFlightChip(LastContext);
    }

    bool _chipPlaced;

    /// <summary>Places the chip once the UI has its real size (first time the map opens).</summary>
    void PlaceFlightChip(bool reset)
    {
        if (LiveFlightChip is not { } chip) return;
        chip.ResetSize();
        bool saved = !reset && Settings.Display.FlightX >= 0 && Settings.Display.FlightY >= 0;
        chip.Position = Dragger.Clamp(chip, saved ? new Vector2(Settings.Display.FlightX, Settings.Display.FlightY) : DefaultChipSpot(chip));
        _chipPlaced = true;
    }

    Vector2 DefaultChipSpot(Control chip)
    {
        var parent = chip.GetParent() as Control;
        var view = parent is not null && parent.Size.X > 0 ? parent.Size : chip.GetViewportRect().Size;
        // The top bar node spans the screen; line up with its gold counter instead (fallback: a typical bar height).
        float centerY = 48f;
        if (_topBar is Node bar && GodotObject.IsInstanceValid(bar))
            foreach (var n in bar.FindChildren("*", "", true, false))
                if (n.GetType().Name is "NTopBarGold" or "NTopBarHp" && n is Control c && c.Size.Y > 0)
                {
                    centerY = c.GetGlobalRect().GetCenter().Y - (parent?.GetGlobalRect().Position.Y ?? 0f);
                    break;
                }
        // Middle of the bar, between the floor/boss icons and the run timer.
        return new Vector2(view.X * 0.553f, Math.Max(0f, centerY - chip.Size.Y * 0.5f));
    }

    public void ResetFlightChip()
    {
        Settings.Display.FlightX = Settings.Display.FlightY = -1f;
        SaveQuietly();
        PlaceFlightChip(true);
    }

    /// <summary>Settings panel: put the button, panel and chip back where they start.</summary>
    public void ResetPositions()
    {
        Settings.Display.ButtonX = Settings.Display.ButtonY = -1f;
        Settings.Display.PanelX = Settings.Display.PanelY = -1f;
        if (_mapButton is not null && GodotObject.IsInstanceValid(_mapButton)) { _mapButton.QueueFree(); _mapButton = null; }
        if (NMapScreen.Instance is { } screen && GodotObject.IsInstanceValid(screen) && screen.IsOpen) EnsureMapButton(screen);
        LivePanel?.Replace();
        ResetFlightChip();
    }

    void SyncFlightChip(PlanContext? ctx)
    {
        if (LiveFlightChip is not { } chip) return;
        if (ctx is null || !Settings.Plan.FlightModule) chip.SetFlights(0, 0, null);
        else chip.SetFlights(ctx.FlightCharges, FlightsUsedThisAct, _flightChoice);
    }

    void SaveQuietly()
    {
        try { ConfigStore.Save(Settings); } catch (Exception ex) { Plugin.Log("save position: " + ex.Message); }
    }
}
