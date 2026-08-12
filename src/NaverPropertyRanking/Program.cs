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
                    "Naver 매물 랭킹 모니터가 이미 실행 중입니다.\n시스템 트레이를 확인해 주세요.",
                    "이미 실행 중",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var applicationConfiguration = ApplicationConfigurationLoader.Load();
            var store = new LocalStore();
            var settings = store.LoadSettings();
            AuthenticationSession? authenticationSession = null;
            GoogleAuthenticationClient? authenticationClient = null;
            try
            {
                if (applicationConfiguration.GoogleAuthentication.Enabled)
                {
                    authenticationClient = new GoogleAuthenticationClient(
                        applicationConfiguration.GoogleAuthentication);
                    using var loginForm = new LoginForm(authenticationClient, settings.LastLoginId);
                    if (loginForm.ShowDialog() != DialogResult.OK || loginForm.Session is null) return;

                    authenticationSession = loginForm.Session;
                    settings.LastLoginId = authenticationSession.UserId;
                    settings.LoginToken = authenticationSession.Token;
                    store.SaveSettings(settings);
                }
                CheckForUpdates(applicationConfiguration.Update);
                var credentialFingerprint = NaverAuthValidator.GetFingerprint(applicationConfiguration.Api);
                if (NaverAuthValidator.GetError(applicationConfiguration.Api) is null
                    && !string.Equals(settings.CredentialFingerprint, credentialFingerprint, StringComparison.Ordinal))
                {
                    settings.CredentialFingerprint = credentialFingerprint;
                    settings.RateLimitBlockedUntilUtc = null;
                    settings.RateLimitCooldownSource = string.Empty;
                }
                using var apiClient = new NaverLandClient(applicationConfiguration.Api);
                Application.Run(new MainForm(
                    store,
                    apiClient,
                    settings,
                    applicationConfiguration.Api,
                    authenticationSession,
                    authenticationClient,
                    applicationConfiguration.GoogleAuthentication));
            }
            finally
            {
                if (authenticationClient is not null && authenticationSession is not null)
                {
                    try
                    {
                        using var logoutTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        authenticationClient.LogoutAsync(authenticationSession, logoutTimeout.Token)
                            .GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // 비정상 종료는 서버의 heartbeat 만료 처리로 정리합니다.
                    }
                }
                authenticationClient?.Dispose();
            }
        }
    }

    private static void CheckForUpdates(UpdateConfiguration configuration)
    {
        if (!configuration.Enabled || !configuration.CheckOnStartup) return;
        using var updateService = new GitHubUpdateService(configuration);
        var result = updateService.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (!result.UpdateAvailable || string.IsNullOrWhiteSpace(result.DownloadUrl)) return;

        var choice = MessageBox.Show(
            $"새 버전 {result.LatestVersion}이 있습니다.\n현재 버전: {result.CurrentVersion}\n\n다운로드 페이지를 여시겠습니까?",
            "업데이트 알림",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);
        if (choice != DialogResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo(result.DownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"다운로드 페이지를 열 수 없습니다: {ex.Message}", "업데이트 오류",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
