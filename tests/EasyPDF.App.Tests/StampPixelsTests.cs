using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EasyPDF.App.Services;
using Xunit;

namespace EasyPDF.App.Tests;

public class StampPixelsTests
{
    [AvaloniaFact]
    public void PictureBecomesStraightAlphaBgra()
    {
        // 2 x 1 pixels: opaque magenta, and magenta at half alpha (premultiplied: 128,0,128 / a=128)
        var source = new WriteableBitmap(new PixelSize(2, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = source.Lock())
        {
            System.Runtime.InteropServices.Marshal.WriteInt32(fb.Address, unchecked((int)0xFFFF00FF));
            System.Runtime.InteropServices.Marshal.WriteInt32(fb.Address + 4, unchecked((int)0x80800080));
        }
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stamp-{System.Guid.NewGuid():N}.png");
        try
        {
#pragma warning disable CS0618
            source.Save(path);
#pragma warning restore CS0618
            using var loaded = new Bitmap(path);
            var px = StampPixels.ToStraightBgra(loaded);
            Assert.Equal(8, px.Length);
            Assert.Equal(new byte[] { 255, 0, 255, 255 }, px[..4]);
            Assert.InRange(px[4], 250, 255);  // B back to full strength
            Assert.Equal(0, px[5]);
            Assert.InRange(px[6], 250, 255);  // R
            Assert.InRange(px[7], 126, 130);  // A stays
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
