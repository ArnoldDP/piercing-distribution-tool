using System.Reflection;
using nifly;
using PiercingDistributionTool.Core.Assets;

namespace PiercingDistributionTool.Core.Patching;

/// <summary>
/// Builds a body mesh with piercings baked in: the body NIF plus every shape from the piercing NIFs,
/// and one morph file holding the body's and the piercings' morphs so OBody/RaceMenu reshape the
/// piercings with the body. Both halves of a weighted pair (_0/_1) are merged the same way.
/// </summary>
/// <summary>
/// One piercing mesh to merge, with the texture sets its armor addon swaps onto named shapes
/// (shape name -> texture paths by BSShaderTextureSet slot, empty where the set leaves a slot alone).
/// </summary>
public sealed record PiercingMesh(string Path, IReadOnlyDictionary<string, string[]> AlternateTextures);

public sealed class MeshMerger
{
    private readonly AssetResolver _assets;
    private readonly string _outputRoot;   // folder that receives meshes\..., i.e. Data or the output folder
    private readonly string _tempFolder;
    private readonly Dictionary<string, string?> _done = new(StringComparer.OrdinalIgnoreCase);

    public int MeshesWritten { get; private set; }
    public List<string> Problems { get; } = new();

    private readonly bool _stripReflections;
    private readonly bool _piercingMorphs;

    public MeshMerger(AssetResolver assets, string outputRoot, bool stripReflections, bool piercingMorphs = true)
    {
        _assets = assets;
        _outputRoot = outputRoot;
        _stripReflections = stripReflections;
        _piercingMorphs = piercingMorphs;
        _tempFolder = Path.Combine(Path.GetTempPath(), "PiercingDistributionTool");
        Directory.CreateDirectory(_tempFolder);
    }

    /// <summary>
    /// Merges <paramref name="body"/> (a mesh path as an addon names it, under meshes\) with the
    /// piercing meshes and returns the merged mesh's path in the same form, or null on failure.
    /// <paramref name="outBase"/> is the merged file's path under meshes\ without the _0/_1 suffix.
    /// </summary>
    public string? Merge(string body, IReadOnlyList<PiercingMesh> piercings, string outBase)
    {
        var key = body + "|" + string.Join("|", piercings.Select(p =>
            p.Path + ":" + string.Join(";", p.AlternateTextures.Select(kv => kv.Key + "=" + string.Join(",", kv.Value)))));
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
                // SMP reads one physics config per mesh, matched to shapes by name.
                var physics = ExtraStrings(nif, PhysicsLink).FirstOrDefault();
                var bodyHasPhysics = physics != null;

                foreach (var piece in piercings)
                {
                    var piercing = piece.Path;
                    var pPair = WeightPair(piercing);
                    var pPath = pPair is { } pp ? pp + (weight == "" ? "_1" : weight) + ".nif" : piercing;
                    using var src = Load(pPath) ?? (pPair is { } pp2 ? Load(pp2 + "_1.nif") : null);
                    if (src == null)
                    {
                        Problems.Add($"Couldn't read piercing mesh {pPath}.");
                        return Remember(key, null);
                    }
                    var piercingPhysics = ExtraStrings(src, PhysicsLink).FirstOrDefault();
                    if (piercingPhysics != null)
                    {
                        if (physics != null)
                        {
                            Problems.Add(bodyHasPhysics
                                ? $"{body} has its own SMP physics, so the SMP piercing {piercing} can't be merged into it."
                                : $"Two SMP piercings ({piercing} and another) can't share one merged mesh.");
                            return Remember(key, null);
                        }
                        physics = piercingPhysics;
                    }
                    var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var shape in src.GetShapes())
                    {
                        var name = shape.name.get();
                        var unique = name;
                        for (var i = 2; names.Contains(unique); i++) unique = $"{name}_{i}";
                        names.Add(unique);
                        renames[name] = unique;
                        if (piercingPhysics != null && unique != name)
                        {
                            Problems.Add($"SMP piercing {piercing} has a shape named like one already in the body ({name}); its physics config would no longer match.");
                            return Remember(key, null);
                        }
                        var clone = nif.CloneShape(shape, unique, src);
                        if (clone == null) continue;
                        TagAsBody(nif, clone);
                        if (piece.AlternateTextures.TryGetValue(name, out var textures)) ApplyTextures(nif, clone, textures);
                        if (_stripReflections) StripReflections(nif, clone);
                    }
                    if (_piercingMorphs && weight is "" or "_1" && ReadTri(src) is { } pTri)
                    {
                        tri ??= new TriFile();
                        tri.Absorb(pTri, n => renames.TryGetValue(n, out var r) ? r : n);
                    }
                }

                if (tri != null) mergedTri = tri;
                PointBodyTri(nif, mergedTri != null || HasBodyTri(nif) ? triRel : null);
                if (physics != null && !bodyHasPhysics)
                    nif.AddStringExtraDataToNode((int)nif.GetBlockID(nif.GetRootNode()), PhysicsLink, physics);

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

    /// <summary>
    /// An addon's alternate texture set replaces a shape's textures at runtime; merged copies don't go
    /// through that addon, so the swap is written into the mesh instead.
    /// </summary>
    private static void ApplyTextures(NifFile nif, NiShape shape, string[] textures)
    {
        for (uint slot = 0; slot < textures.Length; slot++)
            if (!string.IsNullOrWhiteSpace(textures[slot])) nif.SetTextureSlot(shape, textures[slot], slot);
    }

    private const uint EnvironmentMapping = 0x80; // SLSF1_Environment_Mapping

    /// <summary>
    /// Environment-mapped (cube-map reflective) piercings crash the game once they're part of a skin
    /// addon, presumably in the engine's skin handling. Merged copies drop the reflection and render as
    /// plain lit metal; the piece worn as normal armor keeps it.
    /// </summary>
    private static void StripReflections(NifFile nif, NiShape shape)
    {
        var shader = nif.GetShader(shape);
        if (shader == null || shader.GetBlockName() != "BSLightingShaderProperty") return;
        var lighting = Cast<BSLightingShaderProperty>(shader);
        if ((lighting.shaderFlags1 & EnvironmentMapping) == 0) return;
        lighting.shaderFlags1 &= ~EnvironmentMapping;
        if (shader.GetShaderType() == 1) shader.SetShaderType(0); // environment map -> default
        nif.SetTextureSlot(shape, "", 4);
        nif.SetTextureSlot(shape, "", 5);
    }

    private const ushort BodySlot = 32; // SBP_32_BODY

    /// <summary>
    /// A skinned shape's partitions carry the body slot they belong to, and the game only draws the
    /// partitions whose slot the addon fills. Piercing shapes come tagged with their own slot (56, 36…),
    /// which a slot-32 body addon never fills, so they'd be invisible until retagged as body.
    /// </summary>
    private static void TagAsBody(NifFile nif, NiShape shape)
    {
        var parts = new NiVectorBSDismemberSkinInstancePartitionInfo();
        var triParts = new vectorint();
        if (!nif.GetShapePartitions(shape, parts, triParts)) return;
        var retagged = new vectorPartitionInfo();
        var changed = false;
        foreach (var part in parts.items())
        {
            changed |= part.partID != BodySlot;
            retagged.Add(new BSDismemberSkinInstance.PartitionInfo { flags = part.flags, partID = BodySlot });
        }
        if (!changed) return;
        parts.SetItems(retagged);
        nif.SetShapePartitions(shape, parts, triParts);
    }

    private const string PhysicsLink = "HDT Skinned Mesh Physics Object";

    private static IEnumerable<(uint Id, NiStringExtraData Data)> ExtraBlocks(NifFile nif, string name)
    {
        var header = nif.GetHeader();
        for (uint i = 0; i < header.GetNumBlocks(); i++)
        {
            if (header.GetBlockTypeStringById(i) != "NiStringExtraData") continue;
            var data = Cast<NiStringExtraData>(header.GetBlockById(i));
            if (data.name.get() == name) yield return (i, data);
        }
    }

    private static IEnumerable<string> ExtraStrings(NifFile nif, string name) =>
        ExtraBlocks(nif, name).Select(b => b.Data.stringData.get()).Where(v => !string.IsNullOrWhiteSpace(v));

    private static IEnumerable<(uint Id, NiStringExtraData Data)> BodyTriBlocks(NifFile nif) => ExtraBlocks(nif, "BODYTRI");

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
