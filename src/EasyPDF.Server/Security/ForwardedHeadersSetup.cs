using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace EasyPDF.Server.Security;

/// <summary>
/// Builds the <see cref="ForwardedHeadersOptions"/> for <c>UseForwardedHeaders</c>. Without a trusted proxy ASP.NET Core
/// ignores <c>X-Forwarded-Proto</c>, takes every request for HTTP and the client address for the proxy's (rate limits
/// would then count all users as one).
///
/// In a Docker container the host's reverse proxy arrives through the container gateway, not through loopback. So the
/// gateway is read from the container's routing table (<see cref="ReverseProxyOptions.TrustContainerGateway"/>) instead
/// of a fixed Docker address.
/// </summary>
public static class ForwardedHeadersSetup
{
    internal const string RouteTablePath = "/proc/net/route";

    /// <summary>
    /// The options. <paramref name="trustedDescriptions"/> lists the added proxies for the start log; if the gateway is
    /// wanted but cannot be found, a note says so.
    /// </summary>
    public static ForwardedHeadersOptions Create(ReverseProxyOptions config, bool runningInContainer, Func<string?> readRouteTable,
        out List<string> trustedDescriptions)
    {
        // The defaults (127.0.0.0/8 and ::1) stay; this only adds to them
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };
        trustedDescriptions = [];

        if (runningInContainer && config.TrustContainerGateway)
        {
            if (TryParseDefaultGateway(readRouteTable(), out var gateway))
            {
                options.KnownProxies.Add(gateway);
                trustedDescriptions.Add($"{gateway} (container gateway)");
            }
            else
                trustedDescriptions.Add("container gateway not found");
        }

        foreach (var entry in config.TrustedProxies)
        {
            if (!TryParseTrustedEntry(entry, out var address, out var network))
                continue;
            if (address is not null)
                options.KnownProxies.Add(address);
            if (network is not null)
                options.KnownIPNetworks.Add(network.Value);
            trustedDescriptions.Add(entry.Trim());
        }
        return options;
    }

    /// <summary>Reads the container's routing table; null if it cannot be read.</summary>
    public static string? ReadRouteTable()
    {
        try
        {
            return File.Exists(RouteTablePath) ? File.ReadAllText(RouteTablePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The IPv4 default gateway from the content of <c>/proc/net/route</c>: the line with destination <c>00000000</c> is the
    /// default route, its gateway is a hex value in reverse byte order (<c>010011AC</c> = 172.17.0.1).
    /// </summary>
    public static bool TryParseDefaultGateway(string? routeTable, out IPAddress gateway)
    {
        gateway = IPAddress.None;
        if (string.IsNullOrWhiteSpace(routeTable))
            return false;
        foreach (var line in routeTable.Split('\n').Skip(1))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 3 || columns[1] != "00000000" || columns[2].Length != 8)
                continue;
            if (!uint.TryParse(columns[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw) || raw == 0)
                continue;
            gateway = new IPAddress([(byte)raw, (byte)(raw >> 8), (byte)(raw >> 16), (byte)(raw >> 24)]);
            return true;
        }
        return false;
    }

    /// <summary>An entry is a single IP address or a network in CIDR notation.</summary>
    public static bool TryParseTrustedEntry(string? entry, out IPAddress? address, out System.Net.IPNetwork? network)
    {
        address = null;
        network = null;
        var trimmed = entry?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;
        if (trimmed.Contains('/'))
        {
            if (!System.Net.IPNetwork.TryParse(trimmed, out var parsed))
                return false;
            network = parsed;
            return true;
        }
        if (!IPAddress.TryParse(trimmed, out var ip))
            return false;
        address = ip;
        return true;
    }
}
