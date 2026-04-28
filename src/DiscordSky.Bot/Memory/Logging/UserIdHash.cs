using System.Security.Cryptography;
using System.Text;

namespace DiscordSky.Bot.Memory.Logging;

/// <summary>
/// Obfuscates Discord user IDs in aggregated logs. Not cryptographic anonymisation —
/// the purpose is just to keep raw IDs out of log sinks while keeping per-user tracing possible.
/// </summary>
public static class UserIdHash
{
    public static string Hash(ulong userId)
    {
        Span<byte> buf = stackalloc byte[8];
        BitConverter.TryWriteBytes(buf, userId);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(buf, digest);
        var sb = new StringBuilder(10);
        for (int i = 0; i < 5; i++)
        {
            sb.Append(digest[i].ToString("x2"));
        }
        return sb.ToString();
    }
}
