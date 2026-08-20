namespace NaverPropertyRanking.Services;

public static class VerificationTypeFormatter
{
    public static string Format(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "DOC" => "구홍보",
        "NDOC1" or "NDOC2" => "신홍보",
        "MOBL" => "모바일V1",
        "OWNER" => "모바일V2",
        _ => "현장확인"
    };
}
