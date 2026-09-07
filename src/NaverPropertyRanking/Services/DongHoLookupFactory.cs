using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

/// <summary>저장된 CP 계정에 맞는 동·호 조회 통로를 만든다.</summary>
public static class DongHoLookupFactory
{
    /// <summary>동·호 조회를 지원하는 CP인지.</summary>
    public static bool Supports(string? cpValue) => cpValue is "1" or "2" or "3" or "4" or "5" or "6";

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
            "4" => new AsilDongHoClient(account),
            // 선방·우리집부동산은 같은 화면이라 통로 하나로 처리한다.
            "5" or "6" => new MemulCenterDongHoClient(account, MemulCenterSite.Find(account.CpValue)!),
            _ => null
        };
    }
}
