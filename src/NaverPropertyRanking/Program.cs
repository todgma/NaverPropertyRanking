using System.Diagnostics;
using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;
using NaverPropertyRanking.UI;

namespace NaverPropertyRanking;

internal static class Program
{
    private const string SingleInstanceMutexName =
        @"Local\NaverPropertyRanking.4F639ED2-89F8-46C2-A099-31D631BE8B24";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var isFirstInstance = SingleInstanceGuard.TryAcquire(SingleInstanceMutexName, out var instanceGuard);
        using (instanceGuard)
        {
            if (!isFirstInstance)
            {
                MessageBox.Show(
                    "매물분석알림이 이미 실행 중입니다.\n시스템 트레이를 확인해 주세요.",
                    "이미 실행 중",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var applicationConfiguration = ApplicationConfigurationLoader.Load();
            var store = new LocalStore();
            var settings = store.LoadSettings();
            GoogleAuthenticationClient? authenticationClient = null;
            try
            {
                if (CheckForUpdates(applicationConfiguration.Update)) return;
                if (applicationConfiguration.GoogleAuthentication.Enabled)
                {
                    authenticationClient = new GoogleAuthenticationClient(
                        applicationConfiguration.GoogleAuthentication);
                }

                // 멤버십 종료 등으로 세션이 닫히면 프로그램을 끄지 않고 로그인 화면으로 돌아온다.
                while (true)
                {
                    AuthenticationSession? authenticationSession = null;
                    if (authenticationClient is not null)
                    {
                        using var loginForm = new LoginForm(authenticationClient, settings.LastLoginId);
                        if (loginForm.ShowDialog() != DialogResult.OK || loginForm.Session is null) return;

                        authenticationSession = loginForm.Session;
                        settings.LastLoginId = authenticationSession.UserId;
                        settings.LoginToken = authenticationSession.Token;
                        settings.Notices = authenticationSession.Notices.ToList();
                        store.SaveSettings(settings);

                        // 네이버 인증값은 앱에 넣지 않고 로그인 응답으로 받아 메모리에만 채운다.
                        if (!NaverCredentialApplier.Apply(
                                applicationConfiguration.Api,
                                loginForm.NaverCredentials))
                        {
                            MessageBox.Show(
                                "로그인 서버에서 네이버 인증값을 받지 못했습니다.\n" +
                                "Apps Script 스크립트 속성에 NAVER_AUTHORIZATION, NAVER_COOKIE를 설정하고\n" +
                                "새 버전으로 배포했는지 관리자에게 확인해 주세요.",
                                "네이버 인증값 없음",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }
                    }
                    var credentialFingerprint = NaverAuthValidator.GetFingerprint(applicationConfiguration.Api);
                    if (NaverAuthValidator.GetError(applicationConfiguration.Api) is null
                        && !string.Equals(settings.CredentialFingerprint, credentialFingerprint, StringComparison.Ordinal))
                    {
                        settings.CredentialFingerprint = credentialFingerprint;
                        settings.RateLimitBlockedUntilUtc = null;
                        settings.RateLimitCooldownSource = string.Empty;
                    }

                    bool returnToLogin;
                    using (var apiClient = new NaverLandClient(applicationConfiguration.Api))
                    {
                        var mainForm = new MainForm(
                            store,
                            apiClient,
                            settings,
                            applicationConfiguration.Api,
                            authenticationSession,
                            authenticationClient,
                            applicationConfiguration.Update.CurrentVersion);
                        Application.Run(mainForm);
                        returnToLogin = mainForm.ReturnToLogin;
                    }

                    Logout(authenticationClient, authenticationSession);
                    // 인증이 꺼져 있으면 돌아갈 로그인 화면이 없으므로 그대로 종료한다.
                    if (!returnToLogin || authenticationClient is null) return;
                }
            }
            finally
            {
                authenticationClient?.Dispose();
            }
        }
    }

    /// <summary>
    /// 서버 세션을 닫아 PC 자리를 즉시 반환한다.
    /// 실패해도 진행을 막지 않는다. 비정상 종료와 마찬가지로 서버가 알아서 정리한다.
    /// </summary>
    private static void Logout(
        GoogleAuthenticationClient? authenticationClient,
        AuthenticationSession? authenticationSession)
    {
        if (authenticationClient is null || authenticationSession is null) return;
        try
        {
            using var logoutTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            authenticationClient.LogoutAsync(authenticationSession, logoutTimeout.Token)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // 통신 실패는 무시한다. 서버 쪽 상태는 다음 로그인 때 덮어써진다.
        }
    }

    private static bool CheckForUpdates(UpdateConfiguration configuration)
    {
        if (!configuration.Enabled || !configuration.CheckOnStartup) return false;
        using var updateService = new GitHubUpdateService(configuration);
        var result = updateService.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (!result.UpdateAvailable) return false;
        if (string.IsNullOrWhiteSpace(result.DownloadUrl))
        {
            MessageBox.Show(
                result.Message,
                "업데이트 파일 없음",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        var choice = MessageBox.Show(
            $"새 버전 {result.LatestVersion}이 있습니다.\n현재 버전: {result.CurrentVersion}\n\n" +
            "새 실행 파일을 다운로드한 후 프로그램을 종료하고 자동으로 교체하시겠습니까?",
            "업데이트 알림",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);
        if (choice != DialogResult.Yes) return false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var downloadedPath = updateService.DownloadUpdateAsync(result, timeout.Token)
                .GetAwaiter().GetResult();
            UpdateInstaller.Start(downloadedPath, result.LatestVersion);
            MessageBox.Show(
                "업데이트 파일 다운로드가 완료되었습니다.\n프로그램을 종료한 후 새 버전으로 교체하고 자동 재실행합니다.",
                "업데이트 준비 완료",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"업데이트를 적용할 수 없습니다: {ex.Message}", "업데이트 오류",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }
}
