using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using Pfim;
using PiercingDistributionTool.Core.Assets;

namespace PiercingDistributionTool.Core.Preview;

public sealed record Rgba(int Width, int Height, byte[] Pixels);

/// <summary>
/// Decodes DDS textures to plain RGBA for upload. Block-compressed formats (BC1-BC7, which modern
/// texture mods use) go through BCnEncoder; uncompressed ones through Pfim; anything else is grey.
/// </summary>
public sealed class TextureLoader
{
    private readonly AssetResolver _assets;
    private readonly Dictionary<string, Rgba?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public TextureLoader(AssetResolver assets) => _assets = assets;

    public Rgba? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_cache.TryGetValue(path, out var hit)) return hit;

        Rgba? result = null;
        var bytes = _assets.Read(path);
        if (bytes != null)
        {
            result = DecodeBlockCompressed(bytes);
            if (result == null)
            {
                try
                {
                    using var image = Pfimage.FromStream(new MemoryStream(bytes));
                    result = ToRgba(image);
                }
                catch
                {
                    result = null;
                }
            }
        }
        _cache[path] = result;
        return result;
    }

    private static Rgba? DecodeBlockCompressed(byte[] bytes)
    {
        try
        {
            var dds = DdsFile.Load(new MemoryStream(bytes));
            var decoder = new BcDecoder();
            if (!decoder.IsHdrFormat(dds) && dds.Faces.Count > 0)
            {
                var pixels = decoder.Decode(dds);
                var w = (int)dds.header.dwWidth;
                var h = (int)dds.header.dwHeight;
                if (pixels.Length != w * h) return null;
                return new Rgba(w, h, MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray());
            }
        }
        catch
        {
            // not block-compressed, or a format BCnEncoder doesn't read
        }
        return null;
    }

    private static Rgba? ToRgba(IImage image)
    {
        var w = image.Width;
        var h = image.Height;
        var outPx = new byte[w * h * 4];
        var src = image.Data;
        var stride = image.Stride;

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = (y * w + x) * 4;
            switch (image.Format)
            {
                case ImageFormat.Rgba32:
                {
                    var i = y * stride + x * 4; // stored BGRA
                    outPx[o] = src[i + 2]; outPx[o + 1] = src[i + 1]; outPx[o + 2] = src[i]; outPx[o + 3] = src[i + 3];
                    break;
                }
                case ImageFormat.Rgb24:
                {
                    var i = y * stride + x * 3; // stored BGR
                    outPx[o] = src[i + 2]; outPx[o + 1] = src[i + 1]; outPx[o + 2] = src[i]; outPx[o + 3] = 255;
                    break;
                }
                case ImageFormat.Rgb8:
                {
                    var v = src[y * stride + x];
                    outPx[o] = outPx[o + 1] = outPx[o + 2] = v; outPx[o + 3] = 255;
                    break;
                }
                default:
                    return null;
            }
        }
        return new Rgba(w, h, outPx);
    }
}
