// Path configuration and scoring for the classic routes are derived from
// RouteSuggest by Jiajie Chen (https://github.com/jiegec/STS2RouteSuggest), MIT License.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Map;

namespace RouteSuggestPlus;

/// <summary>One highlighted route. Classic routes sum fixed weights; the dynamic route runs <see cref="Planner"/>.</summary>
public sealed class PathConfig
{
    public string Name { get; set; } = "";
    public Color Color { get; set; } = Colors.White;
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>True for the state-aware Recommend route.</summary>
    public bool Dynamic { get; set; }
    public Dictionary<MapPointType, int> ScoringWeights { get; set; } = new();
    public int RestBeforeEliteBonus { get; set; }
    public int EliteBeforeRestBonus { get; set; }
    public int TreasureBeforeEliteBonus { get; set; }
    public int RestTwoBeforeEliteBonus { get; set; }
    public int EliteTwoBeforeRestBonus { get; set; }
    public int ZeroShopsBonus { get; set; }
    public int OneShopBonus { get; set; }
    public int TwoShopsBonus { get; set; }

    public int CalculateScore(List<MapPoint> path, bool expertMode)
    {
        int score = 0;
        foreach (var point in path)
            if (ScoringWeights.TryGetValue(point.PointType, out int weight)) score += weight;
        if (expertMode) score += AdjacencyBonuses(path);
        return score;
    }

    int AdjacencyBonuses(List<MapPoint> path)
    {
        int bonus = 0;
        for (int i = 0; i + 1 < path.Count; i++)
        {
            var (curr, next) = (path[i].PointType, path[i + 1].PointType);
            if (curr == MapPointType.RestSite && next == MapPointType.Elite) bonus += RestBeforeEliteBonus;
            if (curr == MapPointType.Elite && next == MapPointType.RestSite) bonus += EliteBeforeRestBonus;
            if (curr == MapPointType.Treasure && next == MapPointType.Elite) bonus += TreasureBeforeEliteBonus;
        }
        for (int i = 0; i + 2 < path.Count; i++)
        {
            var (curr, after) = (path[i].PointType, path[i + 2].PointType);
            if (curr == MapPointType.RestSite && after == MapPointType.Elite) bonus += RestTwoBeforeEliteBonus;
            if (curr == MapPointType.Elite && after == MapPointType.RestSite) bonus += EliteTwoBeforeRestBonus;
        }
        int shops = path.Skip(1).Count(p => p.PointType == MapPointType.Shop);
        bonus += shops switch { 0 => ZeroShopsBonus, 1 => OneShopBonus, 2 => TwoShopsBonus, _ => 0 };
        return bonus;
    }

    public PathConfig Clone() => new()
    {
        Name = Name, Color = Color, Priority = Priority, Enabled = Enabled, Dynamic = Dynamic,
        ScoringWeights = new Dictionary<MapPointType, int>(ScoringWeights),
        RestBeforeEliteBonus = RestBeforeEliteBonus, EliteBeforeRestBonus = EliteBeforeRestBonus,
        TreasureBeforeEliteBonus = TreasureBeforeEliteBonus, RestTwoBeforeEliteBonus = RestTwoBeforeEliteBonus,
        EliteTwoBeforeRestBonus = EliteTwoBeforeRestBonus, ZeroShopsBonus = ZeroShopsBonus,
        OneShopBonus = OneShopBonus, TwoShopsBonus = TwoShopsBonus,
    };
}

/// <summary>Display options that are not part of route scoring.</summary>
public sealed class DisplayOptions
{
    public bool Lanes { get; set; } = true;
    public float LaneSpacing { get; set; } = 11f;
    public bool Glow { get; set; } = true;
    public bool PinsEnabled { get; set; } = true;
    /// <summary>Settings panel position after the player drags it; negative means the default spot.</summary>
    public float PanelX { get; set; } = -1f;
    public float PanelY { get; set; } = -1f;
    /// <summary>Map button position after the player drags it; negative means the default bottom-right corner.</summary>
    public float ButtonX { get; set; } = -1f;
    public float ButtonY { get; set; } = -1f;
    /// <summary>Winged Boots chip position on the map; negative means the default bottom-left spot.</summary>
    public float FlightX { get; set; } = -1f;
    public float FlightY { get; set; } = -1f;
    /// <summary>Godot key name for the settings panel.</summary>
    public string Hotkey { get; set; } = "F7";
}

public sealed class RouteSettings
{
    public const string RecommendName = "Recommend";
    public static readonly Color RecommendColor = new(1f, 0.84f, 0f);
    static readonly Color SafeColor = new(0.494f, 0.784f, 1f);

    public bool ExpertMode { get; set; }
    public bool HighlightAll { get; set; }
    public List<PathConfig> Paths { get; set; } = new();
    public PlanParams Plan { get; set; } = new();
    public DisplayOptions Display { get; set; } = new();
    /// <summary>Potion id → value override (e.g. FoulPotion is an Event potion but not a strong one).</summary>
    public Dictionary<string, float> PotionValueOverrides { get; set; } = new() { ["FoulPotion"] = 0.5f, ["PotionShapedRock"] = 0f };
    public Dictionary<string, float> PotionRarityValues { get; set; } = new()
    {
        ["Common"] = 0.5f, ["Uncommon"] = 1f, ["Rare"] = 1f, ["Event"] = 1f, ["Token"] = 0.5f,
    };

    public PathConfig? Recommend => Paths.FirstOrDefault(p => p.Dynamic);

    public static PathConfig DefaultRecommend() => new()
    {
        Name = RecommendName, Color = RecommendColor, Priority = 200, Dynamic = true,
    };

    public static RouteSettings Defaults()
    {
        var s = new RouteSettings();
        s.Paths.Add(DefaultRecommend());
        s.Paths.Add(new PathConfig
        {
            Name = "Safe", Color = SafeColor, Priority = 100,
            ScoringWeights = new() { [MapPointType.RestSite] = 1, [MapPointType.Treasure] = 1, [MapPointType.Shop] = 1,
                [MapPointType.Monster] = -1, [MapPointType.Elite] = -3, [MapPointType.Unknown] = 0 },
        });
        s.Paths.Add(new PathConfig
        {
            Name = "Aggressive", Color = new Color(1f, 0f, 0f), Priority = 50,
            ScoringWeights = new() { [MapPointType.RestSite] = 1, [MapPointType.Treasure] = 1, [MapPointType.Shop] = 1,
                [MapPointType.Monster] = 2, [MapPointType.Elite] = 3, [MapPointType.Unknown] = 2 },
        });
        return s;
    }

    /// <summary>Gold is reserved for Recommend: a classic route that used it is moved to blue.</summary>
    public void EnsureRecommend()
    {
        if (Recommend is null) Paths.Insert(0, DefaultRecommend());
        foreach (var p in Paths.Where(p => !p.Dynamic && IsGoldish(p.Color))) p.Color = SafeColor;
    }

    static bool IsGoldish(Color c) => c.R > 0.9f && c.G > 0.75f && c.G < 0.9f && c.B < 0.15f;
}

/// <summary>Reads and writes RouteSuggestPlusConfig.json next to the mod DLL; imports an upstream config once.</summary>
public static class ConfigStore
{
    public const string FileName = "RouteSuggestPlusConfig.json";
    const string LegacyFileName = "RouteSuggestConfig.json";

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        IncludeFields = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ModDirectory => Path.GetDirectoryName(typeof(ConfigStore).Assembly.Location) ?? "";
    public static string ConfigPath => Path.Combine(ModDirectory, FileName);

    public static RouteSettings Load(out string? note)
    {
        note = null;
        try
        {
            if (File.Exists(ConfigPath))
            {
                var file = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(ConfigPath), Options);
                if (file is not null)
                {
                    var loaded = FromFile(file);
                    var reset = Validate(loaded);
                    if (reset.Count > 0) note = "配置中以下项无效，已用默认值：" + string.Join("、", reset);
                    return loaded;
                }
            }
            string? legacy = FindLegacy();
            var settings = legacy is null ? RouteSettings.Defaults() : FromFile(JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(legacy), Options) ?? new ConfigFile());
            if (legacy is not null) note = "已导入原版配置：" + legacy;
            settings.EnsureRecommend();
            Validate(settings);
            Save(settings);
            return settings;
        }
        catch (Exception ex)
        {
            note = "配置读取失败，使用默认值：" + ex.Message;
            return RouteSettings.Defaults();
        }
    }

    public static void Save(RouteSettings s)
    {
        var file = new ConfigFile
        {
            SchemaVersion = 5,
            HighlightType = s.HighlightAll ? "All" : "One",
            ExpertMode = s.ExpertMode,
            PathConfigs = s.Paths.Select(p => new PathConfigEntry
            {
                Name = p.Name, Color = "#" + p.Color.ToHtml(false), Priority = p.Priority, Enabled = p.Enabled, Dynamic = p.Dynamic,
                ScoringWeights = p.Dynamic ? null : p.ScoringWeights.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                RestBeforeEliteBonus = p.RestBeforeEliteBonus, EliteBeforeRestBonus = p.EliteBeforeRestBonus,
                TreasureBeforeEliteBonus = p.TreasureBeforeEliteBonus, RestTwoBeforeEliteBonus = p.RestTwoBeforeEliteBonus,
                EliteTwoBeforeRestBonus = p.EliteTwoBeforeRestBonus, ZeroShopsBonus = p.ZeroShopsBonus,
                OneShopBonus = p.OneShopBonus, TwoShopsBonus = p.TwoShopsBonus,
            }).ToList(),
            Plus = new PlusSection
            {
                Plan = s.Plan, Display = s.Display,
                PotionValueOverrides = s.PotionValueOverrides, PotionRarityValues = s.PotionRarityValues,
            },
        };
        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Options));
        File.Move(tmp, ConfigPath, true);
    }

    /// <summary>Repairs hand-edited values so the planner and panel never see broken arrays or ranges.</summary>
    public static List<string> Validate(RouteSettings s)
    {
        s.Plan ??= new PlanParams();
        s.Display ??= new DisplayOptions();
        var reset = s.Plan.Normalize();
        if (!float.IsFinite(s.Display.LaneSpacing) || s.Display.LaneSpacing < 4f || s.Display.LaneSpacing > 20f)
        {
            s.Display.LaneSpacing = new DisplayOptions().LaneSpacing;
            reset.Add("lane_spacing");
        }
        if (string.IsNullOrWhiteSpace(s.Display.Hotkey)) s.Display.Hotkey = "F7";
        if (!float.IsFinite(s.Display.PanelX) || !float.IsFinite(s.Display.PanelY)) s.Display.PanelX = s.Display.PanelY = -1f;
        if (!float.IsFinite(s.Display.ButtonX) || !float.IsFinite(s.Display.ButtonY)) s.Display.ButtonX = s.Display.ButtonY = -1f;
        if (!float.IsFinite(s.Display.FlightX) || !float.IsFinite(s.Display.FlightY)) s.Display.FlightX = s.Display.FlightY = -1f;
        var defaults = new RouteSettings();
        if (s.PotionValueOverrides is null) { s.PotionValueOverrides = defaults.PotionValueOverrides; reset.Add("potion_value_overrides"); }
        if (s.PotionRarityValues is null) { s.PotionRarityValues = defaults.PotionRarityValues; reset.Add("potion_rarity_values"); }
        foreach (var key in s.PotionValueOverrides.Where(kv => !float.IsFinite(kv.Value)).Select(kv => kv.Key).ToList())
            s.PotionValueOverrides.Remove(key);
        foreach (var (key, value) in defaults.PotionRarityValues)
            if (!s.PotionRarityValues.TryGetValue(key, out var v) || !float.IsFinite(v)) s.PotionRarityValues[key] = value;
        s.Paths.RemoveAll(p => p is null);
        foreach (var p in s.Paths) p.ScoringWeights ??= new Dictionary<MapPointType, int>();
        return reset;
    }

    static string? FindLegacy()
    {
        string mods = Path.GetDirectoryName(ModDirectory) ?? "";
        if (!Directory.Exists(mods)) return null;
        string direct = Path.Combine(mods, LegacyFileName);
        if (File.Exists(direct)) return direct;
        try { return Directory.GetFiles(mods, LegacyFileName, SearchOption.AllDirectories).FirstOrDefault(); }
        catch { return null; }
    }

    static RouteSettings FromFile(ConfigFile file)
    {
        var s = new RouteSettings
        {
            ExpertMode = file.ExpertMode,
            HighlightAll = string.Equals(file.HighlightType, "All", StringComparison.OrdinalIgnoreCase),
        };
        foreach (var e in file.PathConfigs ?? new List<PathConfigEntry>())
        {
            var weights = new Dictionary<MapPointType, int>();
            foreach (var kv in e.ScoringWeights ?? new Dictionary<string, int>())
                if (Enum.TryParse<MapPointType>(kv.Key, true, out var t)) weights[t] = kv.Value;
            s.Paths.Add(new PathConfig
            {
                Name = e.Name ?? "", Color = ParseColor(e.Color), Priority = e.Priority, Enabled = e.Enabled, Dynamic = e.Dynamic,
                ScoringWeights = weights,
                RestBeforeEliteBonus = e.RestBeforeEliteBonus, EliteBeforeRestBonus = e.EliteBeforeRestBonus,
                TreasureBeforeEliteBonus = e.TreasureBeforeEliteBonus, RestTwoBeforeEliteBonus = e.RestTwoBeforeEliteBonus,
                EliteTwoBeforeRestBonus = e.EliteTwoBeforeRestBonus, ZeroShopsBonus = e.ZeroShopsBonus,
                OneShopBonus = e.OneShopBonus, TwoShopsBonus = e.TwoShopsBonus,
            });
        }
        if (file.Plus is { } plus)
        {
            if (plus.Plan is not null) s.Plan = plus.Plan;
            if (plus.Display is not null) s.Display = plus.Display;
            if (plus.PotionValueOverrides is not null) s.PotionValueOverrides = plus.PotionValueOverrides;
            if (plus.PotionRarityValues is not null) s.PotionRarityValues = plus.PotionRarityValues;
        }
        if (s.Paths.Count == 0) s.Paths = RouteSettings.Defaults().Paths;
        s.EnsureRecommend();
        return s;
    }

    static Color ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Colors.White;
        try { return Color.FromHtml(text.Trim()); }
        catch { return Colors.White; }
    }

    sealed class ConfigFile
    {
        public int SchemaVersion { get; set; } = 5;
        public string? HighlightType { get; set; }
        public bool ExpertMode { get; set; }
        public List<PathConfigEntry>? PathConfigs { get; set; }
        public PlusSection? Plus { get; set; }
    }

    sealed class PlusSection
    {
        public PlanParams? Plan { get; set; }
        public DisplayOptions? Display { get; set; }
        public Dictionary<string, float>? PotionValueOverrides { get; set; }
        public Dictionary<string, float>? PotionRarityValues { get; set; }
    }

    sealed class PathConfigEntry
    {
        public string? Name { get; set; }
        public string? Color { get; set; }
        public int Priority { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Dynamic { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, int>? ScoringWeights { get; set; }
        public int RestBeforeEliteBonus { get; set; }
        public int EliteBeforeRestBonus { get; set; }
        public int TreasureBeforeEliteBonus { get; set; }
        public int RestTwoBeforeEliteBonus { get; set; }
        public int EliteTwoBeforeRestBonus { get; set; }
        public int ZeroShopsBonus { get; set; }
        public int OneShopBonus { get; set; }
        public int TwoShopsBonus { get; set; }
    }
}
