using System.Security.Cryptography;
using System.Text;

namespace PilahDok.Core.Services;

public static class SecureToken
{
    public static string Generate(int byteLength = 32)
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(byteLength));
    }

    public static string Hash(string token)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static bool Matches(string suppliedToken, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(suppliedToken))
        {
            return false;
        }

        var actualBytes = Convert.FromHexString(Hash(suppliedToken));
        var expectedBytes = Convert.FromHexString(expectedHash);
        return actualBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
