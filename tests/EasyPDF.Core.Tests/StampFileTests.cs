using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EasyPDF.Core.Stamps;
using EasyPDF.Core.Templates;
using Xunit;

namespace EasyPDF.Core.Tests;

public class StampFileTests
{
    /// <summary>A valid 1x1 PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static readonly StampDefinition Definition = new()
    {
        Fields =
        [
            new StampField { X = 0.1, Y = 0.58, Width = 0.8, Height = 0.17, Text = "{$DATE:dd. MMM. yyyy}", Color = "#D0021B", Uppercase = true, Language = "de" },
            new StampField { X = 0, Y = 0, Width = 1, Height = 0.2, Text = "Grüße", Align = StampFieldAlign.Left },
        ],
    };

    private static readonly TemplateContext Context = new(new DateTime(2019, 1, 11, 9, 30, 0), "wolfgang", "post.pdf", 2, 5);

    private static byte[] PngWithJson(string json)
    {
        // Written by hand, like another tool would: the same chunk as the program's but with any content
        var written = StampFile.WriteDefinition(Png, new StampDefinition { Fields = [new StampField { Width = 1, Height = 1, Text = "x" }] });
        var marker = Encoding.ASCII.GetBytes("iTXt");
        var at = written.AsSpan().IndexOf(marker);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(written.AsSpan(at - 4));
        var head = Encoding.Latin1.GetBytes(StampFile.Keyword).Concat(new byte[] { 0, 0, 0, 0, 0 }).Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        var chunk = new byte[4 + 4 + head.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(chunk, head.Length);
        marker.CopyTo(chunk, 4);
        head.CopyTo(chunk, 8);
        return [.. written[..(at - 4)], .. chunk, .. written[(at + 4 + length + 4)..]];
    }

    [Fact]
    public void ADefinitionSurvivesWritingAndReading()
    {
        var read = StampFile.ReadDefinition(StampFile.WriteDefinition(Png, Definition));

        Assert.NotNull(read);
        Assert.Equal(Definition.Fields, read.Fields);
    }

    [Fact]
    public void TheJsonInTheFileIsTheDocumentedOne()
    {
        var file = StampFile.WriteDefinition(Png, new StampDefinition { Fields = [Definition.Fields[0]] });

        var text = Encoding.UTF8.GetString(file);
        Assert.Contains("""{"fields":[{"x":0.1,"y":0.58,"w":0.8,"h":0.17,"text":"{$DATE:dd. MMM. yyyy}","color":"#D0021B","upper":true,"lang":"de","align":"center"}]}""", text.Replace("\\u0024", "$"));
    }

    [Fact]
    public void TheWrittenFileIsStillAValidPngWithCorrectChecksums()
    {
        var file = StampFile.WriteDpi(StampFile.WriteDefinition(Png, Definition), 300);

        var pos = 8;
        var types = new System.Collections.Generic.List<string>();
        while (pos < file.Length)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos));
            types.Add(Encoding.ASCII.GetString(file, pos + 4, 4));
            var stored = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos + 8 + length));
            Assert.Equal(Crc(file.AsSpan(pos + 4, 4 + length)), stored);
            pos += 12 + length;
        }
        Assert.Equal("IHDR", types[0]);
        Assert.Equal("IEND", types[^1]);
        Assert.True(types.IndexOf("pHYs") < types.IndexOf("IDAT")); // the PNG rules ask for that, and ReadDpi stops at IDAT
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void WritingTwiceLeavesOneDefinitionAndNoFieldsLeaveAPlainPicture()
    {
        var once = StampFile.WriteDefinition(Png, Definition);
        var other = new StampDefinition { Fields = [Definition.Fields[1]] };
        var twice = StampFile.WriteDefinition(once, other);

        Assert.Single(StampFile.ReadDefinition(twice)!.Fields);
        Assert.Equal(Png, StampFile.WriteDefinition(twice, new StampDefinition()));
        Assert.Equal(Png, StampFile.WriteDefinition(twice, null));
    }

    [Fact]
    public void TheResolutionIsStoredAndOtherChunksStay()
    {
        var file = StampFile.WriteDpi(StampFile.WriteDefinition(Png, Definition), 300);
        file = StampFile.WriteDpi(file, 150);

        Assert.Equal(150, StampLibrary.ReadDpi(file)!.Value, 0);
        Assert.NotNull(StampFile.ReadDefinition(file));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"fields":[]}""")]
    [InlineData("""{"fields":null}""")]
    [InlineData("""{"fields":[null]}""")]
    [InlineData("""{"fields":[{"x":0,"y":0,"w":0,"h":1,"text":"no area"}]}""")]
    [InlineData("""{"fields":[{"x":0.5,"y":0,"w":0.9,"h":1,"text":"too wide"}]}""")]
    [InlineData("""{"fields":[{"x":0,"y":0,"w":1,"h":1,"text":""}]}""")]
    public void AnUnusableDefinitionMakesAPlainPicture(string json)
    {
        Assert.Null(StampFile.ReadDefinition(PngWithJson(json)));
    }

    [Fact]
    public void GoodFieldsAreKeptWhenAnotherIsUnusable()
    {
        var read = StampFile.ReadDefinition(PngWithJson("""{"fields":[{"x":0,"y":0,"w":0,"h":1,"text":"no area"},{"x":0,"y":0,"w":1,"h":1,"text":"good","unknown":1}]}"""));

        Assert.Equal("good", Assert.Single(read!.Fields).Text);
    }

    [Fact]
    public void OtherFilesHaveNoDefinition()
    {
        Assert.Null(StampFile.ReadDefinition(Png));
        Assert.Null(StampFile.ReadDefinition([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0]));
        Assert.Null(StampFile.ReadDefinition([]));
        // Cut off in the middle of a chunk
        Assert.Null(StampFile.ReadDefinition(StampFile.WriteDefinition(Png, Definition).AsSpan(0, 50)));
        Assert.Throws<InvalidDataException>(() => StampFile.WriteDefinition([1, 2, 3], Definition));
    }

    [Theory]
    [InlineData("de", 3, "11. MÄR. 2019")]
    [InlineData("de", 9, "11. SEP. 2019")]
    [InlineData("de", 12, "11. DEZ. 2019")]
    [InlineData("en", 9, "11. SEP. 2019")]
    [InlineData("en", 10, "11. OCT. 2019")]
    public void MonthNamesAreTheSameOnEverySystemAndNeverBringADotOfTheirOwn(string language, int month, string expected)
    {
        var field = new StampField { Text = "{$DATE:dd. MMM. yyyy}", Uppercase = true, Language = language };

        Assert.Equal(expected, field.Expand(Context with { Now = new DateTime(2019, month, 11) }));
    }

    [Fact]
    public void TheLanguageOfTheStampWinsOverTheProgramLanguage()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var german = new StampField { Text = "{$DATE:dd. MMM yyyy}", Language = "de" };
            var program = new StampField { Text = "{$DATE:MMM}" };
            var unknown = new StampField { Text = "{$DATE:MMM}", Language = "not a language" };

            Assert.Equal("11. Mär 2019", german.Expand(Context with { Now = new DateTime(2019, 3, 11) }));
            Assert.Equal("Mar", program.Expand(Context with { Now = new DateTime(2019, 3, 11) }));
            Assert.Equal("Mar", unknown.Expand(Context with { Now = new DateTime(2019, 3, 11) }));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void OtherMacrosWorkInAFieldToo()
    {
        var field = new StampField { Text = "{$USER}, {$FILE}, {$PAGE}/{$PAGES}, {$NOPE}", Uppercase = false };

        Assert.Equal("wolfgang, post, 2/5, {$NOPE}", field.Expand(Context));
    }

    [Theory]
    [InlineData("{$DATE}", true)]
    [InlineData("am {$date:dd.MM.yyyy}", true)]
    [InlineData("{$USER}", false)]
    [InlineData("DATE", false)]
    public void AStampKnowsWhetherItShowsADate(string text, bool expected)
    {
        Assert.Equal(expected, new StampDefinition { Fields = [new StampField { Text = text }] }.UsesDate);
    }
}
