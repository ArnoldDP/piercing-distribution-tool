using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using PiercingDistributionTool.Core.Game;
using PiercingDistributionTool.Core.Rules;

namespace PiercingDistributionTool.Core.Patching;

/// <summary>
/// The engine puts one armor addon in each biped slot. A piercing that shares a slot with a part of the
/// NPC's skin (body, hands, a schlong, antlers, hair) or with another piece in its set knocks one of
/// them out, so those overlaps are worth a warning before writing.
/// </summary>
public static class SlotCheck
{
    public static BipedObjectFlag SlotsOf(IArmorAddonGetter addon) => addon.BodyTemplate?.FirstPersonFlags ?? 0;

    /// <summary>Slot numbers as the game and xEdit name them: bit 0 is slot 30.</summary>
    public static string Describe(BipedObjectFlag slots)
    {
        var names = new List<string>();
        for (var bit = 0; bit < 32; bit++)
            if (((uint)slots & (1u << bit)) != 0) names.Add((bit + 30).ToString());
        return names.Count == 0 ? "no slot" : "slot " + string.Join(", ", names);
    }

    public static List<string> Conflicts(ILinkCache<ISkyrimMod, ISkyrimModGetter> cache, NpcEntry npc, PiercingSet set)
    {
        var warnings = new List<string>();
        if (npc.SkinKey is not { } skinKey || !cache.TryResolve<IArmorGetter>(skinKey, out var skin)) return warnings;

        FormKey? armorRace = cache.TryResolve<IRaceGetter>(npc.RaceKey, out var race) && !race.ArmorRace.IsNull
            ? race.ArmorRace.FormKey : null;
        bool Fits(IArmorAddonGetter a) =>
            a.Race.FormKey == npc.RaceKey || a.AdditionalRaces.Any(r => r.FormKey == npc.RaceKey)
            || armorRace is { } ar && (a.Race.FormKey == ar || a.AdditionalRaces.Any(r => r.FormKey == ar));

        var skinParts = skin.Armature
            .Select(l => cache.TryResolve<IArmorAddonGetter>(l.FormKey, out var a) ? a : null)
            .Where(a => a != null && Fits(a))
            .Select(a => a!)
            .ToList();

        var pieces = set.ArmorAddons
            .Select(k => FormKey.TryFactory(k) is { } fk && cache.TryResolve<IArmorAddonGetter>(fk, out var a) ? a : null)
            .Where(a => a != null)
            .Select(a => a!)
            .ToList();

        foreach (var piece in pieces)
        {
            var slots = SlotsOf(piece);
            if (slots == 0)
            {
                warnings.Add($"{piece.EditorID} has no biped slot, so the game won't draw it.");
                continue;
            }
            foreach (var part in skinParts)
            {
                var shared = slots & SlotsOf(part);
                if (shared != 0)
                    warnings.Add($"{piece.EditorID} shares {Describe(shared)} with the skin's {part.EditorID}; one of them won't show.");
            }
        }

        for (var i = 0; i < pieces.Count; i++)
        for (var j = i + 1; j < pieces.Count; j++)
        {
            var shared = SlotsOf(pieces[i]) & SlotsOf(pieces[j]);
            if (shared != 0)
                warnings.Add($"{pieces[i].EditorID} and {pieces[j].EditorID} both use {Describe(shared)}; only one will show.");
        }
        return warnings;
    }
}
