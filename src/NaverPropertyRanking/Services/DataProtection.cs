using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NaverPropertyRanking.Services;

internal static class DataProtection
{
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(ProtectOrUnprotect(bytes, protect: true));
    }

    public static string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(protectedText);
            return Encoding.UTF8.GetString(ProtectOrUnprotect(bytes, protect: false));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static byte[] ProtectOrUnprotect(byte[] input, bool protect)
    {
        var inBlob = new DataBlob();
        var outBlob = new DataBlob();
        try
        {
            inBlob.Data = Marshal.AllocHGlobal(input.Length);
            inBlob.Size = input.Length;
            Marshal.Copy(input, 0, inBlob.Data, input.Length);

            var ok = protect
                ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[outBlob.Size];
            Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
            return result;
        }
        finally
        {
            if (inBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.Data);
            if (outBlob.Data != IntPtr.Zero) LocalFree(outBlob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
