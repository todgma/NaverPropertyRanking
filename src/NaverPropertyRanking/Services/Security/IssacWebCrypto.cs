using System.Security.Cryptography;
using System.Text;

namespace NaverPropertyRanking.Services.Security;

/// <summary>
/// 이실장(aipartner) 로그인이 쓰는 ISSAC WebCrypto의 hybridEncrypt를 그대로 옮긴 것.
/// 사이트 자바스크립트와 같은 순서로 만든다.
///  1) 세션키 16바이트를 무작위로 만든다.
///  2) 서버 공개키로 세션키를 RSAES-OAEP(SHA-1) 암호화한다.
///  3) 본문을 그 세션키로 SEED-CBC 암호화한다(IV는 0으로 채운 16바이트).
///  4) 둘을 DER SEQUENCE로 묶어 base64로 만든다.
/// </summary>
public static class IssacWebCrypto
{
    /// <summary>사이트가 쓰는 고정 IV. 전부 0인 16바이트다.</summary>
    private static readonly byte[] ZeroIv = new byte[SeedCipher.BlockSize];

    /// <summary>
    /// 사이트의 issacweb_escape와 같은 규칙.
    /// 본문이 key=value&amp;key=value 형태라 구분자로 쓰이는 일곱 글자만 바꾼다.
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case ' ': builder.Append("%20"); break;
                case '%': builder.Append("%25"); break;
                case '&': builder.Append("%26"); break;
                case '+': builder.Append("%2B"); break;
                case '=': builder.Append("%3D"); break;
                case '?': builder.Append("%3F"); break;
                case '|': builder.Append("%7C"); break;
                default: builder.Append(ch); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>아이디·비밀번호·타임스탬프를 사이트가 기대하는 본문 한 줄로 만든다.</summary>
    public static string BuildLoginMessage(string userId, string password, string timeStamp) =>
        $"{Escape("id")}={Escape(userId)}" +
        $"&{Escape("pw")}={Escape(password)}" +
        $"&{Escape("timeStamp")}={Escape(timeStamp)}";

    /// <summary>
    /// 본문을 서버 공개키로 하이브리드 암호화해 issacwebData 값을 만든다.
    /// publicKeyBase64는 PKCS#1 RSAPublicKey를 base64로 담은 값이다.
    /// </summary>
    public static string HybridEncrypt(string message, string publicKeyBase64)
    {
        var sessionKey = RandomNumberGenerator.GetBytes(SeedCipher.KeySize);
        return HybridEncrypt(message, publicKeyBase64, sessionKey);
    }

    /// <summary>세션키를 직접 넣는 형태. 결과를 재현해 확인할 때 쓴다.</summary>
    public static string HybridEncrypt(string message, string publicKeyBase64, byte[] sessionKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(sessionKey);
        if (string.IsNullOrWhiteSpace(publicKeyBase64))
            throw new ArgumentException("공개키가 비어 있습니다.", nameof(publicKeyBase64));

        using var rsa = RSA.Create();
        rsa.ImportRSAPublicKey(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
        var encryptedKey = rsa.Encrypt(sessionKey, RSAEncryptionPadding.OaepSHA1);

        var encryptedBody = SeedCipher.EncryptCbc(Encoding.UTF8.GetBytes(message), sessionKey, ZeroIv);

        // 사이트는 암호화된 세션키를 부호 처리 없이 INTEGER로, 본문을 OCTET STRING으로 담는다.
        var der = DerSequence(DerValue(0x02, encryptedKey), DerValue(0x04, encryptedBody));
        return Convert.ToBase64String(der);
    }

    private static byte[] DerSequence(params byte[][] items)
    {
        var content = items.SelectMany(item => item).ToArray();
        return DerValue(0x30, content);
    }

    private static byte[] DerValue(byte tag, byte[] content)
    {
        var length = EncodeLength(content.Length);
        var value = new byte[1 + length.Length + content.Length];
        value[0] = tag;
        Array.Copy(length, 0, value, 1, length.Length);
        Array.Copy(content, 0, value, 1 + length.Length, content.Length);
        return value;
    }

    /// <summary>DER 길이 표기. 127바이트를 넘으면 길이의 바이트 수를 먼저 적는다.</summary>
    private static byte[] EncodeLength(int length)
    {
        if (length < 0x80) return [(byte)length];

        var bytes = new List<byte>();
        var remaining = length;
        while (remaining > 0)
        {
            bytes.Insert(0, (byte)(remaining & 0xFF));
            remaining >>= 8;
        }
        bytes.Insert(0, (byte)(0x80 | bytes.Count));
        return [.. bytes];
    }
}
