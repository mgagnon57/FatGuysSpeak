using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FatGuysSpeak.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Services;

public class SessionBlacklistService
{
    private readonly ConcurrentDictionary<string, bool> _revoked = new();

    public void Revoke(string tokenHash) => _revoked[tokenHash] = true;

    public bool IsRevoked(string tokenHash) => _revoked.ContainsKey(tokenHash);

    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // Rehydrate from the database on startup so revocations survive server restarts
    public async Task RehydrateAsync(AppDbContext db)
    {
        var hashes = await db.UserSessions
            .Where(s => s.RevokedAt != null)
            .Select(s => s.TokenHash)
            .ToListAsync();
        foreach (var hash in hashes)
            _revoked[hash] = true;
    }
}
