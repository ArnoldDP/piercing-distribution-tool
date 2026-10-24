namespace PiercingDistributionTool.Core.Preview;

/// <summary>One drawable piece of an NPC: positions already in the shared skeleton space.</summary>
public sealed class MeshPart
{
    public required string Name { get; init; }
    public required float[] Positions { get; init; }   // xyz
    public required float[] Normals { get; init; }     // xyz
    public required float[] Uvs { get; init; }         // uv
    public required uint[] Indices { get; init; }
    public string? DiffusePath { get; init; }
    public bool IsPiercing { get; init; }
}

public sealed class NpcModel
{
    public List<MeshPart> Parts { get; } = new();
    public List<string> Missing { get; } = new();
}
