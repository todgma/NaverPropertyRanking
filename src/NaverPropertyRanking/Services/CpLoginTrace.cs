namespace NaverPropertyRanking.Services;

/// <summary>
/// CP 로그인이 어느 단계에서 어긋나는지 남기는 기록장.
/// 아이디·비밀번호·보안 토큰 같은 값은 남기지 않고, 단계와 주소만 적는다.
///
/// 조회하는 동안 메모리에 모아 두었다가 실패했을 때만 파일로 남긴다.
/// 정상 동작할 때 파일이 쌓이지 않게 하면서, 문제가 생기면 바로 확인할 수 있게 하기 위해서다.
/// </summary>
public static class CpLoginTrace
{
    private static readonly object Gate = new();
    private static readonly List<string> Lines = [];

    /// <summary>켜져 있을 때만 기록을 모은다.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>실행 파일과 같은 폴더에 남긴다.</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "cp-login-trace.log");

    /// <summary>기록을 새로 시작한다.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            Lines.Clear();
            Lines.Add($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 동·호 조회 기록 ===");
            Enabled = true;
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (!Enabled) return;
            Lines.Add($"{DateTime.Now:HH:mm:ss.fff}  {message}");
        }
    }

    /// <summary>
    /// 기록을 마친다. 실패했을 때만 파일로 남기고, 잘 끝났으면 지난 기록을 지운다.
    /// </summary>
    public static void Stop(bool keep)
    {
        string[] snapshot;
        lock (Gate)
        {
            Enabled = false;
            snapshot = [.. Lines];
            Lines.Clear();
        }

        try
        {
            if (keep && snapshot.Length > 0)
                File.WriteAllLines(FilePath, snapshot);
            else if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 기록은 부가 기능이라 실패해도 조회 결과에 영향을 주지 않는다.
        }
    }
}
