using System;
using System.Collections.Generic;
using Godot;

namespace RouteSuggestPlus;

/// <summary>
/// Native-style settings window (gold on ink, game CJK font), opened with the hotkey or the map button.
/// No Godot source generator: no engine virtual overrides, only signals wired in the constructor.
/// Styling helpers follow SpireStreak's HUD.
/// </summary>
public partial class SettingsPanel : Control
{
    const string CjkFontPath = "res://themes/fonts/zhs/noto_sans_mono_cjksc_regular_shared.tres";
    const float PanelWidth = 500f;

    static readonly Color InkSolid = new(0.065f, 0.072f, 0.10f, 0.97f);
    static readonly Color Gold = new(0.96f, 0.78f, 0.42f);
    static readonly Color GoldDim = new(0.80f, 0.62f, 0.32f, 0.55f);
    static readonly Color TextMain = new(0.93f, 0.94f, 0.97f);
    static readonly Color TextSub = new(0.66f, 0.70f, 0.78f);
    static readonly Color TextMute = new(0.50f, 0.53f, 0.60f);
    static readonly Color WarnColor = new(1.0f, 0.62f, 0.38f);
    static readonly Color Clear = new(0f, 0f, 0f, 0f);

    RouteSettings _settings = new();
    Action _changed = () => { };
    Action _clearPins = () => { };
    Action _resetPositions = () => { };
    bool _built, _open, _syncing;
    PanelContainer _panel = null!;
    ScrollContainer _scroll = null!;
    VBoxContainer _routes = null!;
    Label _status = null!;
    Label _note = null!;
    readonly List<Action> _sync = new();

    public bool IsOpen => _open;

    public SettingsPanel()
    {
        Name = "RouteSuggestPlusSettings";
        Ready += OnReady;
    }

    public void Initialize(RouteSettings settings, Action changed, Action clearPins, Action? resetPositions = null)
    {
        _resetPositions = resetPositions ?? (() => { });
        _settings = settings;
        _changed = changed;
        _clearPins = clearPins;
        if (_built) Sync();
    }

    public void Toggle() => SetOpen(!_open);

    public void SetOpen(bool open)
    {
        _open = open;
        if (!_built) return;
        _panel.Visible = open;
        if (open)
        {
            Sync();
            _panel.GrabFocus();
        }
    }

    public void SetStatus(string text, string? note)
    {
        if (!_built) return;
        _status.Text = text;
        _note.Text = note ?? "";
        _note.Visible = !string.IsNullOrEmpty(note);
    }

    public static Font? LoadCjkFont()
    {
        try { return ResourceLoader.Exists(CjkFontPath) ? ResourceLoader.Load<Font>(CjkFontPath) : null; }
        catch { return null; }
    }

    void OnReady()
    {
        if (_built) return;
        MouseFilter = MouseFilterEnum.Ignore;
        ProcessMode = ProcessModeEnum.Always;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var font = LoadCjkFont();
        if (font is not null)
        {
            var theme = new Theme { DefaultFont = font };
            foreach (var type in new[] { "Label", "Button", "CheckButton", "LineEdit" }) theme.SetFont("font", type, font);
            Theme = theme;
        }
        Build();
        _built = true;
        _panel.Visible = _open;
        Sync();
    }

    void Build()
    {
        var style = MakeBox(InkSolid, GoldDim, 1, 10);
        style.ContentMarginLeft = style.ContentMarginRight = 16;
        style.ContentMarginTop = style.ContentMarginBottom = 14;
        style.ShadowColor = new Color(0f, 0f, 0f, 0.5f);
        style.ShadowSize = 18;
        _panel = new PanelContainer { Name = "RouteSettingsPanel", MouseFilter = MouseFilterEnum.Stop, FocusMode = FocusModeEnum.All, Visible = false };
        _panel.AddThemeStyleboxOverride("panel", style);
        _panel.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _panel.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Echo: false } key && (key.Keycode == Key.Escape || key.PhysicalKeycode == Key.Escape))
            {
                SetOpen(false);
                _panel.AcceptEvent();
            }
        };
        AddChild(_panel);
        _panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        _panel.Resized += Place;
        Resized += Place;

        var outer = VBox(10);
        _panel.AddChild(outer);
        var titleRow = HBox(8);
        outer.AddChild(titleRow);
        // Drag the panel by its title bar; the spot is remembered in the config.
        titleRow.MouseFilter = MouseFilterEnum.Stop;
        titleRow.MouseDefaultCursorShape = CursorShape.Move;
        titleRow.TooltipText = "按住标题栏可拖动";
        titleRow.GuiInput += OnTitleInput;
        titleRow.AddChild(new ColorRect { Color = Gold, CustomMinimumSize = new Vector2(3, 18), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
        var title = MakeLabel("路线推荐 · 精修", 20, Gold);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleRow.AddChild(title);
        var close = MakeButton("关闭");
        close.Pressed += () => SetOpen(false);
        titleRow.AddChild(close);

        _note = MakeHint("");
        _note.CustomMinimumSize = new Vector2(PanelWidth, 0); // autowrap needs a width, or its minimum height explodes
        _note.AddThemeColorOverride("font_color", WarnColor);
        _note.Visible = false;
        outer.AddChild(_note);

        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            CustomMinimumSize = new Vector2(PanelWidth, 200),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        outer.AddChild(_scroll);
        var c = VBox(6);
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        c.CustomMinimumSize = new Vector2(PanelWidth - 24, 0);
        _scroll.AddChild(c);

        c.AddChild(MakeSection("路线"));
        _routes = VBox(2);
        c.AddChild(_routes);
        c.AddChild(Indent(MakeHint("金色流光为精修路线（按当前血量、金币、药水实时规划）；其余为固定权重路线。")));

        c.AddChild(MakeSection("精修考虑"));
        Toggle(c, "商店看金币", "金币 100 以下商店≈无收益，300 满；删卡越贵商店越不值。", () => _settings.Plan.ShopModule, v => _settings.Plan.ShopModule = v);
        Toggle(c, "药水换精英", "约 1 瓶好药或 2 瓶普通药抵 1 只精英；药水栏满时掉药不再加分。", () => _settings.Plan.PotionModule, v => _settings.Plan.PotionModule = v);
        Toggle(c, "分层精英", "精英收益 第一层 ×1.3 / 第二层 ×1.0 / 第三层 ×0.8。", () => _settings.Plan.ActModule, v => _settings.Plan.ActModule = v);
        Toggle(c, "血量安全线", "按 1/2/3 层绝对血量 10/20/30 设软安全线，少血避战、多血迎战。", () => _settings.Plan.HpModule, v => _settings.Plan.HpModule = v);
        Toggle(c, "近期战力", "最近 3 场受伤与预期之比（最后一场权重最高）修正预计掉血。", () => _settings.Plan.StrengthModule, v => _settings.Plan.StrengthModule = v);
        Toggle(c, "飞鞋规划", "有飞鞋时，精修路线可飞到下一行任意房间（金色弧线）；本层飞几次在地图上的飞鞋小条里选（默认自动）。", () => _settings.Plan.FlightModule, v => _settings.Plan.FlightModule = v);

        c.AddChild(MakeSection("参数"));
        Slider(c, "血量权重", 0, 2, 0.1, () => _settings.Plan.HpWeight, v => _settings.Plan.HpWeight = (float)v, v => v.ToString("0.0"));
        Slider(c, "战力影响", 0, 1, 0.05, () => _settings.Plan.StrengthInfluence, v => _settings.Plan.StrengthInfluence = (float)v, v => v.ToString("0.00"));
        Slider(c, "单店消费", 150, 450, 25, () => _settings.Plan.ShopSpend, v => _settings.Plan.ShopSpend = (float)v, v => v.ToString("0"));
        Slider(c, "一层精英", 0.8, 1.8, 0.05, () => _settings.Plan.ActEliteMultiplier[0], v => _settings.Plan.ActEliteMultiplier[0] = (float)v, v => "×" + v.ToString("0.00"));
        c.AddChild(Indent(MakeHint("更多参数（掉血、安全线、药水价值等）在模组目录 RouteSuggestPlusConfig.json。")));

        c.AddChild(MakeSection("显示"));
        Toggle(c, "多线并排", "多条路线走同一段时左右对称排开，不互相覆盖。", () => _settings.Display.Lanes, v => _settings.Display.Lanes = v);
        Toggle(c, "精修流光", "精修路线加金色柔光，并朝 Boss 方向流动。", () => _settings.Display.Glow, v => _settings.Display.Glow = v);
        Toggle(c, "右键固定节点", "右键未来节点固定/取消；所有路线都须经过固定节点。", () => _settings.Display.PinsEnabled, v => _settings.Display.PinsEnabled = v);
        Slider(c, "线间距", 5, 16, 1, () => _settings.Display.LaneSpacing, v => _settings.Display.LaneSpacing = (float)v, v => v.ToString("0"));
        var pinRow = HBox(10);
        var clearPins = MakeButton("清空固定节点");
        clearPins.Pressed += () => _clearPins();
        pinRow.AddChild(clearPins);
        var resetSpots = MakeButton("恢复默认位置");
        resetSpots.TooltipText = "路线设置按钮、设置面板、飞鞋小条回到默认位置";
        resetSpots.Pressed += () => _resetPositions();
        pinRow.AddChild(resetSpots);
        c.AddChild(Indent(pinRow));

        c.AddChild(MakeSection("当前评估"));
        _status = MakeLabel("", 14, TextSub);
        _status.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
        _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        c.AddChild(Indent(_status));

        var footer = MakeHint($"{_settings.Display.Hotkey} 或地图上的「路线设置」按钮打开（按钮可拖动）· 拖标题栏移动 · Esc 关闭");
        footer.HorizontalAlignment = HorizontalAlignment.Center;
        footer.AutowrapMode = TextServer.AutowrapMode.Off;
        outer.AddChild(footer);
    }

    /// <summary>Re-applies the saved (or default) position, e.g. after a reset.</summary>
    public void Replace() => Place();

    void Place()
    {
        if (_panel is null || _scroll is null) return;
        var view = Size.X > 0 ? Size : GetViewportRect().Size;
        // Fixed height below the top bar; the scroll area fills whatever the sections do not use.
        float height = Math.Clamp(view.Y - 260f, 360f, 760f);
        float width = _panel.GetCombinedMinimumSize().X;
        var size = new Vector2(width, height);
        if (_panel.Size != size) _panel.Size = size;
        var spot = _settings.Display.PanelX >= 0 && _settings.Display.PanelY >= 0
            ? new Vector2(_settings.Display.PanelX, _settings.Display.PanelY)
            : new Vector2(view.X - width - 48, 120f);
        _panel.Position = Clamp(spot, size, view);
    }

    static Vector2 Clamp(Vector2 spot, Vector2 size, Vector2 view) => new(
        Math.Clamp(spot.X, 8f, Math.Max(8f, view.X - size.X - 8f)),
        Math.Clamp(spot.Y, 8f, Math.Max(8f, view.Y - size.Y - 8f)));

    bool _dragging;
    Vector2 _grab;

    void OnTitleInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed) { _dragging = true; _grab = button.GlobalPosition - _panel.GlobalPosition; }
            else if (_dragging)
            {
                _dragging = false;
                _settings.Display.PanelX = _panel.Position.X;
                _settings.Display.PanelY = _panel.Position.Y;
                _changed(); // saves the config
            }
            _panel.AcceptEvent();
        }
        else if (e is InputEventMouseMotion motion && _dragging)
        {
            var view = Size.X > 0 ? Size : GetViewportRect().Size;
            _panel.Position = Clamp(motion.GlobalPosition - _grab - GlobalPosition, _panel.Size, view);
            _panel.AcceptEvent();
        }
    }

    void RebuildRoutes()
    {
        foreach (var child in _routes.GetChildren()) child.QueueFree();
        foreach (var path in _settings.Paths)
        {
            var p = path;
            var row = HBox(8);
            row.AddChild(new ColorRect { Color = p.Color, CustomMinimumSize = new Vector2(14, 14), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
            var t = MakeToggle(p.Dynamic ? "精修推荐（Recommend）" : p.Name, v => { if (_syncing) return; p.Enabled = v; _changed(); });
            t.SetPressedNoSignal(p.Enabled);
            row.AddChild(t);
            _routes.AddChild(row);
        }
    }

    void Sync()
    {
        if (!_built) return;
        _syncing = true;
        RebuildRoutes();
        foreach (var s in _sync) s();
        _syncing = false;
        Place();
    }

    void Toggle(VBoxContainer parent, string text, string hint, Func<bool> get, Action<bool> set)
    {
        var t = MakeToggle(text, v => { if (_syncing) return; set(v); _changed(); });
        parent.AddChild(t);
        parent.AddChild(Indent(MakeHint(hint)));
        _sync.Add(() => t.SetPressedNoSignal(get()));
    }

    void Slider(VBoxContainer parent, string caption, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> format)
    {
        var row = HBox(10);
        var cap = MakeLabel(caption, 15, TextMain);
        cap.CustomMinimumSize = new Vector2(76, 0);
        row.AddChild(cap);
        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step, Scrollable = false,
            FocusMode = FocusModeEnum.None, MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(160, 20),
        };
        var value = MakeLabel("", 14, Gold);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.CustomMinimumSize = new Vector2(56, 0);
        slider.ValueChanged += v => { value.Text = format(v); if (!_syncing) set(v); };
        slider.DragEnded += changed => { if (changed && !_syncing) _changed(); };
        row.AddChild(slider);
        row.AddChild(value);
        parent.AddChild(row);
        _sync.Add(() => { slider.SetValueNoSignal(get()); value.Text = format(get()); });
    }

    // ---- styling helpers (same look as SpireStreak) ----

    static StyleBoxFlat MakeBox(Color bg, Color border, int borderWidth, int radius)
    {
        var sb = new StyleBoxFlat { BgColor = bg, BorderColor = border, AntiAliasing = true };
        sb.SetBorderWidthAll(borderWidth);
        sb.SetCornerRadiusAll(radius);
        return sb;
    }

    static HBoxContainer HBox(int separation)
    {
        var b = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        b.AddThemeConstantOverride("separation", separation);
        return b;
    }

    static VBoxContainer VBox(int separation)
    {
        var b = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        b.AddThemeConstantOverride("separation", separation);
        return b;
    }

    static MarginContainer Indent(Control child)
    {
        var m = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        m.AddThemeConstantOverride("margin_left", 16);
        m.AddThemeConstantOverride("margin_bottom", 2);
        m.AddChild(child);
        return m;
    }

    static Label MakeLabel(string text, int fontSize, Color color)
    {
        var l = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", fontSize);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    static Label MakeHint(string text)
    {
        var l = MakeLabel(text, 13, TextMute);
        l.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return l;
    }

    static Control MakeSection(string text)
    {
        var box = HBox(8);
        box.AddChild(MakeLabel(text, 15, Gold));
        box.AddChild(new ColorRect { Color = new Color(GoldDim, 0.3f), CustomMinimumSize = new Vector2(0, 1), SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
        var m = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        m.AddThemeConstantOverride("margin_top", 6);
        m.AddChild(box);
        return m;
    }

    static CheckButton MakeToggle(string text, Action<bool> onToggled)
    {
        var cb = new CheckButton
        {
            Text = text, FocusMode = FocusModeEnum.None, MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        cb.AddThemeFontSizeOverride("font_size", 16);
        cb.AddThemeColorOverride("font_color", TextMain);
        cb.AddThemeColorOverride("font_pressed_color", TextMain);
        cb.AddThemeColorOverride("font_hover_color", Gold);
        cb.AddThemeColorOverride("font_hover_pressed_color", Gold);
        cb.AddThemeColorOverride("font_focus_color", TextMain);
        var normal = MakeBox(Clear, Clear, 0, 5);
        normal.ContentMarginLeft = 6;
        normal.ContentMarginRight = 4;
        normal.ContentMarginTop = normal.ContentMarginBottom = 3;
        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = new Color(1f, 1f, 1f, 0.05f);
        cb.AddThemeStyleboxOverride("normal", normal);
        cb.AddThemeStyleboxOverride("pressed", normal);
        cb.AddThemeStyleboxOverride("hover", hover);
        cb.AddThemeStyleboxOverride("hover_pressed", hover);
        cb.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        cb.Toggled += on => onToggled(on);
        return cb;
    }

    public static Button MakeButton(string text)
    {
        var b = new Button { Text = text, FocusMode = FocusModeEnum.None, MouseFilter = MouseFilterEnum.Pass, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        b.AddThemeFontSizeOverride("font_size", 12);
        b.AddThemeColorOverride("font_color", TextMain);
        b.AddThemeColorOverride("font_hover_color", Gold);
        b.AddThemeColorOverride("font_pressed_color", Gold);
        var normal = MakeBox(new Color(InkSolid, 0.85f), GoldDim, 1, 5);
        normal.ContentMarginLeft = normal.ContentMarginRight = 10;
        normal.ContentMarginTop = normal.ContentMarginBottom = 4;
        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BorderColor = Gold;
        var pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = new Color(Gold, 0.28f);
        pressed.BorderColor = Gold;
        b.AddThemeStyleboxOverride("normal", normal);
        b.AddThemeStyleboxOverride("hover", hover);
        b.AddThemeStyleboxOverride("pressed", pressed);
        b.AddThemeStyleboxOverride("hover_pressed", pressed);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var disabled = (StyleBoxFlat)normal.Duplicate();
        disabled.BorderColor = new Color(GoldDim, 0.2f);
        b.AddThemeStyleboxOverride("disabled", disabled);
        b.AddThemeColorOverride("font_disabled_color", new Color(TextMain, 0.3f));
        var font = LoadCjkFont();
        if (font is not null) b.AddThemeFontOverride("font", font);
        return b;
    }
}
