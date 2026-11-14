using System;
using System.IO;
using SkiaSharp;

namespace EasyPDF.DemoVideo;

/// <summary>Fictional sample documents for the video, in its language; every name, number and bank account is made up.</summary>
internal sealed class DemoDocuments
{
    private static readonly SKColor Ink = new(0x22, 0x2B, 0x38);
    private static readonly SKColor Muted = new(0x66, 0x70, 0x80);
    private static readonly SKColor Line = new(0xD5, 0xDA, 0xE2);
    private static readonly SKColor[] Sections =
    [
        new(0x00, 0x4E, 0x8A), new(0x0F, 0x7B, 0x6C), new(0xB4, 0x5F, 0x06), new(0x7A, 0x3E, 0x9D), new(0xA6, 0x1E, 0x4D),
    ];

    private const float A4Width = 595, A4Height = 842, InvoiceHeight = 440;

    private readonly SKTypeface _regular, _bold;
    private readonly string _language;

    public DemoDocuments(string fontFolder, string language)
    {
        _language = language;
        _regular = SKTypeface.FromFile(Path.Combine(fontFolder, "Barlow-Regular.ttf"));
        _bold = SKTypeface.FromFile(Path.Combine(fontFolder, "Barlow-Bold.ttf"));
    }

    private string L(string german, string english) => _language == "de" ? german : english;

    private void Text(SKCanvas canvas, string text, float x, float y, float size, SKColor color, bool bold = false, SKTextAlign align = SKTextAlign.Left)
    {
        using var font = new SKFont(bold ? _bold : _regular, size);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, x, y, align, font, paint);
    }

    private static void Rect(SKCanvas canvas, float x, float y, float width, float height, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRect(x, y, width, height, paint);
    }

    /// <summary>The ten-page annual report: a large page number and a section colour tell the pages apart at a glance.</summary>
    public string Report(string path)
    {
        (string Title, int Section, int Kind)[] pages =
        [
            (L("Jahresbericht 2026", "Annual Report 2026"), 0, 0),
            (L("Das Jahr im Überblick", "The year at a glance"), 0, 1),
            (L("Umsatz nach Quartalen", "Revenue by quarter"), 1, 2),
            (L("Märkte und Kunden", "Markets and customers"), 1, 1),
            (L("Neue Produkte", "New products"), 2, 3),
            (L("Fertigung", "Production"), 2, 1),
            (L("Mitarbeitende", "Our people"), 3, 2),
            (L("Nachhaltigkeit", "Sustainability"), 3, 3),
            (L("Ausblick 2027", "Outlook 2027"), 4, 1),
            (L("Kontakt", "Contact"), 4, 1),
        ];
        using var stream = File.Create(path);
        using var document = SKDocument.CreatePdf(stream);
        for (var i = 0; i < pages.Length; i++)
        {
            var canvas = document.BeginPage(A4Width, A4Height);
            var (title, section, kind) = pages[i];
            var color = Sections[section];
            if (kind == 0)
                Cover(canvas, color);
            else
            {
                Rect(canvas, 0, 0, A4Width, 110, color);
                Text(canvas, title, 40, 72, 32, SKColors.White, bold: true);
                if (kind == 1)
                    Paragraphs(canvas, 150, 5);
                else if (kind == 2)
                {
                    Paragraphs(canvas, 150, 1);
                    Bars(canvas, color, 300, i);
                }
                else
                {
                    Paragraphs(canvas, 150, 2);
                    Table(canvas, color, 430);
                }
                Text(canvas, L("Musterwerk GmbH · Jahresbericht 2026", "Sample Works Inc. · Annual Report 2026"), 40, A4Height - 30, 11, Muted);
                Text(canvas, (i + 1).ToString(), A4Width - 40, A4Height - 30, 26, color, bold: true, SKTextAlign.Right);
            }
            document.EndPage();
        }
        document.Close();
        return path;
    }

    private void Cover(SKCanvas canvas, SKColor color)
    {
        Rect(canvas, 0, 0, A4Width, A4Height, color);
        Rect(canvas, 40, 300, 90, 8, new SKColor(0xDE, 0xDC, 0x00));
        Text(canvas, L("Jahresbericht", "Annual Report"), 40, 370, 58, SKColors.White, bold: true);
        Text(canvas, "2026", 40, 440, 58, SKColors.White, bold: true);
        Text(canvas, L("Musterwerk GmbH", "Sample Works Inc."), 40, A4Height - 60, 20, SKColors.White);
    }

    private void Paragraphs(SKCanvas canvas, float y, int count)
    {
        string[] lines =
        [
            L("Im vergangenen Geschäftsjahr hat sich das Unternehmen in einem", "In the past financial year the company held its own in a demanding"),
            L("anspruchsvollen Umfeld gut behauptet. Die Nachfrage blieb stabil,", "environment. Demand remained stable, and the new sites developed"),
            L("und die neuen Standorte haben sich schneller entwickelt als geplant.", "faster than planned. We thank everyone who contributed for their"),
            L("Wir danken allen, die daran mitgewirkt haben, für ihren Einsatz.", "commitment, and we look forward to the year ahead together."),
        ];
        for (var p = 0; p < count; p++)
        {
            foreach (var line in lines)
            {
                Text(canvas, line, 40, y, 14, Ink);
                y += 21;
            }
            y += 16;
        }
    }

    private void Bars(SKCanvas canvas, SKColor color, float top, int seed)
    {
        string[] labels = ["Q1", "Q2", "Q3", "Q4"];
        for (var i = 0; i < labels.Length; i++)
        {
            var height = 120 + (seed * 37 + i * 71) % 220;
            var x = 70 + i * 120;
            Rect(canvas, x, top + 340 - height, 76, height, color);
            Text(canvas, labels[i], x + 38, top + 366, 15, Muted, align: SKTextAlign.Center);
        }
        Rect(canvas, 40, top + 342, A4Width - 80, 1.5f, Line);
    }

    private void Table(SKCanvas canvas, SKColor color, float top)
    {
        (string Name, string Value)[] rows =
        [
            (L("Produktlinie Nord", "North product line"), L("2,4 Mio. €", "€2.4 m")), (L("Produktlinie Süd", "South product line"), L("1,8 Mio. €", "€1.8 m")), ("Service", L("0,9 Mio. €", "€0.9 m")), (L("Sonstiges", "Other"), L("0,3 Mio. €", "€0.3 m")),
        ];
        Rect(canvas, 40, top, A4Width - 80, 30, color);
        Text(canvas, L("Bereich", "Segment"), 52, top + 21, 14, SKColors.White, bold: true);
        Text(canvas, L("Umsatz", "Revenue"), A4Width - 52, top + 21, 14, SKColors.White, bold: true, SKTextAlign.Right);
        var y = top + 30;
        foreach (var (name, value) in rows)
        {
            Text(canvas, name, 52, y + 24, 14, Ink);
            Text(canvas, value, A4Width - 52, y + 24, 14, Ink, align: SKTextAlign.Right);
            Rect(canvas, 40, y + 36, A4Width - 80, 1, Line);
            y += 36;
        }
    }

    /// <summary>A short document for merging: one coloured title page per file, then plain pages.</summary>
    public string Simple(string path, string title, int pages, int section)
    {
        using var stream = File.Create(path);
        using var document = SKDocument.CreatePdf(stream);
        for (var i = 0; i < pages; i++)
        {
            var canvas = document.BeginPage(A4Width, A4Height);
            Rect(canvas, 0, 0, A4Width, 110, Sections[section]);
            Text(canvas, title, 40, 72, 32, SKColors.White, bold: true);
            Paragraphs(canvas, 150, 4);
            Text(canvas, L($"Seite {i + 1}", $"Page {i + 1}"), A4Width - 40, A4Height - 30, 16, Muted, align: SKTextAlign.Right);
            document.EndPage();
        }
        document.Close();
        return path;
    }

    /// <summary>The invoice as a scan: only a slightly skewed, noisy picture of the page, without a text layer. Wide, so it reads well in the video.</summary>
    public string ScannedInvoice(string path)
    {
        const int dpi = 200;
        var scale = dpi / 72f;
        var (width, height) = ((int)(A4Width * scale), (int)(InvoiceHeight * scale));
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0xFB, 0xFA, 0xF6));
            canvas.Scale(scale);
            canvas.RotateDegrees(0.25f, A4Width / 2, InvoiceHeight / 2);
            InvoicePage(canvas);
        }
        var random = new Random(7);
        for (var y = 0; y < height; y += 3)
            for (var x = random.Next(9); x < width; x += 9)
            {
                var c = bitmap.GetPixel(x, y);
                var d = random.Next(-10, 6);
                bitmap.SetPixel(x, y, new SKColor(Byte(c.Red + d), Byte(c.Green + d), Byte(c.Blue + d)));
            }

        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 82);
        using var encoded = SKImage.FromEncodedData(jpeg);
        using var stream = File.Create(path);
        using var document = SKDocument.CreatePdf(stream);
        var page = document.BeginPage(A4Width, InvoiceHeight);
        page.DrawImage(encoded, new SKRect(0, 0, A4Width, InvoiceHeight));
        document.EndPage();
        document.Close();
        return path;
    }

    private static byte Byte(int value) => (byte)Math.Clamp(value, 0, 255);

    private void InvoicePage(SKCanvas canvas)
    {
        Text(canvas, L("Musterwerk GmbH", "Sample Works Inc."), 40, 38, 22, Ink, bold: true);
        Text(canvas, L("Beispielstraße 1 · 12345 Musterstadt", "1 Sample Street · Sampletown 12345"), 40, 54, 10.5f, Muted);
        Text(canvas, L("Erika Mustermann", "Jane Example"), 40, 84, 12.5f, Ink);
        Text(canvas, L("Musterweg 7", "7 Example Road"), 40, 99, 12.5f, Ink);
        Text(canvas, L("54321 Beispielstadt", "Exampleville 54321"), 40, 114, 12.5f, Ink);
        Text(canvas, L("Rechnung Nr. 2026-0815", "Invoice No. 2026-0815"), 40, 150, 20, Ink, bold: true);
        Text(canvas, L("Rechnungsdatum: 02.11.2026", "Invoice date: 2 Nov 2026"), 40, 168, 11.5f, Muted);

        (string Name, string Quantity, string Sum)[] rows =
        [
            (L("Beratung, Stunden", "Consulting, hours"), "12", L("1.440,00 €", "€1,440.00")), (L("Wartungsvertrag 2026", "Maintenance contract 2026"), "1", L("890,00 €", "€890.00")), (L("Ersatzteile", "Spare parts"), "3", L("267,00 €", "€267.00")),
        ];
        var y = 196f;
        Text(canvas, L("Leistung", "Item"), 40, y, 12.5f, Ink, bold: true);
        Text(canvas, L("Menge", "Qty"), 400, y, 12.5f, Ink, bold: true, SKTextAlign.Right);
        Text(canvas, L("Betrag", "Amount"), A4Width - 40, y, 12.5f, Ink, bold: true, SKTextAlign.Right);
        Rect(canvas, 40, y + 7, A4Width - 80, 1.2f, Ink);
        y += 26;
        foreach (var (name, quantity, sum) in rows)
        {
            Text(canvas, name, 40, y, 12.5f, Ink);
            Text(canvas, quantity, 400, y, 12.5f, Ink, align: SKTextAlign.Right);
            Text(canvas, sum, A4Width - 40, y, 12.5f, Ink, align: SKTextAlign.Right);
            y += 19;
        }
        Rect(canvas, 40, y - 9, A4Width - 80, 1, Line);
        y += 9;
        Text(canvas, L("Gesamtbetrag", "Total"), 40, y, 14, Ink, bold: true);
        Text(canvas, L("2.597,00 €", "€2,597.00"), A4Width - 40, y, 14, Ink, bold: true, SKTextAlign.Right);

        Text(canvas, L("Bitte überweisen Sie den Betrag innerhalb von 14 Tagen.", "Please transfer the amount within 14 days."), 40, 322, 12.5f, Ink);
        Text(canvas, "IBAN: DE00 1234 5678 9012 3456 78", 40, 346, 13.5f, Ink, bold: true);
        Text(canvas, L("Vielen Dank für Ihren Auftrag!", "Thank you for your order!"), 40, 370, 12.5f, Ink);
    }
}
