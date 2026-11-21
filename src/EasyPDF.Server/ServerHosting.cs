using System;
using System.IO;

namespace EasyPDF.Server;

/// <summary>How the server is deployed, for Help &gt; About of the browser program.</summary>
public static class ServerHosting
{
    /// <summary>Set by the official .NET images (also runtime-deps); absent when the server runs natively.</summary>
    public const string ContainerVariable = "DOTNET_RUNNING_IN_CONTAINER";

    public static bool InContainer => IsTrue(Environment.GetEnvironmentVariable(ContainerVariable));

    /// <summary>When the server's program file was built: in the container the time its image was built (the deployment).</summary>
    public static DateTimeOffset DeployedAt { get; } = BuildTime();

    public static bool IsTrue(string? value) => bool.TryParse(value, out var result) && result;

    private static DateTimeOffset BuildTime()
    {
        var file = Path.Combine(AppContext.BaseDirectory, typeof(ServerHosting).Assembly.GetName().Name + ".dll");
        if (!File.Exists(file))
            file = Environment.ProcessPath ?? file;
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }
}
