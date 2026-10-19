using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Scour.Core.Updates;

public sealed record UpdateInfo(Version Latest, string Url, bool IsNewer);

/// <summary>
/// Opt-in update check. One anonymous GET to the GitHub releases API: no identifiers, no version,
/// no machine details. Never downloads anything; the user follows the link.
/// </summary>
public static class UpdateChecker
{
    public static Version CurrentVersion =>
        typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0);

    public static string Repository =>
        typeof(UpdateChecker).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ScourRepository")?.Value ?? "scour/scour";

    public static string ReleasesUrl => $"https://github.com/{Repository}/releases";

    public static async Task<UpdateInfo?> CheckAsync(HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        var owned = http is null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            // GitHub requires a User-Agent; this one carries no version or identifier.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Scour", null));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesUrl : ReleasesUrl;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return null;

            return new UpdateInfo(latest, url, latest > CurrentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (owned)
                http.Dispose();
        }
    }
}
