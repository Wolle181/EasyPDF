using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.Core.Tests;

public class TextStyleTests
{
    [Theory]
    [InlineData("#ff6600", "#FF6600")]
    [InlineData("ff6600", "#FF6600")]
    [InlineData(" #f60 ", "#FF6600")]
    [InlineData("", "#000000")]
    [InlineData(null, "#000000")]
    [InlineData("#12345", "#000000")]
    [InlineData("#GGGGGG", "#000000")]
    public void ColoursAreNormalised(string? input, string expected) =>
        Assert.Equal(expected, TextStyle.NormalizeColor(input));

    [Fact]
    public void RgbIsReadFromTheColour() => Assert.Equal(((byte)255, (byte)102, (byte)0), TextStyle.ToRgb("#FF6600"));

    [Theory]
    [InlineData("times", "Times")]
    [InlineData("Courier", "Courier")]
    [InlineData("Comic Sans", "Helvetica")]
    public void FontsFallBackToHelvetica(string input, string expected) =>
        Assert.Equal(expected, TextStyle.NormalizeFont(input));

    [Fact]
    public void SizesAreClamped()
    {
        Assert.Equal(TextStyle.MinSize, TextStyle.ClampSize(1));
        Assert.Equal(TextStyle.MaxSize, TextStyle.ClampSize(5000));
        Assert.Equal(14, TextStyle.ClampSize(14));
        Assert.Equal(12, TextStyle.ClampSize(double.NaN));
    }

    [Fact]
    public void TextStyleSurvivesSaveAndLoad()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"settings-{System.Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { TextColor = "#C00000", TextFont = "Courier", TextFontSize = 18 }.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal("#C00000", loaded.TextColor);
            Assert.Equal("Courier", loaded.TextFont);
            Assert.Equal(18, loaded.TextFontSize);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
