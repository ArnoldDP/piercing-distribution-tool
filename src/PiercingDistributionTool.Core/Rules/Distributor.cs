using System.Security.Cryptography;
using System.Text;
using PiercingDistributionTool.Core.Game;

namespace PiercingDistributionTool.Core.Rules;

public sealed record Assignment(NpcEntry Npc, PiercingSet Set);

/// <summary>Decides which NPC gets which set. Deterministic: same NPCs, rules and seed give the same answer.</summary>
public static class Distributor
{
    public static List<Assignment> Assign(IEnumerable<NpcEntry> npcs, ToolSettings settings)
    {
        var rules = settings.Rules;
        var result = new List<Assignment>();
        foreach (var npc in npcs)
        {
            if (npc.SkipReason != null) continue;

            if (rules.Overrides.TryGetValue(npc.FormKey.ToString(), out var forced))
            {
                var set = settings.Sets.FirstOrDefault(s => s.Name == forced);
                if (set != null) result.Add(new Assignment(npc, set));
                continue;
            }

            if (npc.IsUnique ? !rules.IncludeUnique : !rules.IncludeGeneric) continue;

            var candidates = settings.Sets.Where(s => Fits(s, npc) && s.ArmorAddons.Count > 0 && s.Weight > 0).ToList();
            if (candidates.Count == 0) continue;

            if (Roll(npc, rules.Seed, "chance") * 100 >= rules.ChancePercent) continue;

            var total = candidates.Sum(s => s.Weight);
            var pick = Roll(npc, rules.Seed, "set") * total;
            foreach (var s in candidates)
            {
                pick -= s.Weight;
                if (pick < 0) { result.Add(new Assignment(npc, s)); break; }
            }
        }
        return result;
    }

    public static bool Fits(PiercingSet set, NpcEntry npc)
    {
        if (set.Gender == GenderFilter.Female && !npc.IsFemale) return false;
        if (set.Gender == GenderFilter.Male && npc.IsFemale) return false;
        if (set.Races.Count > 0 && !set.Races.Contains(npc.RaceEditorId, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>A stable 0..1 value from the NPC's FormKey, so results never depend on load timing.</summary>
    private static double Roll(NpcEntry npc, int seed, string salt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{npc.FormKey}|{seed}|{salt}"));
        return BitConverter.ToUInt32(bytes, 0) / (double)uint.MaxValue;
    }
}
