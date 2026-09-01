using System.Text.Json;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

/// <summary>
/// CP 계정을 실행 파일 위치의 파일에 저장하고 읽는다.
/// 비밀번호는 DPAPI로 보호해 저장하므로 파일을 다른 PC로 옮겨도 풀리지 않는다.
/// </summary>
public sealed class CpAccountStore
{
    private const string FileName = "cp-accounts.inf";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public CpAccountStore(string? directory = null) =>
        _path = Path.Combine(directory ?? AppContext.BaseDirectory, FileName);

    public string FilePath => _path;

    /// <summary>저장된 계정을 읽는다. 파일이 없거나 손상되면 빈 목록을 돌려준다.</summary>
    public List<CpAccount> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var accounts = JsonSerializer.Deserialize<List<CpAccount>>(File.ReadAllText(_path), JsonOptions)
                           ?? [];
            foreach (var account in accounts)
                account.Password = DataProtection.Unprotect(account.EncryptedPassword);
            return accounts
                .Where(account => !string.IsNullOrWhiteSpace(account.CpValue))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 계정을 추가하거나 갱신한다. CP당 한 계정만 유지하므로 같은 CP는 덮어쓴다.
    /// </summary>
    public List<CpAccount> Save(string cpValue, string userId, string password)
    {
        var accounts = Load();
        accounts.RemoveAll(account =>
            string.Equals(account.CpValue, cpValue, StringComparison.Ordinal));
        accounts.Add(new CpAccount
        {
            CpValue = cpValue,
            UserId = userId.Trim(),
            Password = password,
            EncryptedPassword = DataProtection.Protect(password),
            SavedAt = DateTime.Now
        });
        Write(accounts);
        return accounts;
    }

    public List<CpAccount> Remove(string cpValue)
    {
        var accounts = Load();
        accounts.RemoveAll(account =>
            string.Equals(account.CpValue, cpValue, StringComparison.Ordinal));
        Write(accounts);
        return accounts;
    }

    private void Write(List<CpAccount> accounts)
    {
        var ordered = accounts
            .OrderBy(account => account.CpValue, StringComparer.Ordinal)
            .ToList();
        File.WriteAllText(_path, JsonSerializer.Serialize(ordered, JsonOptions));
    }
}
