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
    private readonly string _listingCachePath;

    public LocalStore(string? directory = null, string? listingCacheDirectory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NaverPropertyRanking");
        _settingsPath = Path.Combine(_directory, "settings.json");
        _snapshotsPath = Path.Combine(_directory, "snapshots.json");
        var cacheDirectory = listingCacheDirectory ??
                             (directory is null ? AppContext.BaseDirectory : directory);
        _listingCachePath = Path.Combine(cacheDirectory, "listing-cache.inf");
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
            settings.Notices ??= [];
            settings.GridColumnOrder ??= [];
            settings.PollIntervalMinutes = AppSettings.NormalizePollInterval(settings.PollIntervalMinutes);
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
        persisted.PollIntervalMinutes = AppSettings.NormalizePollInterval(persisted.PollIntervalMinutes);
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

    public ListingCacheEntry? LoadListingCache(string loginId, string groupId)
    {
        if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(groupId)) return null;
        return LoadListingCaches().FirstOrDefault(entry =>
            string.Equals(entry.LoginId, loginId.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.GroupId, groupId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public void SaveListingCache(ListingCacheEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.LoginId) || string.IsNullOrWhiteSpace(entry.GroupId)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(_listingCachePath)!);
        var entries = LoadListingCaches();
        entries.RemoveAll(existing =>
            string.Equals(existing.LoginId, entry.LoginId.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.GroupId, entry.GroupId.Trim(), StringComparison.OrdinalIgnoreCase));
        entries.Add(entry with
        {
            LoginId = entry.LoginId.Trim(),
            GroupId = entry.GroupId.Trim(),
            Listings = entry.Listings.ToList(),
            RankingResults = entry.RankingResults.ToList()
        });

        SaveListingCaches(entries);
    }

    public void RemoveListingCache(string loginId, string groupId)
    {
        if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(groupId)) return;
        var entries = LoadListingCaches();
        var removed = entries.RemoveAll(existing =>
            string.Equals(existing.LoginId, loginId.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.GroupId, groupId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        if (entries.Count == 0)
        {
            if (File.Exists(_listingCachePath)) File.Delete(_listingCachePath);
            return;
        }

        SaveListingCaches(entries);
    }

    private void SaveListingCaches(IReadOnlyList<ListingCacheEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_listingCachePath)!);
        var temporaryPath = _listingCachePath + ".tmp";
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(temporaryPath, DataProtection.Protect(json));
        File.Move(temporaryPath, _listingCachePath, true);
    }

    private List<ListingCacheEntry> LoadListingCaches()
    {
        try
        {
            if (!File.Exists(_listingCachePath)) return [];
            var json = DataProtection.Unprotect(File.ReadAllText(_listingCachePath));
            if (string.IsNullOrWhiteSpace(json)) return [];
            return JsonSerializer.Deserialize<List<ListingCacheEntry>>(
                       json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
