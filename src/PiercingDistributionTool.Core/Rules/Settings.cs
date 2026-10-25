using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiercingDistributionTool.Core.Rules;

/// <summary>A named group of piercing armour addons that are given to an NPC together.</summary>
public sealed class PiercingSet
{
    public string Name { get; set; } = "New set";
    /// <summary>ArmorAddon FormKeys, written "XXXXXX:Plugin.esp".</summary>
    public List<string> ArmorAddons { get; set; } = new();
}

public sealed class DistributionRules
{
    /// <summary>NPC FormKey -> set name. Only NPCs listed here get piercings.</summary>
    public Dictionary<string, string> Overrides { get; set; } = new();
}

public sealed class ToolSettings
{
    /// <summary>Bake piercings into a copy of the body mesh so body armor hides them with the body.</summary>
    public bool MergeIntoBody { get; set; } = true;
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
