using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.UI;

/// <summary>
/// 본 화면의 매물목록·랭킹이 갱신되면 알림을 받는 팝업.
/// 이때는 API를 다시 부르지 않고 본 화면이 이미 받아 둔 결과로만 화면을 맞춘다.
/// API 재조회는 사용자가 팝업의 새로고침을 눌렀을 때만 한다.
/// </summary>
public interface IReloadablePopup
{
    void OnListingsUpdated(IReadOnlyDictionary<string, RankingResult> rankingResults);
}
