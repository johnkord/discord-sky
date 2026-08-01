using System.Security.Cryptography;
using System.Text;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Bot.Memory;

internal static class MemoryIdentity
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    public static string FromOperation(string operationId, ulong userId, int ordinal)
    {
        var material = Encoding.UTF8.GetBytes($"memory:{operationId}:{userId}:{ordinal}");
        return Convert.ToHexString(SHA256.HashData(material).AsSpan(0, 16)).ToLowerInvariant();
    }

    public static bool NormalizeInPlace(List<UserMemory> memories)
    {
        var changed = false;
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < memories.Count; index++)
        {
            var memory = memories[index];
            var memoryId = memory.MemoryId?.Trim();
            if (string.IsNullOrWhiteSpace(memoryId) || !used.Add(memoryId))
            {
                do memoryId = NewId(); while (!used.Add(memoryId));
                memories[index] = memory with { MemoryId = memoryId };
                changed = true;
            }
            else if (!string.Equals(memoryId, memory.MemoryId, StringComparison.Ordinal))
            {
                memories[index] = memory with { MemoryId = memoryId };
                changed = true;
            }
        }
        return changed;
    }

    public static List<UserMemory> NormalizeCopy(IReadOnlyList<UserMemory> memories)
    {
        var copy = memories.ToList();
        NormalizeInPlace(copy);
        return copy;
    }
}