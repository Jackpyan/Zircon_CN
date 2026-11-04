using System.Security.Cryptography;
using System.Text;

namespace Server.Web.Services;

/// <summary>
/// TOTP 纯算法核心（RFC 6238，HMAC-SHA1 / 30 秒 / 6 位 / 允许前后各 1 步偏差）。
/// 无任何外部依赖，便于单独验证（RFC 测试向量）。
/// </summary>
public static class TotpCore
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int Skew = 1;
    public const int SecretBytes = 20;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>生成 20 字节随机密钥并编码为 Base32（无填充）。</summary>
    public static string CreateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>按时间戳计算 6 位验证码。</summary>
    public static string ComputeCode(string secret, DateTimeOffset timestamp)
        => Compute(secret, timestamp.ToUnixTimeSeconds() / StepSeconds);

    /// <summary>校验验证码（默认允许前后各 1 步 = ±30 秒）。</summary>
    public static bool VerifyCode(string secret, string? code, DateTimeOffset? timestamp = null, int skew = Skew)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(code)) return false;

        string trimmed = code.Trim().Replace(" ", string.Empty);

        if (trimmed.Length != Digits || !trimmed.All(char.IsDigit)) return false;

        long counter = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / StepSeconds;

        for (int offset = -skew; offset <= skew; offset++)
        {
            string expected = Compute(secret, counter + offset);

            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(trimmed)))
                return true;
        }

        return false;
    }

    public static string BuildUri(string secret, string issuer = "Zircon", string account = "admin")
        => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
           $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    private static string Compute(string secret, long counter)
    {
        byte[] key = Base32Decode(secret);
        byte[] counterBytes = BitConverter.GetBytes(counter);

        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        using HMACSHA1 hmac = new(key);
        byte[] hash = hmac.ComputeHash(counterBytes);

        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6");
    }

    public static string Base32Encode(byte[] data)
    {
        StringBuilder builder = new((data.Length + 4) / 5 * 8);
        int buffer = 0, bitsLeft = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                builder.Append(Base32Alphabet[(buffer >> (bitsLeft - 5)) & 0x1F]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
            builder.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);

        return builder.ToString();
    }

    public static byte[] Base32Decode(string input)
    {
        string clean = (input ?? string.Empty).Trim().Replace(" ", string.Empty).TrimEnd('=').ToUpperInvariant();
        List<byte> output = new(clean.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;

        foreach (char c in clean)
        {
            int value = Base32Alphabet.IndexOf(c);
            if (value < 0) continue;

            buffer = (buffer << 5) | value;
            bitsLeft += 5;

            if (bitsLeft < 8) continue;

            output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
            bitsLeft -= 8;
        }

        return [.. output];
    }
}
