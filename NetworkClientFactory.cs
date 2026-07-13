using Microsoft.Win32;
using System.Net;

namespace CodexQuotaFloat;

internal static class NetworkClientFactory
{
    internal static readonly Uri UsageEndpoint = new("https://chatgpt.com/backend-api/wham/usage");

    internal static HttpClient Create()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };

        var windowsProxy = ResolveWindowsProxy();
        if (windowsProxy is not null && !HasHttpsEnvironmentProxy())
        {
            handler.Proxy = new WebProxy(windowsProxy);
            handler.UseProxy = true;
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
    }

    internal static Uri? ResolveWindowsProxy()
    {
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (settings?.GetValue("ProxyEnable") is not int enabled || enabled == 0)
            {
                return null;
            }

            var server = settings.GetValue("ProxyServer") as string;
            if (string.IsNullOrWhiteSpace(server))
            {
                return null;
            }

            var candidate = SelectHttpsProxy(server);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            if (!candidate.Contains("://", StringComparison.Ordinal))
            {
                candidate = "http://" + candidate;
            }

            return Uri.TryCreate(candidate, UriKind.Absolute, out var proxyUri)
                ? proxyUri
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? SelectHttpsProxy(string server)
    {
        if (!server.Contains('='))
        {
            return server.Trim();
        }

        string? http = null;
        string? socks = null;
        foreach (var entry in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                continue;
            }

            var scheme = entry[..separator].Trim();
            var value = entry[(separator + 1)..].Trim();
            if (scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
            if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                http = value;
            }
            else if (scheme.Equals("socks", StringComparison.OrdinalIgnoreCase))
            {
                socks = value.Contains("://", StringComparison.Ordinal) ? value : "socks5://" + value;
            }
        }

        return http ?? socks;
    }

    private static bool HasHttpsEnvironmentProxy()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HTTPS_PROXY")) ||
               !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("https_proxy")) ||
               !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALL_PROXY")) ||
               !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("all_proxy"));
    }
}
