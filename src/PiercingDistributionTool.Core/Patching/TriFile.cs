using System.Text;

namespace PiercingDistributionTool.Core.Patching;

/// <summary>
/// BodySlide's morph file (.tri, magic "PIRT"), which RaceMenu and OBody read through a mesh's BODYTRI
/// link to reshape it. Per shape, by name: a list of morphs, each a multiplier and sparse vertex
/// offsets. An optional second section holds UV morphs in the same layout with two components.
/// Entries are kept as raw bytes; merging only moves whole shapes between files.
/// </summary>
public sealed class TriFile
{
    public sealed record Morph(string Name, float Multiplier, ushort Count, byte[] Entries);
    public sealed record Shape(string Name, List<Morph> Morphs);

    public List<Shape> Shapes { get; } = new();
    public List<Shape> UvShapes { get; } = new();

    private const int PositionEntry = 8; // ushort index, short x, y, z
    private const int UvEntry = 6;       // ushort index, short u, v

    public static TriFile? Read(byte[] bytes)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes));
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "PIRT") return null;
            var tri = new TriFile();
            ReadSection(r, tri.Shapes, PositionEntry);
            if (r.BaseStream.Position < r.BaseStream.Length) ReadSection(r, tri.UvShapes, UvEntry);
            return tri;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static void ReadSection(BinaryReader r, List<Shape> into, int entrySize)
    {
        var shapes = r.ReadUInt16();
        for (var s = 0; s < shapes; s++)
        {
            var shape = new Shape(ReadName(r), new List<Morph>());
            var morphs = r.ReadUInt16();
            for (var m = 0; m < morphs; m++)
            {
                var name = ReadName(r);
                var mult = r.ReadSingle();
                var count = r.ReadUInt16();
                shape.Morphs.Add(new Morph(name, mult, count, r.ReadBytes(count * entrySize)));
            }
            into.Add(shape);
        }
    }

    private static string ReadName(BinaryReader r) => Encoding.ASCII.GetString(r.ReadBytes(r.ReadByte()));

    public void Write(string path)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Encoding.ASCII.GetBytes("PIRT"));
        WriteSection(w, Shapes);
        if (UvShapes.Count > 0) WriteSection(w, UvShapes);
    }

    private static void WriteSection(BinaryWriter w, List<Shape> shapes)
    {
        w.Write((ushort)shapes.Count);
        foreach (var shape in shapes)
        {
            WriteName(w, shape.Name);
            w.Write((ushort)shape.Morphs.Count);
            foreach (var m in shape.Morphs)
            {
                WriteName(w, m.Name);
                w.Write(m.Multiplier);
                w.Write(m.Count);
                w.Write(m.Entries);
            }
        }
    }

    private static void WriteName(BinaryWriter w, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        w.Write((byte)bytes.Length);
        w.Write(bytes);
    }

    /// <summary>Adds another file's shapes, renamed through <paramref name="rename"/> to match the merged mesh.</summary>
    public void Absorb(TriFile other, Func<string, string> rename)
    {
        foreach (var s in other.Shapes) Shapes.Add(s with { Name = rename(s.Name) });
        foreach (var s in other.UvShapes) UvShapes.Add(s with { Name = rename(s.Name) });
    }
}
