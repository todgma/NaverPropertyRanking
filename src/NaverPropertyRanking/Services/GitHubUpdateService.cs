using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public sealed class GitHubUpdateService : IDisposable
{
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
        var apiUrl = string.IsNullOrWhiteSpace(_configuration.ReleasesApiUrl)
            ? _configuration.LatestReleaseApiUrl
            : _configuration.ReleasesApiUrl;
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            return new UpdateCheckResult(false, currentText, currentText, string.Empty, "업데이트 API 주소가 올바르지 않습니다.");

        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(false, currentText, currentText, string.Empty,
                    $"업데이트 확인 실패: HTTP {(int)response.StatusCode}");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var release = FindLatestRelease(document.RootElement, _configuration.ReleaseTagPrefix);
            if (release is null)
                return new UpdateCheckResult(false, currentText, currentText, string.Empty,
                    "설정한 태그 접두사와 일치하는 GitHub Release가 없습니다.");

            var latestText = release.Value.VersionText;
            if (!TryVersion(currentText, out var current) || !TryVersion(latestText, out var latest))
                return new UpdateCheckResult(false, currentText, latestText, string.Empty, "버전 형식을 비교할 수 없습니다.");

            var downloadUrl = release.Value.Assets
                .FirstOrDefault(asset => string.Equals(asset.Name, _configuration.AssetName, StringComparison.OrdinalIgnoreCase))
                .DownloadUrl;
            var available = latest > current;
            return new UpdateCheckResult(
                available,
                currentText,
                latestText,
                downloadUrl ?? string.Empty,
                available && string.IsNullOrWhiteSpace(downloadUrl)
                    ? $"새 버전 {latestText}은 있지만 {_configuration.AssetName} 파일이 없습니다."
                    : available ? $"새 버전 {latestText}을 사용할 수 있습니다." : "최신 버전입니다.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpdateCheckResult(false, currentText, currentText, string.Empty, $"업데이트 확인 실패: {ex.Message}");
        }
    }

    public async Task<string> DownloadUpdateAsync(
        UpdateCheckResult update,
        CancellationToken cancellationToken)
    {
        if (!update.UpdateAvailable) throw new InvalidOperationException("다운로드할 업데이트가 없습니다.");
        if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("업데이트 다운로드 주소가 올바르지 않습니다.");

        var updateDirectory = Path.Combine(
            Path.GetTempPath(),
            "NaverPropertyRanking",
            "updates",
            update.LatestVersion);
        Directory.CreateDirectory(updateDirectory);
        var downloadPath = Path.Combine(updateDirectory, "NaverPropertyRanking.download");
        var executablePath = Path.Combine(updateDirectory, "NaverPropertyRanking.exe");

        using var response = await _httpClient.GetAsync(
            downloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 500_000_000)
            throw new InvalidDataException("업데이트 파일 크기가 허용 범위를 초과했습니다.");

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(
                         downloadPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         FileOptions.Asynchronous))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        ValidateDownloadedExecutable(downloadPath, update.LatestVersion);
        File.Move(downloadPath, executablePath, true);
        return executablePath;
    }

    private static bool TryVersion(string value, out Version version) =>
        Version.TryParse(
            value.Trim().TrimStart('v', 'V').Split('-')[0].Split('+')[0],
            out version!);

    private static string NormalizeVersion(string value) =>
        TryVersion(value, out var version) ? version.ToString() : "1.0.0";

    private static ReleaseInfo? FindLatestRelease(JsonElement root, string tagPrefix)
    {
        var releases = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : root.ValueKind == JsonValueKind.Object ? [root] : [];
        ReleaseInfo? latest = null;
        foreach (var release in releases)
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (release.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True) continue;
            if (!release.TryGetProperty("tag_name", out var tagElement)) continue;
            var tag = tagElement.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(tagPrefix) &&
                !tag.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var versionText = string.IsNullOrWhiteSpace(tagPrefix)
                ? tag.TrimStart('v', 'V')
                : tag[tagPrefix.Length..];
            if (!TryVersion(versionText, out var version)) continue;

            var assets = new List<ReleaseAsset>();
            if (release.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;
                    var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                        ? urlElement.GetString() ?? string.Empty
                        : string.Empty;
                    assets.Add(new ReleaseAsset(name, url));
                }
            }

            if (latest is null || version > latest.Value.Version)
                latest = new ReleaseInfo(version, versionText, assets);
        }
        return latest;
    }

    private static void ValidateDownloadedExecutable(string path, string expectedVersion)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < 100_000)
            throw new InvalidDataException("다운로드한 업데이트 파일이 올바른 실행 파일이 아닙니다.");
        using (var stream = File.OpenRead(path))
        {
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                throw new InvalidDataException("다운로드한 파일에 Windows 실행 파일 서명이 없습니다.");
        }

        var downloadedVersionText = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        if (!TryVersion(expectedVersion, out var expected) ||
            string.IsNullOrWhiteSpace(downloadedVersionText) ||
            !TryVersion(downloadedVersionText, out var downloaded) ||
            downloaded.Major != expected.Major ||
            downloaded.Minor != expected.Minor ||
            downloaded.Build != expected.Build)
            throw new InvalidDataException(
                $"다운로드한 실행 파일 버전({downloadedVersionText ?? "확인불가"})이 Release 버전({expectedVersion})과 다릅니다.");
    }

    public void Dispose() => _httpClient.Dispose();

    private readonly record struct ReleaseInfo(Version Version, string VersionText, List<ReleaseAsset> Assets);
    private readonly record struct ReleaseAsset(string Name, string DownloadUrl);
}
