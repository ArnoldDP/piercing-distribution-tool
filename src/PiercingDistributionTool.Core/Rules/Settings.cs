using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiercingDistributionTool.Core.Rules;

public enum GenderFilter { Any, Female, Male }

/// <summary>A named group of piercing armour addons that are given out together.</summary>
public sealed class PiercingSet
{
    public string Name { get; set; } = "New set";
    /// <summary>ArmorAddon FormKeys, written "XXXXXX:Plugin.esp".</summary>
    public List<string> ArmorAddons { get; set; } = new();
    public GenderFilter Gender { get; set; } = GenderFilter.Any;
    /// <summary>Race EditorIDs this set may go to; empty means any race.</summary>
    public List<string> Races { get; set; } = new();
    /// <summary>Relative weight when an NPC qualifies for more than one set.</summary>
    public int Weight { get; set; } = 1;
}

public sealed class DistributionRules
{
    /// <summary>Percent of eligible NPCs that receive any piercing set.</summary>
    public double ChancePercent { get; set; } = 30;
    public bool IncludeUnique { get; set; } = true;
    public bool IncludeGeneric { get; set; } = true;
    /// <summary>Changing the seed reshuffles who gets what; the same seed always gives the same result.</summary>
    public int Seed { get; set; } = 1;
    /// <summary>NPC FormKey -> set name, or "" to force none. Wins over the random roll.</summary>
    public Dictionary<string, string> Overrides { get; set; } = new();
}

public sealed class ToolSettings
{
    public string? DataFolder { get; set; }
    public string OutputPluginName { get; set; } = "PiercingDistribution.esp";
    public string? OutputFolder { get; set; }
    public List<PiercingSet> Sets { get; set; } = new();
    public DistributionRules Rules { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PiercingDistributionTool", "settings.json");

    public static ToolSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new ToolSettings();
        return JsonSerializer.Deserialize<ToolSettings>(File.ReadAllText(path), Json) ?? new ToolSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Preset files hold only sets, so mod authors can ship one alongside a piercing mod.</summary>
    public static List<PiercingSet> LoadPreset(string path) =>
        JsonSerializer.Deserialize<List<PiercingSet>>(File.ReadAllText(path), Json) ?? new();

    public static void SavePreset(string path, IEnumerable<PiercingSet> sets) =>
        File.WriteAllText(path, JsonSerializer.Serialize(sets.ToList(), Json));
}
