using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace RouteSuggestPlus;

/// <summary>
/// Draws routes on the native map. One route on an edge recolors the native dots; several routes on the same
/// edge hide the native dots and draw parallel copies, spread symmetrically and in a fixed global order so
/// shared stretches never braid. The Recommend route gets a soft additive halo that flows toward the boss.
/// </summary>
public sealed class MapOverlay
{
    static readonly FieldInfo? PathsField = typeof(NMapScreen).GetField("_paths", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo? PointsField = typeof(NMapScreen).GetField("_mapPointDictionary", BindingFlags.NonPublic | BindingFlags.Instance);

    const string DotMeta = "route_suggest_plus_dot";
    const string FlightMeta = "route_suggest_plus_flight";
    readonly Dictionary<TextureRect, (Color modulate, Vector2 scale, bool visible)> _native = new();
    // What we last set on each native dot; Clear restores a property only if it still holds our value,
    // so changes the game made meanwhile (e.g. the traveled color) survive.
    readonly Dictionary<TextureRect, (Color modulate, Vector2 scale, bool visible)> _applied = new();
    readonly List<Node> _spawned = new();
    readonly List<(TextureRect halo, float phase, float baseAlpha)> _halos = new();
    readonly List<Control> _pinMarkers = new();
    CanvasItemMaterial? _additive;

    public int SpawnedDots => _spawned.Count(n => GodotObject.IsInstanceValid(n) && n.HasMeta(DotMeta));
    public int Halos => _halos.Count;
    public int Recolored => _native.Count(kv => GodotObject.IsInstanceValid(kv.Key) && kv.Key.Visible);
    public int Hidden => _native.Count(kv => GodotObject.IsInstanceValid(kv.Key) && !kv.Key.Visible);
    public int PinMarkers => _pinMarkers.Count;
    public int FlightDots => _spawned.Count(n => GodotObject.IsInstanceValid(n) && n.HasMeta(FlightMeta));

    public static bool Available => PathsField is not null && PointsField is not null;

    public void Render(NMapScreen screen, List<RouteResult> results, RouteSettings s, IReadOnlyCollection<MapCoord> pins)
    {
        Clear();
        if (PathsField?.GetValue(screen) is not IDictionary paths) return;
        DrawPins(screen, pins);

        // Global lane order: Recommend first, then by priority. Same order on every edge keeps lanes from crossing.
        var ordered = results.Where(r => r.Paths.Count > 0)
            .OrderByDescending(r => r.Config.Dynamic).ThenByDescending(r => r.Config.Priority).ToList();
        var edgeRoutes = new Dictionary<(MapCoord, MapCoord), List<RouteResult>>();
        foreach (var route in ordered)
            foreach (var path in route.Paths)
                for (int i = 0; i + 1 < path.Count; i++)
                {
                    var key = (path[i].coord, path[i + 1].coord);
                    if (!edgeRoutes.TryGetValue(key, out var list)) edgeRoutes[key] = list = new List<RouteResult>();
                    if (!list.Contains(route)) list.Add(route);
                }

        // Edge index along the Recommend route drives the halo phase so the glow flows toward the boss.
        var flow = new Dictionary<(MapCoord, MapCoord), int>();
        var rec = ordered.FirstOrDefault(r => r.Config.Dynamic);
        if (rec is not null)
        {
            var path = rec.Paths[0];
            for (int i = 0; i + 1 < path.Count; i++) flow[(path[i].coord, path[i + 1].coord)] = i;
        }

        foreach (var (edge, routes) in edgeRoutes)
        {
            if (!TryTicks(paths, edge, out var ticks) || ticks.Count == 0) continue;
            var drawn = s.Display.Lanes ? routes : routes.Take(1).ToList();
            if (drawn.Count == 1)
            {
                var r = drawn[0];
                foreach (var (tick, j) in ticks.Select((t, j) => (t, j)))
                {
                    if (!GodotObject.IsInstanceValid(tick)) continue;
                    Remember(tick);
                    tick.Modulate = r.Config.Color;
                    tick.Scale = Vector2.One * (r.Config.Dynamic ? 1.55f : 1.4f);
                    Applied(tick);
                    if (r.Config.Dynamic && s.Display.Glow) AddHalo(tick, tick.Position, 1.55f, Phase(flow, edge, j, ticks.Count));
                }
                continue;
            }

            Vector2 normal = Normal(ticks);
            float spacing = Math.Clamp(s.Display.LaneSpacing, 4f, 20f);
            for (int lane = 0; lane < drawn.Count; lane++)
            {
                var r = drawn[lane];
                float offset = (lane - (drawn.Count - 1) / 2f) * spacing;
                float scale = r.Config.Dynamic ? 1.3f : 1.15f;
                for (int j = 0; j < ticks.Count; j++)
                {
                    var tick = ticks[j];
                    if (!GodotObject.IsInstanceValid(tick)) continue;
                    Remember(tick);
                    tick.Visible = false;
                    Applied(tick);
                    float t = (j + 1f) / (ticks.Count + 1f);
                    float taper = Math.Clamp(Math.Min(t, 1f - t) / 0.2f, 0.45f, 1f); // lanes converge into the nodes
                    var pos = tick.Position + normal * offset * taper;
                    if (r.Config.Dynamic && s.Display.Glow) AddHalo(tick, pos, scale, Phase(flow, edge, j, ticks.Count));
                    var copy = (TextureRect)tick.Duplicate();
                    copy.Name = "RouteSuggestPlusDot";
                    tick.GetParent().AddChild(copy);
                    // Position after entering the tree: dots are center-anchored, so the anchors would shift a preset position.
                    copy.Position = pos;
                    copy.Modulate = r.Config.Color;
                    copy.Scale = Vector2.One * scale;
                    copy.Visible = true;
                    copy.SetMeta(DotMeta, true);
                    _spawned.Add(copy);
                }
            }
        }
        if (rec?.Jumped is { } jumped) DrawFlights(screen, paths, rec, jumped, s);
    }

    /// <summary>
    /// Winged Boots flights have no native dotted line: draw a gold arc of dots between the two rooms, bowed to one
    /// side so it reads as a jump rather than a path, with the same flowing glow as the Recommend route.
    /// </summary>
    void DrawFlights(NMapScreen screen, IDictionary paths, RouteResult rec, List<bool> jumped, RouteSettings s)
    {
        if (PointsField?.GetValue(screen) is not IDictionary points) return;
        TextureRect? template = null;
        foreach (var value in paths.Values)
            if (value is IReadOnlyList<TextureRect> { Count: > 0 } ticks && GodotObject.IsInstanceValid(ticks[0])) { template = ticks[0]; break; }
        if (template?.GetParent() is not CanvasItem layer) return;
        var path = rec.Paths[0];
        var toLayer = layer.GetGlobalTransformWithCanvas().AffineInverse();
        Vector2 Center(NMapPoint p) => toLayer * (p.GetGlobalTransformWithCanvas() * (p.Size * 0.5f));
        for (int i = 1; i < path.Count && i < jumped.Count; i++)
        {
            if (!jumped[i]) continue;
            if (points[path[i - 1].coord] is not NMapPoint a || points[path[i].coord] is not NMapPoint b) continue;
            var from = Center(a);
            var to = Center(b);
            if ((to - from).Length() < 1f) continue;
            // Rooms the arc must keep away from, so it is not misread as passing through them or clicked by mistake.
            var others = new List<Vector2>();
            foreach (var value in points.Values)
                if (value is NMapPoint other && other != a && other != b && GodotObject.IsInstanceValid(other) && other.Visible)
                    others.Add(Center(other));
            var corner = toLayer * (a.GetGlobalTransformWithCanvas() * a.Size) - toLayer * (a.GetGlobalTransformWithCanvas() * Vector2.Zero);
            float radius = Math.Max(18f, corner.Length() * 0.36f); // about the drawn icon
            var curve = FlightCurve(from, to, others, radius + 16f);
            float length = 0f;
            var prev = from;
            for (int k = 1; k <= 24; k++) { var q = curve(k / 24f).pos; length += (q - prev).Length(); prev = q; }
            int count = Math.Max(3, (int)(length / 28f));
            for (int k = 1; k < count; k++)
            {
                float t = k / (float)count;
                var (pos, tangent) = curve(t);
                if ((pos - from).Length() < radius + 8f || (pos - to).Length() < radius + 8f) continue; // keep clear of the end rooms
                if (others.Any(o => (pos - o).Length() < radius + 6f)) continue; // never draw on top of another room
                var dot = (TextureRect)template.Duplicate();
                dot.Name = "RouteSuggestPlusFlight";
                dot.MouseFilter = Control.MouseFilterEnum.Ignore;
                layer.AddChild(dot);
                dot.Position = pos;
                dot.Rotation = tangent.Angle() + MathF.PI / 2f;
                dot.Modulate = rec.Config.Color;
                dot.Scale = Vector2.One * 1.45f;
                dot.Visible = true;
                dot.SetMeta(DotMeta, true);
                dot.SetMeta(FlightMeta, true);
                _spawned.Add(dot);
                if (s.Display.Glow) AddHalo(dot, pos, 1.45f, (i + t) * 0.9f);
            }
        }
    }

    /// <summary>
    /// Picks the flight arc: candidates from a gentle upward bow to wider bows on either side and S-bends; the first
    /// (least bent, preferring the boss side) that stays <paramref name="clear"/> away from every other room wins,
    /// otherwise the one with the most room. Returns position and tangent along the curve for t in [0, 1].
    /// </summary>
    internal static Func<float, (Vector2 pos, Vector2 tangent)> FlightCurve(Vector2 from, Vector2 to, IReadOnlyList<Vector2> others, float clear)
    {
        var dir = to - from;
        float length = dir.Length();
        var normal = new Vector2(-dir.Y, dir.X) / Math.Max(length, 1e-3f);
        if (normal.Y > 0) normal = -normal; // up = toward the boss
        var candidates = new List<(Vector2 c1, Vector2 c2)>();
        void Bow(float f) // quadratic bow written as a cubic
        {
            var control = (from + to) * 0.5f + normal * length * f;
            candidates.Add((from + (control - from) * (2f / 3f), to + (control - to) * (2f / 3f)));
        }
        void Bend(float f) => candidates.Add((from + dir / 3f + normal * length * f, from + dir * (2f / 3f) - normal * length * f));
        Bow(0.18f); Bow(0.08f); Bow(0.3f); Bow(-0.12f); Bow(0.42f); Bow(-0.25f);
        Bend(0.22f); Bend(-0.22f); Bow(0.55f); Bow(-0.4f); Bend(0.38f); Bend(-0.38f);

        (Vector2 pos, Vector2 tangent) At((Vector2 c1, Vector2 c2) c, float t)
        {
            float u = 1f - t;
            var pos = u * u * u * from + 3f * u * u * t * c.c1 + 3f * u * t * t * c.c2 + t * t * t * to;
            var tangent = 3f * u * u * (c.c1 - from) + 6f * u * t * (c.c2 - c.c1) + 3f * t * t * (to - c.c2);
            return (pos, tangent);
        }
        float Clearance((Vector2, Vector2) c)
        {
            float best = float.MaxValue;
            for (int k = 1; k < 40; k++)
            {
                var p = At(c, k / 40f).pos;
                foreach (var o in others) best = Math.Min(best, (p - o).Length());
            }
            return best;
        }
        var pick = candidates[0];
        float pickClear = float.MinValue;
        foreach (var c in candidates)
        {
            float room = Clearance(c);
            if (room >= clear) { pick = c; break; }
            if (room > pickClear + 1f) { pick = c; pickClear = room; }
        }
        return t => At(pick, t);
    }

    /// <summary>Pulses the Recommend halo. Called every frame; cheap when nothing is drawn.</summary>
    public void Tick(double seconds)
    {
        _now = seconds;
        if (Toasts.Count > 0) FadeToasts(seconds);
        if (_strokes.Count > 0) AnimateStrokes(seconds);
        if (_halos.Count == 0) return;
        float now = (float)seconds;
        foreach (var (halo, phase, baseAlpha) in _halos)
        {
            if (!GodotObject.IsInstanceValid(halo)) continue;
            float wave = 0.5f + 0.5f * MathF.Sin(now * MathF.Tau / 1.6f - phase);
            var c = halo.Modulate;
            halo.Modulate = new Color(c.R, c.G, c.B, baseAlpha * (0.45f + 0.75f * wave));
        }
    }

    public void Clear()
    {
        foreach (var (tick, (modulate, scale, visible)) in _native)
        {
            if (!GodotObject.IsInstanceValid(tick) || !_applied.TryGetValue(tick, out var mine)) continue;
            if (tick.Modulate == mine.modulate) tick.Modulate = modulate;
            if (tick.Scale == mine.scale) tick.Scale = scale;
            if (tick.Visible == mine.visible) tick.Visible = visible;
        }
        _native.Clear();
        _applied.Clear();
        foreach (var node in _spawned.Concat(_pinMarkers))
            if (GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion()) node.QueueFree();
        _spawned.Clear();
        _halos.Clear();
        _pinMarkers.Clear();
        _strokes.Clear();
    }

    static readonly List<(Control box, double born)> Toasts = new();
    const double ToastHold = 2.8, ToastFade = 0.6;

    static void FadeToasts(double now)
    {
        for (int i = Toasts.Count - 1; i >= 0; i--)
        {
            var (box, born) = Toasts[i];
            double age = now - born;
            if (!GodotObject.IsInstanceValid(box) || age >= ToastHold + ToastFade)
            {
                if (GodotObject.IsInstanceValid(box) && !box.IsQueuedForDeletion()) box.QueueFree();
                Toasts.RemoveAt(i);
                continue;
            }
            float alpha = age <= ToastHold ? 1f : (float)(1.0 - (age - ToastHold) / ToastFade);
            box.Modulate = new Color(1, 1, 1, alpha);
        }
    }

    /// <summary>Shows a short message at the top of the map that fades out by itself.</summary>
    public static void Toast(NMapScreen screen, string text)
    {
        var box = new PanelContainer { Name = "RouteSuggestPlusToast", MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat { BgColor = new Color(0.065f, 0.072f, 0.10f, 0.92f), BorderColor = new Color(0.96f, 0.78f, 0.42f, 0.9f), AntiAliasing = true };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(10);
        style.ContentMarginLeft = style.ContentMarginRight = 22;
        style.ContentMarginTop = style.ContentMarginBottom = 12;
        box.AddThemeStyleboxOverride("panel", style);
        var label = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", 26);
        label.AddThemeColorOverride("font_color", new Color(1f, 0.93f, 0.8f));
        var font = SettingsPanel.LoadCjkFont();
        if (font is not null) label.AddThemeFontOverride("font", font);
        box.AddChild(label);
        screen.AddChild(box);
        box.ResetSize();
        var size = box.GetCombinedMinimumSize();
        box.Position = new Vector2((screen.Size.X - size.X) * 0.5f, 140f);
        // Faded by Tick rather than a Tween: tweens bound to nodes on the map screen did not advance in play.
        Toasts.Add((box, Time.GetTicksMsec() / 1000.0));
    }

    void Remember(TextureRect tick)
    {
        if (!_native.ContainsKey(tick)) _native[tick] = (tick.Modulate, tick.Scale, tick.Visible);
    }

    void Applied(TextureRect tick) => _applied[tick] = (tick.Modulate, tick.Scale, tick.Visible);

    void AddHalo(TextureRect tick, Vector2 position, float scale, float phase)
    {
        _additive ??= new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
        var halo = (TextureRect)tick.Duplicate();
        halo.Name = "RouteSuggestPlusGlow";
        if (halo.HasMeta(DotMeta)) halo.RemoveMeta(DotMeta);
        if (halo.HasMeta(FlightMeta)) halo.RemoveMeta(FlightMeta);
        var parent = tick.GetParent();
        parent.AddChild(halo);
        parent.MoveChild(halo, 0); // behind every dot
        halo.Position = position;
        halo.Scale = Vector2.One * scale * 2.0f;
        halo.Material = _additive;
        halo.Visible = true;
        const float baseAlpha = 0.42f;
        halo.Modulate = new Color(1f, 0.78f, 0.2f, baseAlpha);
        _spawned.Add(halo);
        _halos.Add((halo, phase, baseAlpha));
    }

    static float Phase(Dictionary<(MapCoord, MapCoord), int> flow, (MapCoord, MapCoord) edge, int tick, int count)
        => flow.TryGetValue(edge, out int index) ? (index + (tick + 1f) / (count + 1f)) * 0.9f : 0f;

    /// <summary>Perpendicular of the edge direction. Edges always run from the lower row upward, so the side is consistent.</summary>
    static Vector2 Normal(IReadOnlyList<TextureRect> ticks)
    {
        Vector2 dir = ticks.Count >= 2
            ? (ticks[^1].Position - ticks[0].Position)
            : Vector2.FromAngle(ticks[0].Rotation - MathF.PI / 2f);
        dir = dir.LengthSquared() < 1e-4f ? Vector2.Up : dir.Normalized();
        return new Vector2(-dir.Y, dir.X);
    }

    static bool TryTicks(IDictionary paths, (MapCoord, MapCoord) edge, out IReadOnlyList<TextureRect> ticks)
    {
        ticks = Array.Empty<TextureRect>();
        object? value = paths.Contains(edge) ? paths[edge] : null;
        if (value is IReadOnlyList<TextureRect> forward) { ticks = forward; return true; }
        var reverse = (edge.Item2, edge.Item1);
        if (paths.Contains(reverse) && paths[reverse] is IReadOnlyList<TextureRect> back)
        {
            ticks = back.Reverse().ToList();
            return true;
        }
        return false;
    }

    /// <summary>The map node under a canvas-space position, if any (nodes do not take mouse events themselves).</summary>
    public static NMapPoint? PointAt(NMapScreen screen, Vector2 globalPosition)
    {
        if (PointsField?.GetValue(screen) is not IDictionary points) return null;
        NMapPoint? best = null;
        float bestDistance = float.MaxValue;
        foreach (var value in points.Values)
        {
            if (value is not NMapPoint point || !GodotObject.IsInstanceValid(point) || !point.IsVisibleInTree()) continue;
            var size = point.Size * point.GetGlobalTransformWithCanvas().Scale;
            var center = point.GetGlobalTransformWithCanvas() * (point.Size * 0.5f);
            float distance = center.DistanceTo(globalPosition);
            float radius = Math.Max(Math.Abs(size.X), Math.Abs(size.Y)) * 0.6f;
            if (distance <= radius && distance < bestDistance) { best = point; bestDistance = distance; }
        }
        return best;
    }

    // Hand-drawn pin: an ink ring like a pen circle on a paper map, with the character's portrait tucked
    // at its lower left. New pins animate the stroke being drawn; existing ones redraw instantly.
    static readonly Color Ink = new(0.58f, 0.12f, 0.09f, 0.92f);
    static readonly Color InkWash = new(0.58f, 0.12f, 0.09f, 0.28f);
    const float DrawSeconds = 0.35f;
    readonly Dictionary<MapCoord, double> _pinBorn = new();
    readonly List<(Line2D line, Vector2[] points, MapCoord coord)> _strokes = new();
    double _now;

    /// <summary>Supplies a fresh portrait node for the local character (null if unavailable).</summary>
    public Func<Control?>? Portrait { get; set; }

    void DrawPins(NMapScreen screen, IReadOnlyCollection<MapCoord> pins)
    {
        foreach (var gone in _pinBorn.Keys.Where(k => !pins.Contains(k)).ToList()) _pinBorn.Remove(gone);
        if (pins.Count == 0 || PointsField?.GetValue(screen) is not IDictionary points) return;
        foreach (var coord in pins)
        {
            if (!points.Contains(coord) || points[coord] is not NMapPoint point || !GodotObject.IsInstanceValid(point)) continue;
            if (!_pinBorn.ContainsKey(coord)) _pinBorn[coord] = _now;
            var holder = new Control { Name = "RouteSuggestPlusPin", MouseFilter = Control.MouseFilterEnum.Ignore }; // no ZIndex: game popups must cover it
            point.AddChild(holder);
            holder.Position = point.Size * 0.5f;
            var rng = new RandomNumberGenerator { Seed = (ulong)(coord.row * 97 + coord.col * 13 + 7) };
            float radius = Math.Max(point.Size.X, point.Size.Y) * 0.62f + 4f;
            // A faint wide wash first, then the main stroke: reads as ink soaking into paper.
            AddStroke(holder, coord, Ring(rng, radius + 1.5f, 1.08f), InkWash, 11f);
            AddStroke(holder, coord, Ring(rng, radius, 1.16f), Ink, 5.5f);
            AddPortrait(holder, radius);
            _pinMarkers.Add(holder);
        }
    }

    /// <summary>A slightly lopsided loop that overshoots its start, like a quick pen circle.</summary>
    static Vector2[] Ring(RandomNumberGenerator rng, float radius, float turns)
    {
        const int steps = 56;
        float start = rng.RandfRange(-2.4f, -1.6f);             // begin around the upper left
        float squash = rng.RandfRange(0.86f, 0.95f);            // a bit wider than tall
        float tilt = rng.RandfRange(-0.25f, 0.25f);
        float drift = rng.RandfRange(4f, 8f);                   // the overshoot ends off the start
        float w1 = rng.RandfRange(0f, MathF.Tau), w2 = rng.RandfRange(0f, MathF.Tau);
        var pts = new Vector2[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            float a = start + t * turns * MathF.Tau;
            float r = radius * (1f + 0.035f * MathF.Sin(3f * a + w1) + 0.02f * MathF.Sin(5f * a + w2)) + drift * t;
            var v = new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r * squash);
            pts[i] = v.Rotated(tilt);
        }
        return pts;
    }

    void AddStroke(Control holder, MapCoord coord, Vector2[] points, Color color, float width)
    {
        var line = new Line2D
        {
            Width = width,
            DefaultColor = color,
            JointMode = Line2D.LineJointMode.Round,
            BeginCapMode = Line2D.LineCapMode.Round,
            EndCapMode = Line2D.LineCapMode.Round,
            Antialiased = true,
            WidthCurve = BrushCurve(),
        };
        holder.AddChild(line);
        bool fresh = _now - _pinBorn[coord] < DrawSeconds;
        line.Points = fresh ? points.Take(2).ToArray() : points;
        if (fresh) _strokes.Add((line, points, coord));
    }

    static Curve? _brush;
    static Curve BrushCurve()
    {
        if (_brush is not null) return _brush;
        _brush = new Curve();
        _brush.AddPoint(new Vector2(0f, 0.35f)); // light touch-down
        _brush.AddPoint(new Vector2(0.15f, 1f));
        _brush.AddPoint(new Vector2(0.8f, 0.9f));
        _brush.AddPoint(new Vector2(1f, 0.2f));  // flick off
        return _brush;
    }

    void AddPortrait(Control holder, float radius)
    {
        Control? icon = null;
        try { icon = Portrait?.Invoke(); } catch { }
        if (icon is null) return;
        const float size = 36f;
        var frame = new Panel { Name = "PinPortrait", MouseFilter = Control.MouseFilterEnum.Ignore, Size = new Vector2(size, size) };
        // A small parchment disc with an ink rim, like a sticker on the map.
        var disc = new StyleBoxFlat { BgColor = new Color(0.86f, 0.76f, 0.56f, 0.95f), BorderColor = Ink, AntiAliasing = true };
        disc.SetBorderWidthAll(2);
        disc.SetCornerRadiusAll((int)(size / 2));
        frame.AddThemeStyleboxOverride("panel", disc);
        holder.AddChild(frame);
        frame.Position = new Vector2(-radius * 0.74f, radius * 0.5f) - new Vector2(size, size) * 0.5f;
        frame.AddChild(icon);
        icon.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var c in icon.FindChildren("*", "Control", true, false).OfType<Control>()) c.MouseFilter = Control.MouseFilterEnum.Ignore;
        // Scale the native icon uniformly to fit; never crop it.
        var native = icon.Size.X > 1 ? icon.Size : icon.GetCombinedMinimumSize();
        float inner = size - 6f;
        float k = native.X > 1 && native.Y > 1 ? inner / Math.Max(native.X, native.Y) : 0.3f;
        icon.Scale = new Vector2(k, k);
        icon.Position = (new Vector2(size, size) - native * k) * 0.5f;
    }

    void AnimateStrokes(double now)
    {
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var (line, points, coord) = _strokes[i];
            if (!GodotObject.IsInstanceValid(line) || !_pinBorn.TryGetValue(coord, out var born)) { _strokes.RemoveAt(i); continue; }
            float t = (float)Math.Clamp((now - born) / DrawSeconds, 0.0, 1.0);
            float eased = 1f - (1f - t) * (1f - t);
            int count = Math.Max(2, (int)MathF.Ceiling(eased * points.Length));
            line.Points = points.Take(count).ToArray();
            if (t >= 1f) _strokes.RemoveAt(i);
        }
    }
}
