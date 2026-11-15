using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UCS.Common;

public sealed class ReleaseAsset
{
    public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    public long Size { get; set; }
}

public sealed class ReleaseInfo
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    public bool Prerelease { get; set; }
    public bool Draft { get; set; }
    [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
    public List<ReleaseAsset> Assets { get; set; } = new();

    [JsonIgnore] public SemVersion? Version => SemVersion.TryParse(TagName, out var v) ? v : null;

    public ReleaseAsset? FindAsset(Func<string, bool> match) => Assets.FirstOrDefault(a => match(a.Name));
}

/// <summary>Reads the project's releases from the public GitHub API (no account needed).</summary>
public static class GitHubReleases
{
    public const string Owner = "LR-Almeida";
    public const string Repo = "DCS-UCS";
    public const string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases";
    private const string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=30";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static HttpClient CreateHttpClient(string product, TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(product, Protocol.Version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Newest published release strictly newer than <paramref name="current"/>, or null.</summary>
    public static async Task<ReleaseInfo?> FindNewerAsync(string product, SemVersion current, bool includePreReleases, CancellationToken ct)
    {
        using var http = CreateHttpClient(product, TimeSpan.FromSeconds(15));
        var json = await http.GetStringAsync(ApiUrl, ct).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize<List<ReleaseInfo>>(json, Json) ?? new();
        return releases
            .Where(r => !r.Draft && r.Version != null && (includePreReleases || !r.Prerelease))
            .Where(r => r.Version! > current)
            .OrderByDescending(r => r.Version!)
            .FirstOrDefault();
    }
}
