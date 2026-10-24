using PiercingDistributionTool.Core.Game;

namespace PiercingDistributionTool.Core.Rules;

public sealed record Assignment(NpcEntry Npc, PiercingSet Set);

/// <summary>Turns the per-NPC choices into assignments. Nothing is handed out at random.</summary>
public static class Distributor
{
    public static List<Assignment> Assign(IEnumerable<NpcEntry> npcs, ToolSettings settings)
    {
        var result = new List<Assignment>();
        foreach (var npc in npcs)
        {
            if (npc.SkipReason != null) continue;
            if (!settings.Rules.Overrides.TryGetValue(npc.FormKey.ToString(), out var name)) continue;
            var set = settings.Sets.FirstOrDefault(s => s.Name == name);
            if (set != null && set.ArmorAddons.Count > 0) result.Add(new Assignment(npc, set));
        }
        return result;
    }
}
