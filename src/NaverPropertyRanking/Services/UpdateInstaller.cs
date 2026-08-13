using System.Diagnostics;
using System.Text;

namespace NaverPropertyRanking.Services;

public static class UpdateInstaller
{
    public static void Start(string downloadedExecutablePath, string latestVersion)
    {
        var currentExecutablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutablePath) ||
            !string.Equals(Path.GetExtension(currentExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("현재 실행 파일 경로를 확인할 수 없습니다.");
        if (!File.Exists(downloadedExecutablePath))
            throw new FileNotFoundException("다운로드한 업데이트 파일이 없습니다.", downloadedExecutablePath);

        var scriptPath = Path.Combine(
            Path.GetDirectoryName(downloadedExecutablePath)!,
            $"apply-update-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, UpdaterScript, new UTF8Encoding(false));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(Process.GetCurrentProcess().Id.ToString());
        startInfo.ArgumentList.Add(downloadedExecutablePath);
        startInfo.ArgumentList.Add(currentExecutablePath);
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        startInfo.ArgumentList.Add(latestVersion);

        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("업데이트 적용 프로세스를 시작하지 못했습니다.");
    }

    private const string UpdaterScript = """
        param(
          [int]$ProcessId,
          [string]$SourcePath,
          [string]$TargetPath,
          [string]$SettingsPath,
          [string]$Version
        )
        $ErrorActionPreference = 'Stop'
        $backupPath = $TargetPath + '.update-backup'
        $replacementPath = $TargetPath + '.update-new'
        $logPath = Join-Path (Split-Path -Parent $TargetPath) 'update-error.log'
        try {
          Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
          Copy-Item -LiteralPath $SourcePath -Destination $replacementPath -Force
          if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Force }
          Move-Item -LiteralPath $TargetPath -Destination $backupPath -Force
          try {
            Move-Item -LiteralPath $replacementPath -Destination $TargetPath -Force
          } catch {
            Move-Item -LiteralPath $backupPath -Destination $TargetPath -Force
            throw
          }

          if (Test-Path -LiteralPath $SettingsPath) {
            try {
              $settings = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
              if ($null -ne $settings.Update) {
                $settings.Update.CurrentVersion = $Version
                $settings | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $SettingsPath -Encoding UTF8
              }
            } catch {
              Add-Content -LiteralPath $logPath -Value ('설정 버전 갱신 실패: ' + $_.Exception.Message) -ErrorAction SilentlyContinue
            }
          }

          Start-Process -FilePath $TargetPath -WorkingDirectory (Split-Path -Parent $TargetPath)
          Start-Sleep -Seconds 2
          if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Force }
          if (Test-Path -LiteralPath $SourcePath) { Remove-Item -LiteralPath $SourcePath -Force }
        } catch {
          Add-Content -LiteralPath $logPath -Value ((Get-Date).ToString('s') + ' 업데이트 실패: ' + $_.Exception.Message) -ErrorAction SilentlyContinue
          if (Test-Path -LiteralPath $backupPath) {
            if (Test-Path -LiteralPath $TargetPath) { Remove-Item -LiteralPath $TargetPath -Force -ErrorAction SilentlyContinue }
            Move-Item -LiteralPath $backupPath -Destination $TargetPath -Force
          }
          if (Test-Path -LiteralPath $TargetPath) {
            Start-Process -FilePath $TargetPath -WorkingDirectory (Split-Path -Parent $TargetPath)
          }
        } finally {
          Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        }
        """;
}
