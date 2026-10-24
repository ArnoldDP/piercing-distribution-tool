using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Noggog;

namespace PiercingDistributionTool.Core.Assets;

/// <summary>
/// Finds game files the way the engine does: a loose file in Data beats any archive, and among
/// archives the later-loaded one wins.
/// </summary>
public sealed class AssetResolver
{
    private readonly string _dataFolder;
    private readonly Lazy<Dictionary<string, IArchiveFile>> _archiveIndex;

    public AssetResolver(string dataFolder)
    {
        _dataFolder = dataFolder;
        _archiveIndex = new Lazy<Dictionary<string, IArchiveFile>>(BuildIndex);
    }

    public static string Normalize(string path) =>
        path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    /// <summary>Returns the file's bytes, or null if no loose file or archive provides it.</summary>
    public byte[]? Read(string relativePath)
    {
        var rel = Normalize(relativePath);
        var loose = Path.Combine(_dataFolder, rel);
        if (File.Exists(loose)) return File.ReadAllBytes(loose);
        return _archiveIndex.Value.TryGetValue(rel, out var file) ? file.GetBytes() : null;
    }

    public bool Exists(string relativePath)
    {
        var rel = Normalize(relativePath);
        return File.Exists(Path.Combine(_dataFolder, rel)) || _archiveIndex.Value.ContainsKey(rel);
    }

    private Dictionary<string, IArchiveFile> BuildIndex()
    {
        var index = new Dictionary<string, IArchiveFile>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<FilePath> archives;
        try
        {
            archives = Archive.GetApplicableArchivePaths(GameRelease.SkyrimSE, new DirectoryPath(_dataFolder));
        }
        catch
        {
            archives = Directory.EnumerateFiles(_dataFolder, "*.bsa").Select(p => new FilePath(p));
        }

        // Later archives overwrite earlier entries, matching the engine's precedence.
        foreach (var archive in archives)
        {
            if (!archive.Exists) continue;
            try
            {
                var reader = Archive.CreateReader(GameRelease.SkyrimSE, archive);
                foreach (var file in reader.Files)
                    index[Normalize(file.Path)] = file;
            }
            catch
            {
                // An unreadable archive just contributes nothing to the preview.
            }
        }
        return index;
    }
}
