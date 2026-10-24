using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using nifly;
using PiercingDistributionTool.Core.Assets;
using PiercingDistributionTool.Core.Game;
using PiercingDistributionTool.Core.Rules;

namespace PiercingDistributionTool.Core.Preview;

/// <summary>
/// Gathers the meshes the game would draw for a naked NPC: every skin addon for its race and sex,
/// its baked FaceGen head, and optionally a piercing set.
/// </summary>
public sealed class NpcModelBuilder
{
    private readonly ILinkCache<ISkyrimMod, ISkyrimModGetter> _cache;
    private readonly AssetResolver _assets;
    private readonly string _tempFolder;
    private readonly Dictionary<string, NifFile?> _skeletons = new(StringComparer.OrdinalIgnoreCase);
    private NifFile? _skeleton;
    private readonly Dictionary<string, MatTransform> _boneGlobals = new();
    private float[]? _hairTint;
    private float _weight = 1f;
    private FormKey? _armorRace;

    public NpcModelBuilder(GameSession session, AssetResolver assets)
    {
        _cache = session.LinkCache;
        _assets = assets;
        _tempFolder = Path.Combine(Path.GetTempPath(), "PiercingDistributionTool");
        Directory.CreateDirectory(_tempFolder);
    }

    public NpcModel Build(NpcEntry npc, PiercingSet? piercings)
    {
        var model = new NpcModel();
        _skeleton = SkeletonFor(npc);
        _boneGlobals.Clear();
        _hairTint = HairTintFor(npc);
        _armorRace = _cache.TryResolve<IRaceGetter>(npc.RaceKey, out var race) && !race.ArmorRace.IsNull ? race.ArmorRace.FormKey : null;
        _weight = _cache.TryResolve<INpcGetter>(npc.FormKey, out var record) ? Math.Clamp(record.Weight / 100f, 0f, 1f) : 1f;

        if (npc.SkinKey is { } skinKey && _cache.TryResolve<IArmorGetter>(skinKey, out var skin))
        {
            foreach (var link in skin.Armature)
                AddAddon(model, link.FormKey, npc, isPiercing: false);
        }
        else model.Missing.Add("skin record");

        AddMesh(model, FaceGenPath(npc.FormKey), "head", isPiercing: false);

        if (piercings != null)
            foreach (var addon in piercings.ArmorAddons)
                AddAddon(model, FormKey.Factory(addon), npc, isPiercing: true, ignoreRace: true);

        WeldSeams(model);
        return model;
    }

    /// <summary>
    /// Head and body are separate meshes whose edges share positions. Computed normals at those
    /// edges only see one side, which draws a line at the neck and wrists; averaging the normals of
    /// coincident vertices across parts removes it, as the game's matched normals do.
    /// </summary>
    private static void WeldSeams(NpcModel model)
    {
        var parts = model.Parts.Where(p => p.NormalsComputed).ToList();
        var sums = new Dictionary<(int, int, int), (float X, float Y, float Z)>();
        static (int, int, int) Key(float[] p, int i) =>
            ((int)MathF.Round(p[i] * 50), (int)MathF.Round(p[i + 1] * 50), (int)MathF.Round(p[i + 2] * 50));
        foreach (var part in parts)
            for (var i = 0; i + 2 < part.Positions.Length; i += 3)
            {
                var k = Key(part.Positions, i);
                sums.TryGetValue(k, out var acc);
                sums[k] = (acc.X + part.Normals[i], acc.Y + part.Normals[i + 1], acc.Z + part.Normals[i + 2]);
            }
        foreach (var part in parts)
            for (var i = 0; i + 2 < part.Positions.Length; i += 3)
            {
                var n = sums[Key(part.Positions, i)];
                var len = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
                if (len <= 0) continue;
                part.Normals[i] = n.X / len; part.Normals[i + 1] = n.Y / len; part.Normals[i + 2] = n.Z / len;
            }
    }

    private void AddAddon(NpcModel model, FormKey key, NpcEntry npc, bool isPiercing, bool ignoreRace = false)
    {
        if (!_cache.TryResolve<IArmorAddonGetter>(key, out var addon))
        {
            model.Missing.Add($"armor addon {key}");
            return;
        }
        if (!ignoreRace && !Fits(addon, npc.RaceKey) && (_armorRace is not { } armorRace || !Fits(addon, armorRace)))
            return;

        var gendered = addon.WorldModel;
        var file = (npc.IsFemale ? gendered?.Female?.File : gendered?.Male?.File)?.DataRelativePath.Path
                   ?? gendered?.Male?.File?.DataRelativePath.Path;
        if (string.IsNullOrEmpty(file)) return;

        var path = file.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? file : Path.Combine("meshes", file);
        // An addon's skin texture set replaces the mesh's own textures on its skin shapes, which is
        // how racial and custom skins retexture a shared body mesh.
        string? skinDiffuse = null;
        var skinSet = npc.IsFemale ? addon.SkinTexture?.Female : addon.SkinTexture?.Male;
        if (skinSet != null && !skinSet.IsNull && _cache.TryResolve<ITextureSetGetter>(skinSet.FormKey, out var textureSet))
            skinDiffuse = TexturePath(textureSet.Diffuse?.DataRelativePath.Path);

        var before = model.Missing.Count;
        AddMesh(model, path, addon.EditorID ?? key.ToString(), isPiercing, skinDiffuse);
        if (isPiercing && model.Missing.Count > before)
        {
            model.MissingPiercings.AddRange(model.Missing.Skip(before));
            model.Missing.RemoveRange(before, model.Missing.Count - before);
        }
    }

    /// <summary>
    /// The race's skeleton holds each bone's bind position. Many meshes (FaceGen heads especially)
    /// carry bone nodes with no transform, so the skeleton is the dependable source.
    /// </summary>
    private NifFile? SkeletonFor(NpcEntry npc)
    {
        string? file = null;
        if (_cache.TryResolve<IRaceGetter>(npc.RaceKey, out var race))
        {
            var models = race.SkeletalModel;
            file = (npc.IsFemale ? models?.Female?.File : models?.Male?.File)?.DataRelativePath.Path
                   ?? models?.Male?.File?.DataRelativePath.Path;
        }
        file ??= npc.IsFemale ? @"actors\character\character assets female\skeleton_female.nif"
                              : @"actors\character\character assets\skeleton.nif";
        var path = file.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? file : Path.Combine("meshes", file);
        if (_skeletons.TryGetValue(path, out var hit)) return hit;

        NifFile? skeleton = null;
        var bytes = _assets.Read(path);
        if (bytes != null)
        {
            var tmp = Path.Combine(_tempFolder, Guid.NewGuid().ToString("N") + ".nif");
            File.WriteAllBytes(tmp, bytes);
            skeleton = new NifFile();
            if (skeleton.Load(tmp) != 0) { skeleton.Dispose(); skeleton = null; }
            try { File.Delete(tmp); } catch { }
        }
        _skeletons[path] = skeleton;
        return skeleton;
    }

    /// <summary>The CK bakes heads to FaceGenData under the NPC's defining plugin.</summary>
    private static string FaceGenPath(FormKey npc) =>
        $@"meshes\actors\character\facegendata\facegeom\{npc.ModKey.FileName}\{npc.ID:X8}.nif";

    private static string? TexturePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return path.StartsWith("textures", StringComparison.OrdinalIgnoreCase) ? path : Path.Combine("textures", path);
    }

    private void AddMesh(NpcModel model, string relativePath, string label, bool isPiercing, string? skinDiffuse = null)
    {
        // The game blends each weighted mesh between its _0 (thin) and _1 (heavy) files by the NPC's
        // weight; FaceGen heads are baked at that weight, so an unblended body leaves a gap at the neck.
        var pair = WeightPair(relativePath);
        using var nif = LoadNif(pair?.Heavy ?? relativePath, model, report: pair == null)
                        ?? (pair != null ? LoadNif(relativePath, model, report: true) : null);
        if (nif == null) return;
        using var thin = pair is { } p && _weight < 1f ? LoadNif(p.Thin, model, report: false) : null;

        foreach (var shape in nif.GetShapes())
        {
            var thinShape = thin?.GetShapes().FirstOrDefault(s => s.name.get() == shape.name.get());
            var part = ReadShape(nif, shape, label, isPiercing, skinDiffuse,
                thinShape == null ? null : thin!.GetVertsForShape(thinShape));
            if (part != null) model.Parts.Add(part);
        }
    }

    private static bool Fits(IArmorAddonGetter addon, FormKey raceKey) =>
        addon.Race.FormKey == raceKey || addon.AdditionalRaces.Any(r => r.FormKey == raceKey);

    /// <summary>Addons may name either file of a weighted pair; returns (heavy _1, thin _0), or null for unweighted meshes.</summary>
    private static (string Heavy, string Thin)? WeightPair(string path)
    {
        if (path.EndsWith("_1.nif", StringComparison.OrdinalIgnoreCase)) return (path, path[..^6] + "_0.nif");
        if (path.EndsWith("_0.nif", StringComparison.OrdinalIgnoreCase)) return (path[..^6] + "_1.nif", path);
        return null;
    }

    private NifFile? LoadNif(string relativePath, NpcModel model, bool report)
    {
        var bytes = _assets.Read(relativePath);
        if (bytes == null)
        {
            if (report) model.Missing.Add(relativePath);
            return null;
        }

        // nifly reads from disk, so archive contents go through a scratch file.
        var tmp = Path.Combine(_tempFolder, Guid.NewGuid().ToString("N") + ".nif");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            var nif = new NifFile();
            if (nif.Load(tmp) == 0) return nif;
            nif.Dispose();
            if (report) model.Missing.Add(relativePath + " (unreadable)");
            return null;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// Bind-pose placement of a skinned shape: a vertex goes skin -> bone through the skin-to-bone
    /// transform, then bone -> skeleton through the bone's bind position. Sources, best first: the
    /// mesh's own node; a position already seen in a body mesh of this NPC (FaceGen heads store empty
    /// nodes, and borrowing the body's keeps the neck closed); the race skeleton.
    /// </summary>
    private MatTransform? SkinToGlobal(NifFile nif, NiShape shape)
    {
        if (!shape.HasSkinInstance()) return null;
        var bones = new vectorstring();
        if (nif.GetShapeBoneList(shape, bones) == 0) return null;
        var withSkin = new List<(string Bone, MatTransform SkinToBone)>();
        foreach (var bone in bones)
        {
            var skinToBone = new MatTransform();
            if (nif.GetShapeTransformSkinToBone(shape, bone, skinToBone)) withSkin.Add((bone, skinToBone));
        }

        MatTransform? result = null;
        foreach (var (bone, skinToBone) in withSkin)
        {
            var own = new MatTransform();
            if (!nif.GetNodeTransformToGlobal(bone, own) || IsOrigin(own)) continue;
            _boneGlobals.TryAdd(bone, own);
            result ??= own.ComposeTransforms(skinToBone);
        }
        if (result != null) return result;

        foreach (var (bone, skinToBone) in withSkin)
            if (_boneGlobals.TryGetValue(bone, out var seen))
                return seen.ComposeTransforms(skinToBone);

        foreach (var (bone, skinToBone) in withSkin)
        {
            var fromSkeleton = new MatTransform();
            if (_skeleton != null && _skeleton.GetNodeTransformToGlobal(bone, fromSkeleton))
            {
                // Shift the skeleton's pose onto the body's, so a part that only names a bone the body
                // lacks (a mouth bound to the head bone alone) still lines up with the rest.
                var shift = SkeletonToBodyShift();
                fromSkeleton.translation = new Vector3(fromSkeleton.translation.x + shift.X,
                    fromSkeleton.translation.y + shift.Y, fromSkeleton.translation.z + shift.Z);
                return fromSkeleton.ComposeTransforms(skinToBone);
            }
        }
        return null;
    }

    private sealed record Skinning(MatTransform?[] BoneToGlobal, float[] Weights, byte[] Bones);

    /// <summary>
    /// Per-vertex skinning data, so each vertex is posed on the actual skeleton the way the game does
    /// it: a weighted blend of (bone's skeleton position x skin-to-bone) over up to four bones. Bodies
    /// built for one skeleton and heads baked for another only meet at the neck once both are posed
    /// on the skeleton the NPC really uses. Only SSE triangle shapes carry these weights; anything
    /// else falls back to one rigid transform.
    /// </summary>
    private Skinning? SkinningFor(NifFile nif, NiShape shape, int vertexCount)
    {
        if (!shape.HasSkinInstance()) return null;
        var block = shape.GetBlockName();
        if (block != "BSTriShape" && block != "BSDynamicTriShape" && block != "BSSubIndexTriShape") return null;
        var bones = new vectorstring();
        if (nif.GetShapeBoneList(shape, bones) == 0) return null;

        var toGlobal = new MatTransform?[bones.Count];
        for (var b = 0; b < bones.Count; b++)
        {
            var skinToBone = new MatTransform();
            if (!nif.GetShapeTransformSkinToBone(shape, bones[b], skinToBone)) continue;
            var boneGlobal = new MatTransform();
            if (_skeleton != null && _skeleton.GetNodeTransformToGlobal(bones[b], boneGlobal)
                || nif.GetNodeTransformToGlobal(bones[b], boneGlobal) && !IsOrigin(boneGlobal)
                || _boneGlobals.TryGetValue(bones[b], out boneGlobal!))
                toGlobal[b] = boneGlobal.ComposeTransforms(skinToBone);
        }
        if (toGlobal.All(t => t == null)) return null;

        var vertData = AsTriShape(shape).vertData;
        if (vertData.Count != vertexCount) return null;
        var weights = new float[vertexCount * 4];
        var ids = new byte[vertexCount * 4];
        for (var i = 0; i < vertexCount; i++)
        {
            var d = vertData[i];
            d.weights.CopyTo(weights, i * 4);
            d.weightBones.CopyTo(ids, i * 4);
        }
        return new Skinning(toGlobal, weights, ids);
    }

    /// <summary>nifly hands shapes back as the NiShape base; a BSTriShape view of the same object reads its vertex data.</summary>
    private static BSTriShape AsTriShape(NiShape shape)
    {
        const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance
                                                  | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var handle = (System.Runtime.InteropServices.HandleRef)typeof(NiShape).GetMethod("getCPtr", any)!.Invoke(null, new object[] { shape })!;
        var ctor = typeof(BSTriShape).GetConstructor(any, new[] { typeof(IntPtr), typeof(bool) })!;
        return (BSTriShape)ctor.Invoke(new object[] { handle.Handle, false });
    }

    private static void Skin(Skinning skin, int i, Vector3 v, Vector3? normal, float[] pos, float[] nrm)
    {
        float px = 0, py = 0, pz = 0, nx = 0, ny = 0, nz = 0, total = 0;
        for (var k = 0; k < 4; k++)
        {
            var w = skin.Weights[i * 4 + k];
            if (w <= 0) continue;
            var b = skin.Bones[i * 4 + k];
            if (b >= skin.BoneToGlobal.Length || skin.BoneToGlobal[b] is not { } m) continue;
            var p = m.ApplyTransform(v);
            px += p.x * w; py += p.y * w; pz += p.z * w;
            if (normal != null)
            {
                var d = m.ApplyTransformToDir(normal);
                nx += d.x * w; ny += d.y * w; nz += d.z * w;
            }
            total += w;
        }
        if (total <= 0)
        {
            var m = skin.BoneToGlobal.First(t => t != null)!;
            var p = m.ApplyTransform(v);
            px = p.x; py = p.y; pz = p.z; total = 1;
        }
        pos[i * 3] = px / total; pos[i * 3 + 1] = py / total; pos[i * 3 + 2] = pz / total;
        nrm[i * 3] = nx; nrm[i * 3 + 1] = ny; nrm[i * 3 + 2] = nz;
    }

    private (float X, float Y, float Z) SkeletonToBodyShift()
    {
        if (_skeleton == null) return default;
        foreach (var bone in new[] { "NPC Neck [Neck]", "NPC Head [Head]", "NPC Spine2 [Spn2]" })
        {
            var fromSkeleton = new MatTransform();
            if (_boneGlobals.TryGetValue(bone, out var body) && _skeleton.GetNodeTransformToGlobal(bone, fromSkeleton))
                return (body.translation.x - fromSkeleton.translation.x,
                        body.translation.y - fromSkeleton.translation.y,
                        body.translation.z - fromSkeleton.translation.z);
        }
        return default;
    }

    /// <summary>The game only swaps in the skin texture set on shapes using the skin-tint shader.</summary>
    private static bool IsSkinShader(NifFile nif, NiShape shape) => ShaderType(nif, shape) == 5; // BSLSP_SKINTINT

    private static int ShaderType(NifFile nif, NiShape shape)
    {
        try
        {
            var shader = nif.GetShader(shape);
            return shader == null ? -1 : (int)shader.GetShaderType();
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>The hair colour on the winning NPC record, which the game multiplies into hair-tint shapes.</summary>
    private float[]? HairTintFor(NpcEntry npc)
    {
        if (!_cache.TryResolve<INpcGetter>(npc.FormKey, out var record)) return null;
        if (record.HairColor.IsNull || !_cache.TryResolve<IColorRecordGetter>(record.HairColor.FormKey, out var color)) return null;
        return new[] { color.Color.R / 255f, color.Color.G / 255f, color.Color.B / 255f };
    }

    /// <summary>
    /// A skin texture set only stands in for the ordinary body, hand, foot and head textures. Extra
    /// skin shapes with textures of their own (a body's separate genital mesh, say) keep theirs.
    /// </summary>
    private static bool IsStandardSkinTexture(string? path)
    {
        if (path == null) return true;
        var name = Path.GetFileNameWithoutExtension(path);
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"^(fe)?male(body|hands|feet|head)(_\d+)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// SMP/HDT physics ships collision proxies in hair and body meshes ("VirtualHead", "VirtualBody"
    /// and the like): hidden or untextured shapes the game never draws, which would show up white.
    /// </summary>
    private static bool IsPhysicsOrHidden(NifFile nif, NiShape shape)
    {
        if ((shape.flags & 1) != 0) return true; // NiAVObject "hidden"
        var name = shape.name.get() ?? "";
        if (name.StartsWith("Virtual", StringComparison.OrdinalIgnoreCase)
            || name.Contains("collision", StringComparison.OrdinalIgnoreCase)
            || name.Contains("proxy", StringComparison.OrdinalIgnoreCase)) return true;
        if (nif.GetShader(shape) == null) return true;
        string? diffuse = null;
        try { diffuse = nif.GetTexturePathByIndex(shape, 0); } catch { }
        return string.IsNullOrWhiteSpace(diffuse);
    }

    private static bool IsOrigin(MatTransform t) =>
        Math.Abs(t.translation.x) + Math.Abs(t.translation.y) + Math.Abs(t.translation.z) < 0.001f;

    /// <summary>
    /// Skin and head meshes using model-space normal maps store no vertex normals, so they are
    /// rebuilt from the triangles for the preview's lighting.
    /// </summary>
    private static bool FillMissingNormals(float[] pos, float[] nrm, uint[] idx)
    {
        var any = false;
        for (var i = 0; i < nrm.Length && !any; i++) any = nrm[i] != 0;
        if (any) return false;
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            int a = (int)idx[t] * 3, b = (int)idx[t + 1] * 3, c = (int)idx[t + 2] * 3;
            if (Math.Max(a, Math.Max(b, c)) + 2 >= pos.Length) continue;
            float ux = pos[b] - pos[a], uy = pos[b + 1] - pos[a + 1], uz = pos[b + 2] - pos[a + 2];
            float vx = pos[c] - pos[a], vy = pos[c + 1] - pos[a + 1], vz = pos[c + 2] - pos[a + 2];
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            foreach (var v in new[] { a, b, c })
            {
                nrm[v] += nx; nrm[v + 1] += ny; nrm[v + 2] += nz;
            }
        }
        for (var i = 0; i + 2 < nrm.Length; i += 3)
        {
            var len = MathF.Sqrt(nrm[i] * nrm[i] + nrm[i + 1] * nrm[i + 1] + nrm[i + 2] * nrm[i + 2]);
            if (len > 0) { nrm[i] /= len; nrm[i + 1] /= len; nrm[i + 2] /= len; }
            else nrm[i + 2] = 1;
        }
        return true;
    }

    private MeshPart? ReadShape(NifFile nif, NiShape shape, string label, bool isPiercing, string? skinDiffuse, vectorVector3? thinVerts)
    {
        if (IsPhysicsOrHidden(nif, shape)) return null;
        var verts = nif.GetVertsForShape(shape);
        if (verts == null || verts.Count == 0) return null;
        if (thinVerts != null && thinVerts.Count != verts.Count) thinVerts = null;
        var normals = nif.GetNormalsForShape(shape);
        var uvs = nif.GetUvsForShape(shape);
        var tris = new vectorTriangle();
        shape.GetTriangles(tris);

        // Skinned shapes store vertices in their own bind space; this brings each into the
        // skeleton space every body part and head share, which is how Outfit Studio lines them up.
        var global = SkinToGlobal(nif, shape) ?? shape.transform;
        var skin = SkinningFor(nif, shape, verts.Count);

        var n = verts.Count;
        var pos = new float[n * 3];
        var nrm = new float[n * 3];
        var uv = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            var v = verts[i];
            if (thinVerts != null)
            {
                var t = thinVerts[i];
                v = new Vector3(t.x + (v.x - t.x) * _weight, t.y + (v.y - t.y) * _weight, t.z + (v.z - t.z) * _weight);
            }
            if (skin != null)
            {
                Skin(skin, i, v, normals != null && normals.Count == n ? normals[i] : null, pos, nrm);
            }
            else
            {
                var p = global.ApplyTransform(v);
                pos[i * 3] = p.x; pos[i * 3 + 1] = p.y; pos[i * 3 + 2] = p.z;
                if (normals != null && normals.Count == n)
                {
                    var d = global.ApplyTransformToDir(normals[i]);
                    nrm[i * 3] = d.x; nrm[i * 3 + 1] = d.y; nrm[i * 3 + 2] = d.z;
                }
            }
            if (uvs != null && uvs.Count == n)
            {
                uv[i * 2] = uvs[i].u; uv[i * 2 + 1] = uvs[i].v;
            }
        }

        var idx = new uint[tris.Count * 3];
        for (var i = 0; i < tris.Count; i++)
        {
            idx[i * 3] = tris[i].p1; idx[i * 3 + 1] = tris[i].p2; idx[i * 3 + 2] = tris[i].p3;
        }

        var computed = FillMissingNormals(pos, nrm, idx);

        string? diffuse = null;
        try { diffuse = nif.GetTexturePathByIndex(shape, 0); } catch { }
        diffuse = TexturePath(diffuse);
        if (skinDiffuse != null && IsSkinShader(nif, shape) && IsStandardSkinTexture(diffuse)) diffuse = skinDiffuse;
        var alphaTest = nif.GetAlphaProperty(shape) != null;

        return new MeshPart
        {
            Name = $"{label}:{shape.name.get()}",
            Positions = pos, Normals = nrm, Uvs = uv, Indices = idx,
            DiffusePath = string.IsNullOrWhiteSpace(diffuse) ? null : diffuse,
            IsPiercing = isPiercing,
            AlphaTest = alphaTest,
            Tint = ShaderType(nif, shape) == 6 ? _hairTint : null, // BSLSP_HAIRTINT
            NormalsComputed = computed,
        };
    }
}
