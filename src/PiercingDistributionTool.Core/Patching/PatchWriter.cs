using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using PiercingDistributionTool.Core.Game;
using Mutagen.Bethesda.Plugins.Cache;
using PiercingDistributionTool.Core.Rules;

namespace PiercingDistributionTool.Core.Patching;

public sealed record PatchResult(string Path, int NpcCount, int SkinCount, int AddonOverrides, bool Light,
    int MergedMeshes, IReadOnlyList<string> Problems);

/// <summary>
/// Writes the output plugin: one skin per (base skin, set) pair, each a copy of the NPC's current skin,
/// and an NPC override pointing WNAM at it. With merging on, the skin's body addon is swapped for a copy
/// whose mesh has the piercings baked in, so body armor hides them with the body. Otherwise the
/// piercing addons are added to the skin alongside the body, in their own slots.
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
        var folder = settings.OutputFolder ?? session.DataFolder;
        Directory.CreateDirectory(folder);

        var merger = settings.MergeIntoBody ? new MeshMerger(new Assets.AssetResolver(session.DataFolder), folder) : null;
        var problems = new List<string>();
        var addonOverrides = new Dictionary<FormKey, ArmorAddon>();
        var skins = new Dictionary<(FormKey Base, string Set), Armor>();

        foreach (var group in assignments.GroupBy(a => (Base: a.Npc.SkinKey!.Value, a.Set)))
        {
            var (baseKey, set) = group.Key;
            if (!cache.TryResolve<IArmorGetter>(baseKey, out var baseSkin)) continue;
            var skin = mod.Armors.AddNew(EditorIdFor(baseSkin, set));
            skin.DeepCopyIn(baseSkin, new Armor.TranslationMask(defaultOn: true) { EditorID = false });
            skins[(baseKey, set.Name)] = skin;

            var merged = merger != null && MergeIntoBody(cache, mod, merger, skin, set, group.Select(a => a.Npc).ToList(), problems);
            if (!merged)
            {
                AddAsSeparateAddons(cache, skin, set);
                // Each piercing addon must list the NPC's race, or the engine skips it at render time.
                foreach (var npc in group.Select(a => a.Npc))
                foreach (var addon in set.ArmorAddons)
                    EnsureRace(cache, mod, addonOverrides, FormKey.Factory(addon), npc.RaceKey);
            }

            foreach (var npc in group.Select(a => a.Npc))
            {
                if (!cache.TryResolve<INpcGetter>(npc.FormKey, out var npcRecord)) continue;
                mod.Npcs.GetOrAddAsOverride(npcRecord).WornArmor.SetTo(skin);
            }
        }

        var newRecords = mod.Armors.Count + mod.ArmorAddons.Count(a => a.FormKey.ModKey == modKey);
        var light = newRecords <= LightLimit;
        if (light) mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;

        var path = System.IO.Path.Combine(folder, modKey.FileName);
        mod.WriteToBinary(path);
        if (merger != null) problems.AddRange(merger.Problems);
        return new PatchResult(path, assignments.Count, skins.Count, addonOverrides.Count, light,
            merger?.MeshesWritten ?? 0, problems);
    }

    private static void AddAsSeparateAddons(ILinkCache<ISkyrimMod, ISkyrimModGetter> cache, Armor skin, PiercingSet set)
    {
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
    }

    /// <summary>
    /// Replaces each body addon the NPCs would wear with a copy pointing at a merged mesh. Returns
    /// false (and the caller falls back to separate addons) when there is no body addon to merge into.
    /// </summary>
    private static bool MergeIntoBody(ILinkCache<ISkyrimMod, ISkyrimModGetter> cache, SkyrimMod mod, MeshMerger merger,
        Armor skin, PiercingSet set, List<NpcEntry> npcs, List<string> problems)
    {
        var races = new HashSet<FormKey>();
        foreach (var npc in npcs)
        {
            races.Add(npc.RaceKey);
            if (cache.TryResolve<IRaceGetter>(npc.RaceKey, out var race) && !race.ArmorRace.IsNull) races.Add(race.ArmorRace.FormKey);
        }
        bool Fits(IArmorAddonGetter a) => races.Contains(a.Race.FormKey) || a.AdditionalRaces.Any(r => races.Contains(r.FormKey));

        var bodies = skin.Armature
            .Select(l => cache.TryResolve<IArmorAddonGetter>(l.FormKey, out var a) ? a : null)
            .Where(a => a != null && SlotCheck.SlotsOf(a).HasFlag(BipedObjectFlag.Body) && Fits(a))
            .Select(a => a!)
            .ToList();
        if (bodies.Count == 0)
        {
            problems.Add($"{skin.EditorID}: no body addon for these NPCs' race, so the piercings were added as separate addons.");
            return false;
        }

        var pieces = set.ArmorAddons
            .Select(k => FormKey.TryFactory(k) is { } fk && cache.TryResolve<IArmorAddonGetter>(fk, out var a) ? a : null)
            .Where(a => a != null)
            .Select(a => a!)
            .ToList();

        var made = new List<(FormKey Original, ArmorAddon Copy)>();
        foreach (var body in bodies)
        {
            var bodyName = Clean(body.EditorID ?? body.FormKey.IDString());
            var copy = mod.ArmorAddons.AddNew($"{skin.EditorID}_{bodyName}");
            copy.DeepCopyIn(body, new ArmorAddon.TranslationMask(defaultOn: true) { EditorID = false });
            var changed = false;
            foreach (var female in npcs.Select(n => n.IsFemale).Distinct())
            {
                var model = female ? copy.WorldModel?.Female : copy.WorldModel?.Male;
                var bodyMesh = model?.File?.DataRelativePath.Path;
                if (model == null || string.IsNullOrEmpty(bodyMesh)) continue;
                var pierceMeshes = pieces
                    .Select(p => (female ? p.WorldModel?.Female : p.WorldModel?.Male)?.File?.DataRelativePath.Path)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Select(p => p!)
                    .ToList();
                if (pierceMeshes.Count == 0) continue;

                var sex = female ? "f" : "m";
                var outBase = Path.Combine("PiercingDistribution", Clean(skin.EditorID!), bodyName + "_" + sex);
                var mergedMesh = merger.Merge(bodyMesh, pierceMeshes, outBase);
                if (mergedMesh == null)
                {
                    // Undo this skin's swaps; the caller adds the piercings as separate addons instead.
                    mod.ArmorAddons.Remove(copy);
                    foreach (var (original, done) in made)
                    {
                        mod.ArmorAddons.Remove(done);
                        var at = skin.Armature.FindIndex(l => l.FormKey == done.FormKey);
                        if (at >= 0) skin.Armature[at] = new FormLink<IArmorAddonGetter>(original);
                    }
                    problems.Add($"{skin.EditorID}: merging failed, so its piercings were added as separate addons.");
                    return false;
                }
                model.File = new Mutagen.Bethesda.Plugins.Assets.AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimModelAssetType>(mergedMesh);
                model.Data = null; // texture hashes described the old mesh
                changed = true;
            }
            if (!changed)
            {
                mod.ArmorAddons.Remove(copy);
                continue;
            }
            var index = skin.Armature.FindIndex(l => l.FormKey == body.FormKey);
            skin.Armature[index] = copy.ToLink();
            made.Add((body.FormKey, copy));
        }
        return true;
    }

    private static string Clean(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

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
