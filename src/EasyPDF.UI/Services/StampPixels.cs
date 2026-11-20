using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EasyPDF.App.Services;

/// <summary>Turns a decoded picture into the pixel format the saver needs.</summary>
public static class StampPixels
{
    /// <summary>B, G, R, A bytes with straight (not premultiplied) alpha, row after row without padding.</summary>
    public static byte[] ToStraightBgra(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var copy = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = copy.Lock())
        {
            bitmap.CopyPixels(new PixelRect(size), fb.Address, fb.RowBytes * size.Height, fb.RowBytes);
            for (var y = 0; y < size.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, pixels, y * stride, stride);
        }
        if (bitmap.AlphaFormat == AlphaFormat.Unpremul)
            return pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a is 0 or 255)
                continue;
            for (var c = 0; c < 3; c++)
                pixels[i + c] = (byte)Math.Min(255, (pixels[i + c] * 255 + a / 2) / a);
        }
        return pixels;
    }
}
