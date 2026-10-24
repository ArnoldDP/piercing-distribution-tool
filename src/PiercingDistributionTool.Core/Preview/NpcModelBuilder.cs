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

        return model;
    }

    private void AddAddon(NpcModel model, FormKey key, NpcEntry npc, bool isPiercing, bool ignoreRace = false)
    {
        if (!_cache.TryResolve<IArmorAddonGetter>(key, out var addon))
        {
            model.Missing.Add($"armor addon {key}");
            return;
        }
        if (!ignoreRace && addon.Race.FormKey != npc.RaceKey && addon.AdditionalRaces.All(r => r.FormKey != npc.RaceKey))
            return;

        var gendered = addon.WorldModel;
        var file = (npc.IsFemale ? gendered?.Female?.File : gendered?.Male?.File)?.DataRelativePath.Path
                   ?? gendered?.Male?.File?.DataRelativePath.Path;
        if (string.IsNullOrEmpty(file)) return;

        var path = file.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? file : Path.Combine("meshes", file);
        AddMesh(model, path, addon.EditorID ?? key.ToString(), isPiercing);
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

    private void AddMesh(NpcModel model, string relativePath, string label, bool isPiercing)
    {
        var bytes = _assets.Read(relativePath);
        if (bytes == null)
        {
            model.Missing.Add(relativePath);
            return;
        }

        // nifly reads from disk, so archive contents go through a scratch file.
        var tmp = Path.Combine(_tempFolder, Guid.NewGuid().ToString("N") + ".nif");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            using var nif = new NifFile();
            if (nif.Load(tmp) != 0)
            {
                model.Missing.Add(relativePath + " (unreadable)");
                return;
            }
            foreach (var shape in nif.GetShapes())
            {
                var part = ReadShape(nif, _skeleton, shape, label, isPiercing);
                if (part != null) model.Parts.Add(part);
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// Bind-pose placement of a skinned shape: a vertex goes skin -> bone through the skin-to-bone
    /// transform, then bone -> skeleton through the bone's bind position. The mesh's own node wins
    /// when it carries one, since bodies are bound to a pose that differs from the skeleton's rest
    /// pose; FaceGen heads often store empty nodes, so those fall back to the race skeleton. The head bone is preferred so every head part shares one
    /// reference; any bound bone gives the same answer in bind pose.
    /// </summary>
    private static MatTransform? SkinToGlobal(NifFile nif, NifFile? skeleton, NiShape shape)
    {
        if (!shape.HasSkinInstance()) return null;
        var bones = new vectorstring();
        if (nif.GetShapeBoneList(shape, bones) == 0) return null;
        var ordered = bones.OrderBy(b => b == "NPC Head [Head]" ? 0 : 1).ToList();
        foreach (var bone in ordered)
        {
            var boneGlobal = new MatTransform();
            var skinToBone = new MatTransform();
            if (!nif.GetShapeTransformSkinToBone(shape, bone, skinToBone)) continue;
            if (nif.GetNodeTransformToGlobal(bone, boneGlobal) && !IsOrigin(boneGlobal))
                return boneGlobal.ComposeTransforms(skinToBone);
            if (skeleton != null && skeleton.GetNodeTransformToGlobal(bone, boneGlobal))
                return boneGlobal.ComposeTransforms(skinToBone);
        }
        return null;
    }

    private static bool IsOrigin(MatTransform t) =>
        Math.Abs(t.translation.x) + Math.Abs(t.translation.y) + Math.Abs(t.translation.z) < 0.001f;

    /// <summary>
    /// Skin and head meshes using model-space normal maps store no vertex normals, so they are
    /// rebuilt from the triangles for the preview's lighting.
    /// </summary>
    private static void FillMissingNormals(float[] pos, float[] nrm, uint[] idx)
    {
        var any = false;
        for (var i = 0; i < nrm.Length && !any; i++) any = nrm[i] != 0;
        if (any) return;
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
    }

    private static MeshPart? ReadShape(NifFile nif, NifFile? skeleton, NiShape shape, string label, bool isPiercing)
    {
        var verts = nif.GetVertsForShape(shape);
        if (verts == null || verts.Count == 0) return null;
        var normals = nif.GetNormalsForShape(shape);
        var uvs = nif.GetUvsForShape(shape);
        var tris = new vectorTriangle();
        shape.GetTriangles(tris);

        // Skinned shapes store vertices in their own bind space; this brings each into the
        // skeleton space every body part and head share, which is how Outfit Studio lines them up.
        var global = SkinToGlobal(nif, skeleton, shape) ?? shape.transform;

        var n = verts.Count;
        var pos = new float[n * 3];
        var nrm = new float[n * 3];
        var uv = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            var p = global.ApplyTransform(verts[i]);
            pos[i * 3] = p.x; pos[i * 3 + 1] = p.y; pos[i * 3 + 2] = p.z;
            if (normals != null && normals.Count == n)
            {
                var d = global.ApplyTransformToDir(normals[i]);
                nrm[i * 3] = d.x; nrm[i * 3 + 1] = d.y; nrm[i * 3 + 2] = d.z;
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

        FillMissingNormals(pos, nrm, idx);

        string? diffuse = null;
        try { diffuse = nif.GetTexturePathByIndex(shape, 0); } catch { }
        if (!string.IsNullOrWhiteSpace(diffuse) && !diffuse.StartsWith("textures", StringComparison.OrdinalIgnoreCase))
            diffuse = Path.Combine("textures", diffuse);

        return new MeshPart
        {
            Name = $"{label}:{shape.name.get()}",
            Positions = pos, Normals = nrm, Uvs = uv, Indices = idx,
            DiffusePath = string.IsNullOrWhiteSpace(diffuse) ? null : diffuse,
            IsPiercing = isPiercing,
        };
    }
}
