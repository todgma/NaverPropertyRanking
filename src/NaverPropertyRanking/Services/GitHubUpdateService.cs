using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public sealed class GitHubUpdateService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly UpdateConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public GitHubUpdateService(UpdateConfiguration configuration, HttpMessageHandler? handler = null)
    {
        _configuration = configuration;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(12);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("NaverPropertyRanking", NormalizeVersion(configuration.CurrentVersion)));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var currentText = string.IsNullOrWhiteSpace(_configuration.CurrentVersion)
            ? Application.ProductVersion
            : _configuration.CurrentVersion;
        if (!_configuration.Enabled || !_configuration.CheckOnStartup)
            return new UpdateCheckResult(false, currentText, currentText, string.Empty, "업데이트 확인이 비활성화되어 있습니다.");
        if (!Uri.TryCreate(_configuration.LatestReleaseApiUrl, UriKind.Absolute, out var endpoint))
            return new UpdateCheckResult(false, currentText, currentText, string.Empty, "업데이트 API 주소가 올바르지 않습니다.");

        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(false, currentText, currentText, string.Empty,
                    $"업데이트 확인 실패: HTTP {(int)response.StatusCode}");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken);
            var latestText = release?.TagName?.TrimStart('v', 'V') ?? currentText;
            if (!TryVersion(currentText, out var current) || !TryVersion(latestText, out var latest))
                return new UpdateCheckResult(false, currentText, latestText, string.Empty, "버전 형식을 비교할 수 없습니다.");

            var downloadUrl = release?.Assets?
                .FirstOrDefault(asset => string.Equals(asset.Name, _configuration.AssetName, StringComparison.OrdinalIgnoreCase))
                ?.BrowserDownloadUrl;
            downloadUrl ??= release?.HtmlUrl;
            downloadUrl ??= _configuration.ReleasesPageUrl;
            var available = latest > current;
            return new UpdateCheckResult(
                available,
                currentText,
                latestText,
                downloadUrl ?? string.Empty,
                available ? $"새 버전 {latestText}을 사용할 수 있습니다." : "최신 버전입니다.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpdateCheckResult(false, currentText, currentText, string.Empty, $"업데이트 확인 실패: {ex.Message}");
        }
    }

    private static bool TryVersion(string value, out Version version) =>
        Version.TryParse(value.Trim().TrimStart('v', 'V').Split('-')[0], out version!);

    private static string NormalizeVersion(string value) =>
        TryVersion(value, out var version) ? version.ToString() : "1.0.0";

    public void Dispose() => _httpClient.Dispose();

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
