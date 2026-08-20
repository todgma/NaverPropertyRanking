namespace NaverPropertyRanking.Models;

public sealed class AppSettings
{
    public const int MinimumPollIntervalMinutes = 10;
    public const int MaximumPollIntervalMinutes = 1440;
    public const int DefaultPollIntervalMinutes = 30;

    public string GroupId { get; set; } = string.Empty;
    public bool SaveGroupId { get; set; } = true;
    public int PollIntervalMinutes { get; set; } = DefaultPollIntervalMinutes;
    public bool StartMinimized { get; set; }
    public bool AutoRefresh { get; set; } = true;
    public int DisplayPageSize { get; set; }
    public bool RankImmediatelyAfterListingLoad { get; set; } = true;

    public bool NotifyEveryRankChange { get; set; } = true;
    public bool NotifyRankThreshold { get; set; }
    public int RankThreshold { get; set; } = 5;
    public bool NotifyCompetitorPriceChange { get; set; } = true;
    public bool NotifyNewDuplicate { get; set; } = true;
    public bool PopupNotificationsEnabled { get; set; }
    public bool PropertyAnalysisEnabled { get; set; }
    public List<string> GridColumnOrder { get; set; } = [];

    public bool SaveCredentials { get; set; }
    public string EncryptedBearerToken { get; set; } = string.Empty;
    public string EncryptedCookieHeader { get; set; } = string.Empty;
    public string ManualArticleNumbers { get; set; } = string.Empty;
    public DateTime? RateLimitBlockedUntilUtc { get; set; }
    public string RateLimitCooldownSource { get; set; } = string.Empty;
    public string CredentialFingerprint { get; set; } = string.Empty;
    public string LastLoginId { get; set; } = string.Empty;
    public string EncryptedLoginToken { get; set; } = string.Empty;
    public List<string> Notices { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string BearerToken { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string CookieHeader { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string LoginToken { get; set; } = string.Empty;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public static int NormalizePollInterval(int minutes) =>
        Math.Clamp(minutes, MinimumPollIntervalMinutes, MaximumPollIntervalMinutes);
}
