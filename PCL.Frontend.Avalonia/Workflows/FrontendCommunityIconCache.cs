using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PCL.Frontend.Avalonia.Workflows;

internal static class FrontendCommunityIconCache
{
    /// <summary>
    /// Time to wait before retrying an icon that failed to download. A permanent negative cache makes
    /// a single transient network hiccup stick for the whole process lifetime, which shows up as every
    /// catalog entry falling back to the placeholder block icon.
    /// </summary>
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(2);

    private static readonly HttpClient HttpClient = CreateHttpClient();

    /// <summary>
    /// media.forgecdn.net is unreachable on some networks (DNS resolves, TCP hangs). Probing once keeps the
    /// catalog from waiting out a full request timeout for every single entry before using the fallback.
    /// </summary>
    private static readonly Lazy<Task<bool>> ForgeCdnReachable = new(ProbeForgeCdnAsync);
    private static readonly ConcurrentDictionary<string, string?> IconCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> FailedIcons = new(StringComparer.Ordinal);

    public static string? TryGetCachedIconPath(string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
        {
            return null;
        }

        if (IconCache.TryGetValue(iconUrl, out var cachedPath)
            && !string.IsNullOrWhiteSpace(cachedPath)
            && File.Exists(cachedPath))
        {
            return cachedPath;
        }

        var targetPath = BuildIconPath(iconUrl, mediaType: null);
        if (File.Exists(targetPath))
        {
            IconCache[iconUrl] = targetPath;
            return targetPath;
        }

        return null;
    }

    public static async Task<string?> EnsureCachedIconAsync(
        string? iconUrl,
        string? fallbackProjectSlug = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
        {
            return await EnsureFallbackIconAsync(fallbackProjectSlug, cancellationToken);
        }

        var cachedPath = TryGetCachedIconPath(iconUrl);
        if (!string.IsNullOrWhiteSpace(cachedPath))
        {
            return cachedPath;
        }

        if (FailedIcons.TryGetValue(iconUrl, out var failedAt)
            && DateTimeOffset.UtcNow - failedAt < FailureRetryDelay)
        {
            return await EnsureFallbackIconAsync(fallbackProjectSlug, cancellationToken);
        }

        if (!await IsIconHostReachableAsync(iconUrl).ConfigureAwait(false))
        {
            FailedIcons[iconUrl] = DateTimeOffset.UtcNow;
            return await EnsureFallbackIconAsync(fallbackProjectSlug, cancellationToken);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, iconUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var targetPath = BuildIconPath(iconUrl, response.Content.Headers.ContentType?.MediaType);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            if (!File.Exists(targetPath))
            {
                var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                await File.WriteAllBytesAsync(targetPath, payload, cancellationToken);
            }

            IconCache[iconUrl] = targetPath;
            FailedIcons.TryRemove(iconUrl, out _);
            return targetPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            FailedIcons[iconUrl] = DateTimeOffset.UtcNow;
            return await EnsureFallbackIconAsync(fallbackProjectSlug, cancellationToken);
        }
    }

    /// <summary>
    /// CurseForge thumbnails are served from media.forgecdn.net, which is unreachable on some networks
    /// even though the directory API works (through a mirror). Fall back to the project's Modrinth icon,
    /// looked up by slug, because most popular projects are published on both platforms.
    /// </summary>
    private static async Task<string?> EnsureFallbackIconAsync(string? projectSlug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectSlug))
        {
            return null;
        }

        var cacheKey = "modrinth:" + projectSlug;
        if (IconCache.TryGetValue(cacheKey, out var cachedPath)
            && !string.IsNullOrWhiteSpace(cachedPath)
            && File.Exists(cachedPath))
        {
            return cachedPath;
        }

        if (FailedIcons.TryGetValue(cacheKey, out var failedAt)
            && DateTimeOffset.UtcNow - failedAt < FailureRetryDelay)
        {
            return null;
        }

        try
        {
            using var projectRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectSlug)}");
            projectRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var projectResponse = await HttpClient.SendAsync(projectRequest, cancellationToken);
            if (!projectResponse.IsSuccessStatusCode)
            {
                FailedIcons[cacheKey] = DateTimeOffset.UtcNow;
                return null;
            }

            var payload = await projectResponse.Content.ReadAsStringAsync(cancellationToken);
            var iconUrl = JsonNode.Parse(payload) is JsonObject project
                ? project["icon_url"]?.GetValue<string>()
                : null;
            if (string.IsNullOrWhiteSpace(iconUrl))
            {
                FailedIcons[cacheKey] = DateTimeOffset.UtcNow;
                return null;
            }

            using var imageRequest = new HttpRequestMessage(HttpMethod.Get, iconUrl);
            imageRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            using var imageResponse = await HttpClient.SendAsync(imageRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!imageResponse.IsSuccessStatusCode)
            {
                FailedIcons[cacheKey] = DateTimeOffset.UtcNow;
                return null;
            }

            var targetPath = BuildIconPath(iconUrl, imageResponse.Content.Headers.ContentType?.MediaType);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);

            IconCache[cacheKey] = targetPath;
            FailedIcons.TryRemove(cacheKey, out _);
            return targetPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            FailedIcons[cacheKey] = DateTimeOffset.UtcNow;
            return null;
        }
    }

    private static async Task<bool> IsIconHostReachableAsync(string iconUrl)
    {
        if (!Uri.TryCreate(iconUrl, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("media.forgecdn.net", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return await ForgeCdnReachable.Value.ConfigureAwait(false);
        }
        catch
        {
            return true;
        }
    }

    private static async Task<bool> ProbeForgeCdnAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://media.forgecdn.net/");
            using var response = await HttpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode || (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildIconPath(string iconUrl, string? mediaType)
    {
        var extension = ResolveIconExtension(iconUrl, mediaType);
        var iconDirectory = Path.Combine(Path.GetTempPath(), "PCL-L", "frontend-resource-icons");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(iconUrl))).ToLowerInvariant();
        return Path.Combine(iconDirectory, $"{hash}{extension}");
    }

    private static string ResolveIconExtension(string iconUrl, string? mediaType)
    {
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            return mediaType.ToLowerInvariant() switch
            {
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".png"
            };
        }

        var extension = Path.GetExtension(iconUrl);
        return string.IsNullOrWhiteSpace(extension) || extension == "." ? ".png" : extension;
    }

    private static HttpClient CreateHttpClient()
    {
        return FrontendHttpProxyService.CreateLauncherHttpClient(
            TimeSpan.FromSeconds(10),
            automaticDecompression: System.Net.DecompressionMethods.GZip
                                    | System.Net.DecompressionMethods.Deflate
                                    | System.Net.DecompressionMethods.Brotli);
    }
}
