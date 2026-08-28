namespace NaverPropertyRanking.UI;

/// <summary>
/// 본 화면의 목록·랭킹이 갱신되면 스스로 데이터를 다시 조회하는 팝업.
/// 사용자가 새로고침을 누르지 않아도 최신 상태를 유지한다.
/// </summary>
public interface IReloadablePopup
{
    Task ReloadAsync();
}
