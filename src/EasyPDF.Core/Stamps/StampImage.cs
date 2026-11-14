using System;

namespace EasyPDF.Core.Stamps;

/// <summary>Pixel work on stamp pictures.</summary>
public static class StampImage
{
    /// <summary>From this brightness of the darkest colour channel on a pixel counts as white (leaves room for the noise of a scan).</summary>
    private const int White = 250;

    /// <summary>Up to this brightness a pixel is ink and stays as it is; in between it fades, which keeps edges smooth.</summary>
    private const int Ink = 215;

    /// <summary>A copy of the picture (B, G, R, A with straight alpha) with white as transparent, so a picture with a white background doesn't cover the page.</summary>
    public static byte[] WhiteToTransparent(byte[] bgra)
    {
        var result = (byte[])bgra.Clone();
        for (var i = 0; i + 3 < result.Length; i += 4)
        {
            var light = Math.Min(result[i], Math.Min(result[i + 1], result[i + 2]));
            if (light <= Ink)
                continue;
            var alpha = light >= White ? 0 : (White - light) * 255 / (White - Ink);
            result[i + 3] = (byte)Math.Min(result[i + 3], alpha);
        }
        return result;
    }
}
