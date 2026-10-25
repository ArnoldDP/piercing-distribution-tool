using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using PiercingDistributionTool.Core.Game;
using PiercingDistributionTool.Core.Rules;

namespace PiercingDistributionTool.Core.Patching;

public sealed record PatchResult(string Path, int NpcCount, int SkinCount, int AddonOverrides, bool Light);

/// <summary>
/// Writes the output plugin: one skin per (base skin, set) pair, each a copy of the NPC's current skin
/// plus the set's addons, and an NPC override pointing WNAM at it.
/// </summary>
public static class PatchWriter
{
    // A light plugin may hold at most 2048 new records (0x800-0xFFF).
    private const int LightLimit = 2048;

    public static PatchResult Write(GameSession session, IReadOnlyList<Assignment> assignments, ToolSettings settings)
    {
        var cache = session.LinkCache;
        var modKey = ModKey.FromFileName(settings.OutputPluginName);
        var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);

        var skins = new Dictionary<(FormKey Base, string Set), Armor>();
        var addonOverrides = new Dictionary<FormKey, ArmorAddon>();

        foreach (var (npc, set) in assignments)
        {
            var baseKey = npc.SkinKey!.Value;
            if (!skins.TryGetValue((baseKey, set.Name), out var skin))
            {
                if (!cache.TryResolve<IArmorGetter>(baseKey, out var baseSkin)) continue;
                skin = mod.Armors.AddNew(EditorIdFor(baseSkin, set));
                skin.DeepCopyIn(baseSkin, new Armor.TranslationMask(defaultOn: true) { EditorID = false });
                foreach (var addon in set.ArmorAddons)
                {
                    var fk = FormKey.Factory(addon);
                    if (skin.Armature.All(a => a.FormKey != fk))
                        skin.Armature.Add(fk);
                    // The engine only draws an addon in slots its armor claims, so the skin has to
                    // claim the piercing's slots too.
                    if (cache.TryResolve<IArmorAddonGetter>(fk, out var piece) && SlotCheck.SlotsOf(piece) is var slots and not 0)
                    {
                        skin.BodyTemplate ??= new BodyTemplate();
                        skin.BodyTemplate.FirstPersonFlags |= slots;
                    }
                }
                skins[(baseKey, set.Name)] = skin;
            }

            // Each piercing addon must list the NPC's race, or the engine skips it at render time.
            foreach (var addon in set.ArmorAddons)
                EnsureRace(cache, mod, addonOverrides, FormKey.Factory(addon), npc.RaceKey);

            if (!cache.TryResolve<INpcGetter>(npc.FormKey, out var npcRecord)) continue;
            var over = mod.Npcs.GetOrAddAsOverride(npcRecord);
            over.WornArmor.SetTo(skin);
        }

        var newRecords = skins.Count;
        var light = newRecords <= LightLimit;
        if (light) mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;

        var folder = settings.OutputFolder ?? session.DataFolder;
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, modKey.FileName);
        mod.WriteToBinary(path);
        return new PatchResult(path, assignments.Count, skins.Count, addonOverrides.Count, light);
    }

    private static void EnsureRace(
        Mutagen.Bethesda.Plugins.Cache.ILinkCache<ISkyrimMod, ISkyrimModGetter> cache,
        SkyrimMod mod, Dictionary<FormKey, ArmorAddon> overrides, FormKey addonKey, FormKey raceKey)
    {
        IArmorAddonGetter? addon = overrides.TryGetValue(addonKey, out var existing) ? existing
            : cache.TryResolve<IArmorAddonGetter>(addonKey, out var resolved) ? resolved : null;
        if (addon == null) return;
        if (Fits(addon, raceKey)) return;
        // Races wear armour as their armour race (Nords as the Default race), so that counts too.
        if (cache.TryResolve<IRaceGetter>(raceKey, out var race) && !race.ArmorRace.IsNull && Fits(addon, race.ArmorRace.FormKey)) return;

        if (existing == null)
        {
            existing = mod.ArmorAddons.GetOrAddAsOverride(addon);
            overrides[addonKey] = existing;
        }
        existing.AdditionalRaces.Add(raceKey);
    }

    private static bool Fits(IArmorAddonGetter addon, FormKey raceKey) =>
        addon.Race.FormKey == raceKey || addon.AdditionalRaces.Any(r => r.FormKey == raceKey);

    private static string EditorIdFor(IArmorGetter baseSkin, PiercingSet set)
    {
        var clean = new string(set.Name.Where(char.IsLetterOrDigit).ToArray());
        return $"PDT_{baseSkin.EditorID ?? baseSkin.FormKey.IDString()}_{clean}";
    }
}
