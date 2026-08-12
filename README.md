# Naver 매물 랭킹 모니터

네이버페이 부동산의 동일매물 노출 순서를 주기적으로 확인하고 화면 중앙에 통합 알림을 표시하는 .NET 8 WinForms 앱입니다.

## 주요 기능

- 사용자 ID를 `realtorId`로 전달해 내 매물 목록 조회
- 주소/매물명, 거래유형, 거래금액 표시
- 매물별 이전랭킹·현재랭킹 및 동일매물 수 표시
- 랭킹 상승은 현재랭킹에 빨간색 `↑변동폭`, 하락은 파란색 `↓변동폭` 표시
- `▶` 버튼으로 동일매물과 공인중개사/정보제공사 펼치기
- 모든 랭킹 변경, 설정한 순위 숫자 이상으로 하락, 타 중개사 가격 변경, 단독매물에 경쟁 매물 생성 내용을 조회당 한 개의 중앙 팝업으로 통합 알림
- 메인 창이 시스템 트레이에 숨겨져 있어도 랭킹 조회 완료 팝업 표시
- 프로그램 중복 실행 차단 및 이미 실행 중일 때 안내 메시지 표시
- Google Apps Script·스프레드시트 기반 로그인 및 회원가입
- 멤버십 기간과 고유 PC 토큰 수를 이용한 로그인 제한
- 로그인 성공 시 토큰·로그인일시·아이디·공인 IP 이력 기록
- GitHub Releases 최신 버전 확인 및 태그 기반 자동 게시
- 창을 닫아도 시스템 트레이에서 자동 조회
- 건물과 상승 랭킹을 조합한 전용 실행 파일·창·시스템 트레이 아이콘
- 사용자 ID 저장 선택(로컬 사용자 설정에만 저장)
- 매물목록 전체를 한 화면에 표시하고 목록 조회 직후 전체 랭킹 조회
- 체크박스로 일부 매물을 선택해 선택 항목만 랭킹 재조회(미선택·전체선택은 전체 목록 재조회)
- 매물을 체크한 상태로 창을 닫으면 선택 대상을 트레이 백그라운드에서 재조회
- 조회 중 반투명 차광막 위 중앙 모달 진행률 표시와 화면 입력 잠금
- HTTP 429 발생 시 최소 30분 자동 쿨다운 및 반복 팝업 방지

## 실행

Windows 10/11과 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)이 필요합니다.

```powershell
dotnet run --project .\src\NaverPropertyRanking\NaverPropertyRanking.csproj
```

Google 인증을 활성화한 경우 앱 실행 직후 로그인 화면이 먼저 표시됩니다. 로그인 화면의 `회원가입` 탭에서 아이디·패스워드·이름만 입력해 가입할 수 있습니다.

아이디와 패스워드는 각각 최소 4자이며, 패스워드는 최대 100자까지 입력할 수 있습니다.

1. 로그인 후 상단에 네이버 부동산 사용자 ID를 입력합니다.
2. 필요하면 `저장`을 체크합니다.
3. `appsettings.json`의 `RealtorArticleList`와 `Ranking`에 각각 해당 API의 Bearer 토큰과 Cookie를 설정합니다.
4. `내 매물 불러오기`를 누르면 조회 및 알림 설정창이 열립니다.
5. 설정을 확인한 뒤 `확인`을 누르면 메인 창이 시스템 트레이로 숨고, 백그라운드에서 매물 전체 목록과 전체 랭킹을 조회합니다.
6. 조회가 끝나면 성공·실패·변동 건수를 포함한 통합 팝업이 화면 중앙에 표시됩니다. `확인`은 팝업만 닫고, `시스템 열기`는 메인 창을 복원합니다.
7. 표의 `▶`를 눌러 동일매물을 확인합니다. 행을 두 번 클릭하면 API에서 받은 `complexNo`와 매물번호를 사용한 네이버 단지 매물 페이지가 열립니다.

매물번호 입력, 표시 행수, `매물 조회 시 랭킹 바로조회`, `랭킹 새로고침` 컨트롤은 메인 화면에 표시하지 않습니다. 표시 행수는 항상 `전체`, 목록 조회 후 랭킹 조회는 항상 사용됩니다. 수동 재조회는 목록의 선택 체크박스 또는 시스템 트레이의 `지금 새로고침`을 이용합니다.

각 내 매물 행의 체크박스로 랭킹 재조회 대상을 선택할 수 있습니다. 체크박스 열 머리글의 `전체`를 누르면 전체 페이지의 매물을 선택하거나 해제합니다. 일부만 선택하면 선택된 매물만 다시 조회하고, 선택된 매물이 없거나 전체 매물이 선택된 경우에는 전체 목록을 다시 조회합니다. 조회 중에는 매물 목록과 상태가 반투명 차광막 뒤로 보이고 화면 중앙에는 모달 진행률 창이 표시됩니다. 완료될 때까지 뒤쪽 화면 조작은 차단됩니다.

하나 이상의 매물을 체크한 상태로 창을 닫으면 창은 시스템 트레이로 숨고 현재 체크 대상을 즉시 백그라운드 재조회합니다. 백그라운드 조회 중 창을 다시 열면 현재 진행률 모달이 표시됩니다.

## appsettings.json

실행 파일과 같은 폴더의 `appsettings.json`에서 서로 독립적인 API 프로필을 불러옵니다.

- `Api.BaseUrl`: 네이버 부동산 API 기준 주소
- `Api.RealtorArticleList`: 참고 프로젝트의 첫 번째 `중개사 매물목록` 탭에 해당하는 설정
  - `Endpoint`, `RealtorIdParameter`: 목록 엔드포인트와 사용자 ID 쿼리 이름(현재 `realtorId`)
  - `Headers`: 중개사 매물목록 API 전용 Authorization, Referer, Cookie, User-Agent 등
  - `Params`: realEstateType, tradeType, order, page, zoom
- `Api.Ranking`: 매물번호별 동일매물 랭킹 조회 전용 설정
  - `Endpoint`, `Headers`, `Params.index`
- `GoogleAuthentication`: Google Apps Script 인증 API 설정
  - `Enabled`: 로그인 기능 활성화 여부
  - `WebAppUrl`: Apps Script 웹 앱의 `/exec` 주소
  - `PublicIpEndpoint`: 로그인 이력에 기록할 공인 IP 조회 주소
  - `RequestTimeoutSeconds`: 인증 요청 제한 시간
  - `HeartbeatIntervalSeconds`: 활성 접속 신호 주기. 기본 120초
- `Update`: GitHub Releases 버전 확인 설정
  - `Enabled`, `CheckOnStartup`: 업데이트 확인 활성화 여부
  - `CurrentVersion`: 현재 배포 버전
  - `LatestReleaseApiUrl`: `https://api.github.com/repos/OWNER/REPOSITORY/releases/latest` 형식
  - `ReleasesPageUrl`, `AssetName`: 대체 릴리스 페이지와 ZIP 파일명

`UserId`는 `appsettings.json`에 두지 않습니다. 메인 화면에서 입력하며 `저장`을 선택한 경우에만 로컬 사용자 설정에 보관됩니다. 앱 시작 시 자동으로 매물을 조회하지 않습니다. 저장된 사용자 ID가 있더라도 `내 매물 불러오기`를 누르고 설정을 확인해야 `realtorId={사용자 ID}`로 중개사 매물목록을 조회합니다. 반환된 각 네이버 매물번호를 기준으로 별도의 랭킹 프로필을 사용해 순위를 조회합니다. 안전한 형식 예시는 `appsettings.example.json`을 참고하세요.

실제 `appsettings.json`에는 로그인 Cookie가 포함될 수 있으므로 `.gitignore`에서 제외했습니다. 파일을 공유하거나 저장소에 커밋하지 마세요.

Bearer 토큰이나 Cookie가 없으면 앱은 해당 프로필로 네이버에 요청을 보내지 않습니다. JWT의 `exp` 값만으로는 요청을 사전 차단하지 않으며, 인증 가능 여부는 네이버 서버의 실제 응답으로 판단합니다. `appsettings.json`에서 두 프로필의 인증값을 교체하면 잘못된 인증 요청으로 만들어진 로컬 429 대기는 한 번 초기화됩니다.

### 로그인 및 Google 스프레드시트 설정

Google 인증 서버 코드는 [google-apps-script](google-apps-script) 폴더에 있습니다. [설정 안내](google-apps-script/README.md)에 따라 스프레드시트와 Apps Script 웹 앱을 배포한 뒤 다음 값을 변경합니다.

```json
"GoogleAuthentication": {
  "Enabled": true,
  "WebAppUrl": "https://script.google.com/macros/s/배포ID/exec",
  "PublicIpEndpoint": "https://api.ipify.org",
  "RequestTimeoutSeconds": 20
}
```

`configure()`를 최초 한 번 실행하면 다음 탭이 자동 생성됩니다.

- `회원정보`: 아이디, 비밀번호해시, 솔트, 이름, 가입일시, 멤버십 시작일자, 멤버십 종료일자, 사용가능PC수
- `이력`: 토큰, 로그인일시, 아이디, 아이피
- `접속현황`: 세션ID, 토큰, 아이디, PC명, 로그인일시, 마지막신호일시, 종료일시, 상태

패스워드는 스프레드시트에 평문으로 저장하지 않습니다. PC 토큰은 Apps Script의 서버 비밀값과 Windows PC 식별값으로 생성됩니다. `접속현황`에서 heartbeat가 유효한 `ACTIVE` 상태의 고유 토큰 수가 `사용가능PC수`를 초과하면 로그인을 거부합니다. 앱은 기본 2분마다 접속 신호를 보내고, 정상 종료하면 즉시 `LOGOUT`, 강제 종료·정전·네트워크 단절이면 기본 5분 뒤 `EXPIRED` 상태로 처리됩니다. `이력` 탭은 감사 기록일 뿐 PC 제한 계산에는 사용하지 않습니다. 공인 IP는 `PublicIpEndpoint`에서 클라이언트가 확인한 값이므로 보안 감사용 절대 증거로 사용하면 안 됩니다.

회원정보의 멤버십 기간이나 사용가능PC수를 변경하면 다음 heartbeat에서 다시 검사합니다. 멤버십 종료 또는 PC 수 축소로 현재 세션이 허용 범위를 벗어나면 앱에 안내를 표시하고 종료합니다.

신규 회원의 멤버십은 가입일 00:00:00에 시작하고 8일 뒤 00:00:00에 종료됩니다. 종료 시각은 사용 기간에 포함되지 않습니다.

로그인 기능을 배포하기 전에는 `Enabled`를 `false`로 유지하면 기존처럼 로그인 화면 없이 실행할 수 있습니다.

### GitHub 버전 관리 및 자동 게시

[release.yml](.github/workflows/release.yml)은 `v1.2.0` 같은 태그가 푸시되면 다음 작업을 자동 실행합니다.

1. .NET 8 복원·빌드·스모크 테스트
2. Windows x64 단일 EXE 게시
3. 배포용 `appsettings.example.json`의 현재 버전을 태그 버전으로 변경
4. EXE와 외부 설정을 `NaverPropertyRanking.zip`으로 압축
5. GitHub Release 생성 및 ZIP 업로드

```powershell
git tag v1.0.1
git push origin v1.0.1
```

앱에서 업데이트 확인을 사용하려면 `Update.Enabled`를 `true`로 바꾸고 GitHub 저장소 주소를 입력합니다. 새 버전이 있으면 앱 시작 시 다운로드 여부를 묻고 GitHub Release 자산을 엽니다. 실행 중인 EXE를 강제로 덮어쓰지는 않습니다.

## 알림 기준

- `모든 랭킹 변경`: 이전 조회와 순위가 다르면 알림
- `랭킹 기준`: 예를 들어 기준이 5이면 1~4위에서 5위 이상 숫자로 내려간 순간 알림
- `동일매물 가격 변경`: 내 매물을 제외한 동일매물의 표시 가격이 바뀌면 알림
- `단독매물 상태 변경`: 경쟁 동일매물 0건에서 1건 이상으로 바뀌면 알림

최초 조회는 비교 기준을 저장하므로 변경 항목은 만들지 않습니다. 다만 조회 완료를 알리는 중앙 팝업은 표시되며 `변동 내역이 없습니다.`라고 안내합니다. 이후 조회부터 변동 항목을 유형별로 묶어 한 개의 팝업에 표시합니다.

## 빌드와 점검

```powershell
dotnet build .\NaverPropertyRanking.sln -nologo
dotnet run --project .\tests\NaverPropertyRanking.SmokeTests\NaverPropertyRanking.SmokeTests.csproj
```

Visual Studio 게시 화면에서 `SingleFile-win-x64` 프로필을 선택하면 .NET Runtime을 포함한 Windows x64용 실행 파일이 `D:\Source\Release\NaverPropertyRanking`에 생성됩니다. 명령줄에서는 다음과 같이 게시할 수 있습니다.

```powershell
dotnet publish .\src\NaverPropertyRanking\NaverPropertyRanking.csproj -p:PublishProfile=SingleFile-win-x64
```

단일 파일 게시 프로필은 `NaverPropertyRanking.exe`와 외부 `appsettings.json`을 함께 생성합니다. 실행 파일 옆의 외부 설정이 EXE 내부 설정보다 우선하므로 인증값을 변경한 뒤 다시 게시할 필요 없이 앱만 재시작하면 됩니다. 외부 파일을 삭제하면 EXE 내부에 포함된 기본 설정을 사용합니다.

## 주의사항

이 앱은 네이버가 개발자용으로 공개·보증한 API가 아니라 현재 웹 화면이 사용하는 읽기 전용 엔드포인트에 의존합니다. 응답 형식이나 인증 방식이 예고 없이 바뀔 수 있습니다. 네이버 이용약관과 관련 법규를 확인하고 본인이 관리할 권한이 있는 매물만 낮은 빈도로 조회하세요.

앱은 401/403/429에서 자동 우회하거나 반복 재시도하지 않습니다. 429가 발생하면 서버의 `Retry-After` 또는 기본 30분 동안 모든 요청을 중지하며, 이 시각은 앱을 재시작해도 유지됩니다. 제한이 풀린 뒤에도 반복된다면 조회 주기를 늘리고 동시에 실행 중인 다른 크롤러를 중지하세요. 기본 조회 간격은 10분이며 최소값은 2분입니다.

각 네이버 요청은 최소 3초 간격으로 순차 실행됩니다. 이미 429가 표시된 상태에서는 앱을 반복 실행하거나 새로고침하지 말고 상태 표시줄에 안내된 시각까지 기다려야 합니다. 토큰이나 IP를 바꿔 제한을 우회하는 동작은 구현하지 않습니다.

Google Sheets와 Apps Script 기반 인증은 소규모 내부 도구 수준입니다. 데스크톱 프로그램과 외부 `appsettings.json`에 들어간 값은 사용자가 추출하거나 변경할 수 있으므로 결제, 민감한 개인정보 또는 강한 라이선스 보호가 필요한 서비스에는 별도 백엔드와 전문 인증 저장소를 사용해야 합니다.
