using System.Text.Json;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public static class ApplicationConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppFileConfiguration Load(string? path = null)
    {
        var useEmbeddedFallback = path is null;
        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        try
        {
            if (File.Exists(path)) return Deserialize(File.ReadAllText(path));
            if (!useEmbeddedFallback) return new AppFileConfiguration();

            using var stream = typeof(ApplicationConfigurationLoader).Assembly
                .GetManifestResourceStream("NaverPropertyRanking.appsettings.json");
            if (stream is null) return new AppFileConfiguration();
            using var reader = new StreamReader(stream);
            return Deserialize(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new InvalidDataException($"appsettings.json을 읽을 수 없습니다: {ex.Message}", ex);
        }
    }

    private static AppFileConfiguration Deserialize(string json) =>
        JsonSerializer.Deserialize<AppFileConfiguration>(json, JsonOptions)
        ?? new AppFileConfiguration();
}
