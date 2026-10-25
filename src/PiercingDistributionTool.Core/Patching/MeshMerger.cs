using System.Reflection;
using nifly;
using PiercingDistributionTool.Core.Assets;

namespace PiercingDistributionTool.Core.Patching;

/// <summary>
/// Builds a body mesh with piercings baked in: the body NIF plus every shape from the piercing NIFs,
/// and one morph file holding the body's and the piercings' morphs so OBody/RaceMenu reshape the
/// piercings with the body. Both halves of a weighted pair (_0/_1) are merged the same way.
/// </summary>
public sealed class MeshMerger
{
    private readonly AssetResolver _assets;
    private readonly string _outputRoot;   // folder that receives meshes\..., i.e. Data or the output folder
    private readonly string _tempFolder;
    private readonly Dictionary<string, string?> _done = new(StringComparer.OrdinalIgnoreCase);

    public int MeshesWritten { get; private set; }
    public List<string> Problems { get; } = new();

    public MeshMerger(AssetResolver assets, string outputRoot)
    {
        _assets = assets;
        _outputRoot = outputRoot;
        _tempFolder = Path.Combine(Path.GetTempPath(), "PiercingDistributionTool");
        Directory.CreateDirectory(_tempFolder);
    }

    /// <summary>
    /// Merges <paramref name="body"/> (a mesh path as an addon names it, under meshes\) with the
    /// piercing meshes and returns the merged mesh's path in the same form, or null on failure.
    /// <paramref name="outBase"/> is the merged file's path under meshes\ without the _0/_1 suffix.
    /// </summary>
    public string? Merge(string body, IReadOnlyList<string> piercings, string outBase)
    {
        var key = body + "|" + string.Join("|", piercings);
        if (_done.TryGetValue(key, out var cached)) return cached;

        string? result = null;
        try
        {
            var bodyPair = WeightPair(body);
            var triRel = outBase + ".tri";
            TriFile? mergedTri = null;

            foreach (var weight in bodyPair is null ? new[] { "" } : new[] { "_0", "_1" })
            {
                var bodyPath = bodyPair is { } bp ? bp + weight + ".nif" : body;
                using var nif = Load(bodyPath);
                if (nif == null)
                {
                    if (weight == "_0") continue; // some bodies ship only the _1 half
                    Problems.Add($"Couldn't read body mesh {bodyPath}.");
                    return Remember(key, null);
                }

                var tri = weight is "" or "_1" ? ReadTri(nif) : null;
                var names = new HashSet<string>(nif.GetShapes().Select(s => s.name.get()), StringComparer.OrdinalIgnoreCase);

                foreach (var piercing in piercings)
                {
                    var pPair = WeightPair(piercing);
                    var pPath = pPair is { } pp ? pp + (weight == "" ? "_1" : weight) + ".nif" : piercing;
                    using var src = Load(pPath) ?? (pPair is { } pp2 ? Load(pp2 + "_1.nif") : null);
                    if (src == null)
                    {
                        Problems.Add($"Couldn't read piercing mesh {pPath}.");
                        continue;
                    }
                    var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var shape in src.GetShapes())
                    {
                        var name = shape.name.get();
                        var unique = name;
                        for (var i = 2; names.Contains(unique); i++) unique = $"{name}_{i}";
                        names.Add(unique);
                        renames[name] = unique;
                        nif.CloneShape(shape, unique, src);
                    }
                    if (weight is "" or "_1" && ReadTri(src) is { } pTri)
                    {
                        tri ??= new TriFile();
                        tri.Absorb(pTri, n => renames.TryGetValue(n, out var r) ? r : n);
                    }
                }

                if (tri != null) mergedTri = tri;
                PointBodyTri(nif, mergedTri != null || HasBodyTri(nif) ? triRel : null);

                var outRel = outBase + weight + ".nif";
                var outAbs = Path.Combine(_outputRoot, "meshes", outRel);
                Directory.CreateDirectory(Path.GetDirectoryName(outAbs)!);
                if (nif.Save(outAbs) != 0)
                {
                    Problems.Add($"Couldn't save {outAbs}.");
                    return Remember(key, null);
                }
                MeshesWritten++;
                result = outRel;
            }

            if (mergedTri != null)
                mergedTri.Write(Path.Combine(_outputRoot, "meshes", triRel));

            // Addons name a mesh by one half of the pair; the engine finds the other by suffix.
            if (bodyPair != null) result = outBase + "_1.nif";
        }
        catch (Exception ex)
        {
            Problems.Add($"Merging {body} failed: {ex.Message}");
            result = null;
        }
        return Remember(key, result);
    }

    private string? Remember(string key, string? value) => _done[key] = value;

    /// <summary>"x\body_1.nif" or "x\body_0.nif" -> "x\body"; anything else isn't a weighted pair.</summary>
    private static string? WeightPair(string path) =>
        path.EndsWith("_1.nif", StringComparison.OrdinalIgnoreCase) || path.EndsWith("_0.nif", StringComparison.OrdinalIgnoreCase)
            ? path[..^6] : null;

    private NifFile? Load(string meshPath)
    {
        var rel = meshPath.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? meshPath : Path.Combine("meshes", meshPath);
        var bytes = _assets.Read(rel);
        if (bytes == null) return null;
        var tmp = Path.Combine(_tempFolder, Guid.NewGuid().ToString("N") + ".nif");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            var nif = new NifFile();
            if (nif.Load(tmp) == 0) return nif;
            nif.Dispose();
            return null;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static IEnumerable<(uint Id, NiStringExtraData Data)> BodyTriBlocks(NifFile nif)
    {
        var header = nif.GetHeader();
        for (uint i = 0; i < header.GetNumBlocks(); i++)
        {
            if (header.GetBlockTypeStringById(i) != "NiStringExtraData") continue;
            var data = Cast<NiStringExtraData>(header.GetBlockById(i));
            if (data.name.get() == "BODYTRI") yield return (i, data);
        }
    }

    private static bool HasBodyTri(NifFile nif) => BodyTriBlocks(nif).Any();

    private TriFile? ReadTri(NifFile nif)
    {
        var path = BodyTriBlocks(nif).Select(b => b.Data.stringData.get()).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path)) return null;
        var bytes = _assets.Read(Path.Combine("meshes", path));
        return bytes == null ? null : TriFile.Read(bytes);
    }

    /// <summary>
    /// Leaves exactly one BODYTRI link, on the root, pointing at the merged morph file. Cloned piercing
    /// shapes bring their own links along, which would otherwise compete with the body's.
    /// </summary>
    private static void PointBodyTri(NifFile nif, string? triRel)
    {
        // nifly can't rewrite a string in place, so every existing link goes and a fresh one is added.
        var header = nif.GetHeader();
        foreach (var b in BodyTriBlocks(nif).ToList().OrderByDescending(b => b.Id)) header.DeleteBlock(b.Id);
        if (triRel != null)
            nif.AddStringExtraDataToNode((int)nif.GetBlockID(nif.GetRootNode()), "BODYTRI", triRel);
    }

    /// <summary>nifly hands blocks back as their base type; this views the same native object as <typeparamref name="T"/>.</summary>
    internal static T Cast<T>(object block)
    {
        const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = block.GetType();
        var handle = (System.Runtime.InteropServices.HandleRef)type.GetMethod("getCPtr", any, new[] { type })!.Invoke(null, new[] { block })!;
        return (T)typeof(T).GetConstructor(any, new[] { typeof(IntPtr), typeof(bool) })!.Invoke(new object[] { handle.Handle, false });
    }
}
