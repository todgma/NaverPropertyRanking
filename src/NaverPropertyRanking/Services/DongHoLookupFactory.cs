using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

/// <summary>저장된 CP 계정에 맞는 동·호 조회 통로를 만든다.</summary>
public static class DongHoLookupFactory
{
    /// <summary>동·호 조회를 지원하는 CP인지.</summary>
    public static bool Supports(string? cpValue) => cpValue is "1" or "2" or "3";

    /// <summary>
    /// 계정에 맞는 통로를 만든다. 지원하지 않는 CP나 비밀번호가 없는 계정이면 null이다.
    /// </summary>
    public static IDongHoLookup? Create(CpAccount account)
    {
        if (account.Password.Length == 0) return null;
        return account.CpValue switch
        {
            "1" => new RfineDongHoClient(account),
            "2" => new NeonetDongHoClient(account),
            "3" => new AipartnerDongHoClient(account),
            _ => null
        };
    }
}
