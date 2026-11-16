using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;

namespace EasyPDF.App.Services;

/// <summary>Version and environment details for Help &gt; About and bug reports.</summary>
public static class VersionInfo
{
    /// <summary>The project's git repository: <c>RepositoryUrl</c> in Directory.Build.props (the SDK writes it into the assembly).</summary>
    public static string Repository { get; } = typeof(VersionInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value ?? "";

    /// <summary>Line "Author" in Help &gt; About: <c>AppAuthor</c> in the brand profile.</summary>
    public static string Author { get; } = typeof(VersionInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "AppAuthor")?.Value ?? "";

    /// <summary>Publisher (<c>Company</c> in the brand profile), e.g. the name of the company logo in Help &gt; About.</summary>
    public static string Company { get; } = typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "";

    /// <summary>Line "Copyright" in Help &gt; About: <c>Copyright</c> in the brand profile.</summary>
    public static string Copyright { get; } = typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    /// <summary>
    /// Product version with build number and commit, e.g. "1.0.0.97 (3fa9c1d2)" ("3fa9c1d2-dirty": built with uncommitted
    /// changes); see Directory.Build.targets.
    /// </summary>
    public static string Version => Display(
        typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    /// <summary>"1.0.0.97+3fa9c1d2" → "1.0.0.97 (3fa9c1d2)".</summary>
    public static string Display(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        return plus < 0 ? informationalVersion : $"{informationalVersion[..plus]} ({informationalVersion[(plus + 1)..]})";
    }

    /// <summary>Label key (resources) and value pairs.</summary>
    public static IReadOnlyList<(string LabelKey, string Value)> Collect()
    {
        var assembly = typeof(VersionInfo).Assembly;
        var pdfium = typeof(EasyPDF.Core.Pdfium.PdfSource).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PdfiumVersion")?.Value ?? "?";
        var buildDate = TryBuildDate(assembly);
        return new List<(string, string)>
        {
            ("InfoVersion", Version),
            ("InfoBuildDate", buildDate),
            ("InfoRuntime", RuntimeInformation.FrameworkDescription),
            ("InfoOs", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"),
            ("InfoAvalonia", typeof(Application).Assembly.GetName().Version?.ToString(3) ?? "?"),
            ("InfoPdfium", pdfium),
            ("InfoOcr", $"RapidOcrNet {VersionOf(typeof(RapidOcrNet.RapidOcr))}, ONNX Runtime {VersionOf(typeof(Microsoft.ML.OnnxRuntime.InferenceSession))}, PaddleOCR PP-OCRv5"),
            ("InfoAuthor", Author),
            ("InfoCopyright", Copyright),
            ("InfoLicense", "MIT"),
            ("InfoRepository", Repository),
        };
    }

    /// <summary>Folder with the license texts of the app and the bundled components (copied next to the program).</summary>
    public static string LicenseFolder => System.IO.Path.Combine(AppContext.BaseDirectory, "licenses");

    /// <summary>Package version of the assembly that defines <paramref name="type"/> (informational version without build metadata).</summary>
    private static string VersionOf(Type type)
    {
        var assembly = type.Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
            return info.Split('+')[0];
        return assembly.GetName().Version?.ToString(3) ?? "?";
    }

    private static string TryBuildDate(Assembly assembly)
    {
        try
        {
            // The assembly's .dll; under Native AOT there is none, the program file itself is the build
            var dll = System.IO.Path.Combine(AppContext.BaseDirectory, assembly.GetName().Name + ".dll");
            var file = System.IO.File.Exists(dll) ? dll : Environment.ProcessPath ?? dll;
            return System.IO.File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm");
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
