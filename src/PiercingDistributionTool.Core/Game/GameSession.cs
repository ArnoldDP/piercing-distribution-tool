using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace PiercingDistributionTool.Core.Game;

/// <summary>
/// The user's load order, read once. Launched from MO2 this sees the virtual Data folder and the
/// profile's plugins.txt; under Vortex it reads the real Data folder directly.
/// </summary>
public sealed class GameSession : IDisposable
{
    private readonly IGameEnvironment<ISkyrimMod, ISkyrimModGetter> _env;

    public ILinkCache<ISkyrimMod, ISkyrimModGetter> LinkCache => _env.LinkCache;
    public string DataFolder => _env.DataFolderPath.Path;
    public IReadOnlyList<ModKey> LoadOrder { get; }

    private GameSession(IGameEnvironment<ISkyrimMod, ISkyrimModGetter> env, ModKey? exclude)
    {
        _env = env;
        LoadOrder = env.LoadOrder.ListedOrder.Select(l => l.ModKey).Where(k => k != exclude).ToList();
    }

    /// <param name="dataFolder">Optional override; null auto-detects the installed Skyrim SE.</param>
    /// <param name="ownPatch">The tool's own output plugin, left out so a re-run starts from scratch.</param>
    public static GameSession Open(string? dataFolder, ModKey ownPatch)
    {
        var builder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE)
            // Only ticked plugins load in game; unticked ones would otherwise win conflicts here.
            .TransformLoadOrderListings(l => l.Where(x => x.Enabled && x.ModKey != ownPatch));
        if (string.IsNullOrWhiteSpace(dataFolder)) dataFolder = Mo2DataFolder();
        if (!string.IsNullOrWhiteSpace(dataFolder))
            builder = builder.WithTargetDataFolder(dataFolder);
        return new GameSession(builder.Build(), ownPatch);
    }

    /// <summary>
    /// When MO2 launched us, its virtual Data folder sits over the game folder MO2 manages (often a
    /// "Stock Game" copy), which Steam's registry entry doesn't know about. MO2 injects its usvfs DLL
    /// from its own folder, and a portable instance keeps ModOrganizer.ini beside it, naming gamePath.
    /// </summary>
    public static string? Mo2DataFolder()
    {
        try
        {
            var usvfs = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                .FirstOrDefault(m => m.ModuleName.StartsWith("usvfs_x64", StringComparison.OrdinalIgnoreCase));
            if (usvfs?.FileName is not { } dll) return null;
            var ini = Path.Combine(Path.GetDirectoryName(dll)!, "ModOrganizer.ini");
            if (!File.Exists(ini)) return null;
            var line = File.ReadLines(ini).FirstOrDefault(l => l.StartsWith("gamePath=", StringComparison.OrdinalIgnoreCase));
            if (line == null) return null;
            var value = line["gamePath=".Length..].Trim();
            if (value.StartsWith("@ByteArray(") && value.EndsWith(")")) value = value[11..^1];
            value = value.Replace(@"\\", @"\");
            var data = Path.Combine(value, "Data");
            return Directory.Exists(data) ? data : null;
        }
        catch
        {
            return null;
        }
    }

    public IEnumerable<INpcGetter> WinningNpcs() =>
        _env.LoadOrder.PriorityOrder.Npc().WinningOverrides();

    public IEnumerable<IArmorGetter> WinningArmors() =>
        _env.LoadOrder.PriorityOrder.Armor().WinningOverrides();

    public IEnumerable<IArmorAddonGetter> WinningArmorAddons() =>
        _env.LoadOrder.PriorityOrder.ArmorAddon().WinningOverrides();

    public void Dispose() => _env.Dispose();
}
