using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;

namespace EasyPDF.Core.Ocr;

/// <summary>
/// OCR with RapidOcrNet: PaddleOCR PP-OCRv5 models (Latin script, covers German and English) on ONNX Runtime.
/// The models ship with the app in <see cref="DefaultModelDirectory"/>. Calls are serialized (one recognition at a time;
/// the engine itself uses several cores).
/// </summary>
public sealed partial class RapidOcrEngine : IOcrEngine
{
    /// <summary>Longest image side for text detection. The default 1024 misses small print on full pages (measured: 79 % vs. 98 % of the words at 150 dpi).</summary>
    public const int DetectionSize = 2048;

    private readonly RapidOcr _ocr = new();
    private readonly SessionOptions _session;
    private readonly RapidOcrOptions _options = RapidOcrOptions.Default with { ReturnWordBox = true, ImgResize = DetectionSize };
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <summary>Loads the models (about 0.3 s). Throws <see cref="FileNotFoundException"/> if a model file is missing.</summary>
    public RapidOcrEngine(string? modelDirectory = null)
    {
        DisableOnnxTelemetry();
        var dir = modelDirectory ?? DefaultModelDirectory;
        string Model(string name)
        {
            var path = Path.Combine(dir, name);
            return File.Exists(path) ? path : throw new FileNotFoundException("OCR model not found.", path);
        }
        // Absolute paths: RapidOcrNet's defaults are relative to the current directory
        string det = Model("ch_PP-OCRv5_mobile_det.onnx"), cls = Model("ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            rec = Model("latin_PP-OCRv5_rec_mobile_infer.onnx"), keys = Model("ppocrv5_latin_dict.txt");

        // Without ONNX Runtime's memory arena the buffers go back after each page: on A4 scans the peak drops from
        // 1.5 GB to 0.84 GB and 0.33 GB instead of 1.27 GB stay in use afterwards, for about 5 % more time.
        _session = RapidOcr.GetDefaultSessionOptions();
        _session.EnableCpuMemArena = false;
        _session.EnableMemoryPattern = false;
        _ocr.InitModels(det, cls, rec, keys, _session);
    }

    /// <summary>
    /// ONNX Runtime (1.29) collects a device id and sends telemetry, on Linux also by running <c>blkid</c> and
    /// <c>hostname</c> through <c>/bin/sh</c>: not wanted in a program that works offline, and in a container without a
    /// shell it crashed the process. <c>ORT_DISABLE_TELEMETRY</c> must be in the native environment before the runtime
    /// starts (.NET's own environment is not the C library's on Linux/macOS, hence setenv); the API call covers Windows.
    /// </summary>
    private static void DisableOnnxTelemetry()
    {
        if (!OperatingSystem.IsWindows())
            _ = SetEnv("ORT_DISABLE_TELEMETRY", "1", 1);
        Environment.SetEnvironmentVariable("ORT_DISABLE_TELEMETRY", "1");
        try
        {
            OrtEnv.Instance().DisableTelemetryEvents();
        }
        catch (OnnxRuntimeException)
        {
            // Not fatal: the environment variable already switched it off
        }
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);

    /// <summary>Folder next to the program that holds the models (copied there by the Core project).</summary>
    public static string DefaultModelDirectory => Path.Combine(AppContext.BaseDirectory, "ocr-models");

    public IReadOnlyList<OcrWord> Recognize(RenderedPage image, CancellationToken token = default)
    {
        using var bitmap = ToBitmap(image);
        OcrResult result;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            result = _ocr.Detect(bitmap, _options, token);
        }
        token.ThrowIfCancellationRequested();

        var words = new List<OcrWord>();
        foreach (var block in result.TextBlocks)
        {
            if (block.WordResults is { Length: > 0 } wordResults)
            {
                foreach (var w in wordResults)
                    if (!string.IsNullOrWhiteSpace(w.Text))
                        words.Add(new OcrWord(w.Text.Trim(), Bounds(w.BoxPoints), w.Score));
            }
            else if (!string.IsNullOrWhiteSpace(block.Text))
            {
                // No word boxes: split the line box evenly by character count
                var box = Bounds(block.BoxPoints);
                var parts = block.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var total = Math.Max(1, block.Text.Length);
                var pos = 0;
                foreach (var part in parts)
                {
                    var start = block.Text.IndexOf(part, pos, StringComparison.Ordinal);
                    var x0 = box.X + box.Width * start / total;
                    var x1 = box.X + box.Width * (start + part.Length) / total;
                    words.Add(new OcrWord(part, new RectPt(x0, box.Y, x1 - x0, box.Height), block.BoxScore));
                    pos = start + part.Length;
                }
            }
        }
        return words;
    }

    private static RectPt Bounds(SKPointI[] points)
    {
        if (points.Length == 0)
            return default;
        int x0 = points.Min(p => p.X), y0 = points.Min(p => p.Y), x1 = points.Max(p => p.X), y1 = points.Max(p => p.Y);
        return new RectPt(x0, y0, x1 - x0, y1 - y0);
    }

    private static SKBitmap ToBitmap(RenderedPage image)
    {
        var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var target = bitmap.GetPixels();
        var rowBytes = image.Width * 4;
        for (var y = 0; y < image.Height; y++)
            Marshal.Copy(image.Pixels, y * image.Stride, target + y * bitmap.RowBytes, rowBytes);
        return bitmap;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _ocr.Dispose();
            _session.Dispose();
        }
    }
}
