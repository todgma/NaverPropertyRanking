using System.Text.Json;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public sealed class LocalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly string _settingsPath;
    private readonly string _snapshotsPath;

    public LocalStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NaverPropertyRanking");
        _settingsPath = Path.Combine(_directory, "settings.json");
        _snapshotsPath = Path.Combine(_directory, "snapshots.json");
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                           ?? new AppSettings();
            settings.BearerToken = DataProtection.Unprotect(settings.EncryptedBearerToken);
            settings.CookieHeader = DataProtection.Unprotect(settings.EncryptedCookieHeader);
            settings.LoginToken = DataProtection.Unprotect(settings.EncryptedLoginToken);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(_directory);
        var persisted = settings.Clone();
        if (!persisted.SaveGroupId) persisted.GroupId = string.Empty;
        if (persisted.SaveCredentials)
        {
            persisted.EncryptedBearerToken = DataProtection.Protect(persisted.BearerToken);
            persisted.EncryptedCookieHeader = DataProtection.Protect(persisted.CookieHeader);
        }
        else
        {
            persisted.EncryptedBearerToken = string.Empty;
            persisted.EncryptedCookieHeader = string.Empty;
        }
        persisted.EncryptedLoginToken = DataProtection.Protect(persisted.LoginToken);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(persisted, JsonOptions));
    }

    public Dictionary<string, ListingSnapshot> LoadSnapshots()
    {
        try
        {
            if (!File.Exists(_snapshotsPath)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, ListingSnapshot>>(
                       File.ReadAllText(_snapshotsPath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveSnapshots(Dictionary<string, ListingSnapshot> snapshots)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_snapshotsPath, JsonSerializer.Serialize(snapshots, JsonOptions));
    }
}
