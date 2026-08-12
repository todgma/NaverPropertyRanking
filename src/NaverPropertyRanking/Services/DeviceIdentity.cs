using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace NaverPropertyRanking.Services;

public static class DeviceIdentity
{
    public static string GetStableId()
    {
        var source = GetWindowsMachineGuid();
        if (string.IsNullOrWhiteSpace(source))
            source = $"{Environment.MachineName}|{Environment.OSVersion.VersionString}";

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"NaverPropertyRanking|{source}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? GetWindowsMachineGuid()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid",
                null)?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
