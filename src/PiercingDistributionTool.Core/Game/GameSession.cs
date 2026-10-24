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
            .TransformLoadOrderListings(l => l.Where(x => x.ModKey != ownPatch));
        if (!string.IsNullOrWhiteSpace(dataFolder))
            builder = builder.WithTargetDataFolder(dataFolder);
        return new GameSession(builder.Build(), ownPatch);
    }

    public IEnumerable<INpcGetter> WinningNpcs() =>
        _env.LoadOrder.PriorityOrder.Npc().WinningOverrides();

    public IEnumerable<IArmorAddonGetter> WinningArmorAddons() =>
        _env.LoadOrder.PriorityOrder.ArmorAddon().WinningOverrides();

    public void Dispose() => _env.Dispose();
}
