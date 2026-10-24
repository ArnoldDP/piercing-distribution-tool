using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace PiercingDistributionTool.Core.Game;

/// <summary>One NPC as the load order resolves it, with the skin it actually wears.</summary>
public sealed record NpcEntry(
    FormKey FormKey,
    string EditorId,
    string Name,
    FormKey RaceKey,
    string RaceEditorId,
    bool IsFemale,
    bool IsUnique,
    FormKey? SkinKey,
    bool SkinFromRace,
    string WinningPlugin,
    string? SkipReason)
{
    public string Display => string.IsNullOrEmpty(Name) ? EditorId : $"{Name} ({EditorId})";

    public static IReadOnlyList<NpcEntry> Collect(GameSession session)
    {
        var cache = session.LinkCache;
        var list = new List<NpcEntry>();
        foreach (var npc in session.WinningNpcs())
        {
            var flags = npc.Configuration.Flags;
            var tplFlags = npc.Configuration.TemplateFlags;
            string? skip = null;

            // With "Traits" inherited from a template, the NPC's own WNAM is ignored by the game.
            if (!npc.Template.IsNull && tplFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits))
                skip = "skin comes from its template";

            var race = npc.Race.TryResolve(cache);
            FormKey? skin = null;
            bool fromRace = false;
            if (!npc.WornArmor.IsNull)
                skin = npc.WornArmor.FormKey;
            else if (race != null && !race.Skin.IsNull)
            {
                skin = race.Skin.FormKey;
                fromRace = true;
            }
            if (skin == null && skip == null) skip = "no skin found";

            list.Add(new NpcEntry(
                npc.FormKey,
                npc.EditorID ?? "",
                npc.Name?.String ?? "",
                npc.Race.FormKey,
                race?.EditorID ?? "",
                flags.HasFlag(NpcConfiguration.Flag.Female),
                flags.HasFlag(NpcConfiguration.Flag.Unique),
                skin,
                fromRace,
                WinnerOf(cache, npc.FormKey),
                skip));
        }
        return list;
    }

    private static string WinnerOf(ILinkCache<ISkyrimMod, ISkyrimModGetter> cache, FormKey key) =>
        cache.TryResolveContext<INpc, INpcGetter>(key, out var ctx) ? ctx.ModKey.FileName.String : "";
}
