using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace RouteSuggestPlus;

/// <summary>
/// Lets a control be moved by press-and-drag. A press that travels less than a few pixels stays a click;
/// after a real drag the next click is swallowed (<see cref="TakeClick"/>) and the new spot is reported.
/// </summary>
public sealed class Dragger
{
    const float Threshold = 6f;
    readonly Control _target;
    readonly Action<Vector2> _moved;
    bool _held, _dragged;
    Vector2 _pressAt, _startAt;

    public Dragger(Control target, Action<Vector2> moved)
    {
        _target = target;
        _moved = moved;
    }

    /// <summary>Feeds mouse input from <paramref name="handle"/> (the target itself or any child that eats clicks).</summary>
    public void Watch(Control handle) => handle.GuiInput += e => Input(handle, e);

    /// <summary>True when the click that just happened ended a drag and should be ignored.</summary>
    public bool TakeClick()
    {
        if (!_dragged) return false;
        _dragged = false;
        return true;
    }

    void Input(Control handle, InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.Pressed)
            {
                _held = true;
                _dragged = false;
                _pressAt = mb.GlobalPosition;
                _startAt = _target.Position;
            }
            else if (_held)
            {
                _held = false;
                if (_dragged) _moved(_target.Position);
            }
        }
        else if (e is InputEventMouseMotion motion && _held)
        {
            var delta = motion.GlobalPosition - _pressAt;
            if (!_dragged && delta.Length() < Threshold) return;
            if (!_dragged)
            {
                _dragged = true;
                Free(_target);
            }
            _target.Position = Clamp(_target, _startAt + delta);
            handle.AcceptEvent();
        }
    }

    /// <summary>Switches an anchored control to a plain top-left position, keeping its size.</summary>
    public static void Free(Control c)
    {
        var size = c.Size;
        var at = c.Position;
        c.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        c.Size = size;
        c.Position = at;
    }

    public static Vector2 Clamp(Control c, Vector2 at)
    {
        var view = c.GetParent() is Control parent && parent.Size.X > 0 ? parent.Size : c.GetViewportRect().Size;
        return new Vector2(Math.Clamp(at.X, 0f, Math.Max(0f, view.X - c.Size.X)), Math.Clamp(at.Y, 0f, Math.Max(0f, view.Y - c.Size.Y)));
    }
}

/// <summary>
/// Small Winged Boots chip on the map: the relic icon with flights left, then 自动 / 0 / 1 / 2 / 3 for how many
/// flights to use in this act in total. Styled after the run's top bar (dark slate plate, outlined cream text),
/// and the player can drag it anywhere.
/// </summary>
public sealed partial class FlightChip : PanelContainer
{
    public const string NodeName = "RouteSuggestPlusFlights";
    static readonly Color Cream = new(0.95f, 0.92f, 0.84f);
    static readonly Color Gold = new(0.98f, 0.80f, 0.40f);
    static readonly Color Plate = new(0.10f, 0.15f, 0.18f, 0.92f);
    static readonly Color Edge = new(0.03f, 0.06f, 0.08f, 0.95f);

    readonly List<(Button button, int? value)> _buttons = new();
    Label _left = null!;
    public Dragger Drag { get; private set; } = null!;
    Action<int?> _choose = _ => { };
    Action _reset = () => { };
    bool _syncing;

    public IReadOnlyList<(Button button, int? value)> Buttons => _buttons;

    public static FlightChip Create(Node? topBar, Action<int?> choose, Action<Vector2> moved, Action reset)
    {
        var chip = new FlightChip { Name = NodeName, _choose = choose, _reset = reset, MouseFilter = MouseFilterEnum.Stop };
        chip.Build(topBar, moved);
        return chip;
    }

    void Build(Node? topBar, Action<Vector2> moved)
    {
        Drag = new Dragger(this, moved);
        Drag.Watch(this);
        WatchReset(this);
        var (font, outline, outlineColor) = NativeText(topBar);
        var plate = new StyleBoxFlat
        {
            BgColor = Plate, BorderColor = Edge, AntiAliasing = true,
            ShadowColor = new Color(0f, 0f, 0f, 0.35f), ShadowSize = 6,
            ContentMarginLeft = 6, ContentMarginRight = 10, ContentMarginTop = 4, ContentMarginBottom = 4,
        };
        plate.SetBorderWidthAll(3);
        plate.SetCornerRadiusAll(12);
        AddThemeStyleboxOverride("panel", plate);
        TooltipText = "飞鞋：本层一共飞几次（含已飞）。自动 = 按本层收益与后续期望决定。按住可拖动，右键回到默认位置。";

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 2);
        AddChild(row);

        // Winged Boots icon with the flights left in the corner, like a relic counter.
        var icon = new TextureRect
        {
            CustomMinimumSize = new Vector2(44, 44),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        try { icon.Texture = ModelDb.Relic<WingedBoots>().Icon; } catch { }
        row.AddChild(icon);
        _left = Text("", 21, Gold, font, outline, outlineColor);
        _left.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight);
        _left.GrowHorizontal = GrowDirection.Begin;
        _left.GrowVertical = GrowDirection.Begin;
        _left.OffsetRight = 2;
        _left.OffsetBottom = 4;
        icon.AddChild(_left);

        string[] labels = { "自动", "0", "1", "2", "3" };
        for (int i = 0; i < labels.Length; i++)
        {
            int? value = i == 0 ? null : i - 1;
            var b = ChoiceButton(labels[i], font, outline, outlineColor);
            b.TooltipText = value is null ? "自动：按本层收益与后续期望决定" : $"本层一共飞 {value} 次（含已飞）";
            b.Pressed += () =>
            {
                if (_syncing) return;
                if (Drag.TakeClick()) { Sync(); return; } // a drag is not a click
                _choose(value);
            };
            Drag.Watch(b);
            WatchReset(b);
            row.AddChild(b);
            _buttons.Add((b, value));
        }
    }

    /// <summary>Right-click anywhere on the chip puts it back at its default spot.</summary>
    void WatchReset(Control c) => c.GuiInput += e =>
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
        {
            _reset();
            c.AcceptEvent();
        }
    };

    bool _mapOpen;

    /// <summary>The chip sits on the global UI, so it is only shown while the map is open.</summary>
    public void SetMapOpen(bool open)
    {
        if (_mapOpen == open) return;
        _mapOpen = open;
        Visible = _mapOpen && _leftNow > 0;
    }

    int _leftNow, _usedNow;
    int? _choiceNow;

    /// <summary>Shows the chip only while there is something to choose; out-of-range totals are disabled.</summary>
    public void SetFlights(int left, int usedThisAct, int? choice)
    {
        (_leftNow, _usedNow, _choiceNow) = (left, usedThisAct, choice);
        Visible = _mapOpen && left > 0;
        Sync();
    }

    void Sync()
    {
        int max = Math.Min(3, _usedNow + _leftNow);
        _syncing = true;
        foreach (var (button, value) in _buttons)
        {
            button.Disabled = value is int v && (v < _usedNow || v > max);
            button.ButtonPressed = value == _choiceNow;
        }
        _syncing = false;
        _left.Text = _leftNow.ToString();
    }

    Button ChoiceButton(string text, Font? font, int outline, Color outlineColor)
    {
        var b = new Button { Text = text, ToggleMode = true, FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand };
        b.CustomMinimumSize = new Vector2(text.Length > 1 ? 52 : 34, 40);
        if (font is not null) b.AddThemeFontOverride("font", font);
        b.AddThemeFontSizeOverride("font_size", text.Length > 1 ? 19 : 23);
        b.AddThemeConstantOverride("outline_size", outline);
        b.AddThemeColorOverride("font_outline_color", outlineColor);
        b.AddThemeColorOverride("font_color", Cream);
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        b.AddThemeColorOverride("font_pressed_color", Gold);
        b.AddThemeColorOverride("font_hover_pressed_color", Gold);
        b.AddThemeColorOverride("font_disabled_color", new Color(Cream, 0.28f));
        StyleBoxFlat Box(Color bg, Color border, int width)
        {
            var sb = new StyleBoxFlat { BgColor = bg, BorderColor = border, AntiAliasing = true };
            sb.SetBorderWidthAll(width);
            sb.SetCornerRadiusAll(8);
            return sb;
        }
        b.AddThemeStyleboxOverride("normal", Box(Colors.Transparent, Colors.Transparent, 0));
        b.AddThemeStyleboxOverride("hover", Box(new Color(1f, 1f, 1f, 0.07f), Colors.Transparent, 0));
        b.AddThemeStyleboxOverride("pressed", Box(new Color(Gold, 0.16f), new Color(Gold, 0.85f), 2));
        b.AddThemeStyleboxOverride("hover_pressed", Box(new Color(Gold, 0.22f), Gold, 2));
        b.AddThemeStyleboxOverride("disabled", Box(Colors.Transparent, Colors.Transparent, 0));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        return b;
    }

    static Label Text(string text, int size, Color color, Font? font, int outline, Color outlineColor)
    {
        var l = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        if (font is not null) l.AddThemeFontOverride("font", font);
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeConstantOverride("outline_size", outline);
        l.AddThemeColorOverride("font_outline_color", outlineColor);
        return l;
    }

    /// <summary>
    /// The top bar's own number font and outline (the gold counter), with the CJK font as a fallback for 自动.
    /// Falls back to the CJK font alone when the top bar cannot be read.
    /// </summary>
    static (Font? font, int outline, Color outlineColor) NativeText(Node? bar)
    {
        var cjk = SettingsPanel.LoadCjkFont();
        Font? native = null;
        int outline = 8;
        var outlineColor = new Color(0.08f, 0.07f, 0.06f);
        try
        {
            Node? gold = null;
            if (bar is not null && GodotObject.IsInstanceValid(bar))
                foreach (var n in bar.FindChildren("*", "", true, false))
                    if (n.GetType().Name == "NTopBarGold") { gold = n; break; }
            foreach (var n in gold?.FindChildren("*", "", true, false) ?? new Godot.Collections.Array<Node>())
            {
                if (n is Label l)
                {
                    native = l.LabelSettings?.Font ?? l.GetThemeFont("font");
                    outline = l.LabelSettings?.OutlineSize ?? l.GetThemeConstant("outline_size");
                    outlineColor = l.LabelSettings?.OutlineColor ?? l.GetThemeColor("font_outline_color");
                    break;
                }
                if (n is RichTextLabel r)
                {
                    native = r.GetThemeFont("normal_font");
                    outline = r.GetThemeConstant("outline_size");
                    outlineColor = r.GetThemeColor("font_outline_color");
                    break;
                }
            }
        }
        catch (Exception ex) { Plugin.Log("flight chip: top bar font unavailable: " + ex.Message); }
        outline = Math.Clamp(outline, 4, 12);
        if (native is null) return (cjk, outline, outlineColor);
        if (cjk is null) return (native, outline, outlineColor);
        var mixed = new FontVariation { BaseFont = native };
        mixed.Fallbacks = new Godot.Collections.Array<Font> { cjk };
        return (mixed, outline, outlineColor);
    }
}
