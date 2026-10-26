using Squelch.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Squelch.Server.Services;

/// <summary>After the FatGuysSpeak -> Squelch rename the production database is copied to the new
/// Azure host. This rewrites stored upload links (avatars, message and DM attachments) from the old
/// host to the new one and renames the seeded server. Runs at every startup; idempotent.</summary>
public static class LegacyHostRewrite
{
    public const string OldHost = "fatguysspeak-server.azurewebsites.net";
    public const string NewHost = "squelch-server.azurewebsites.net";

    /// <summary>Swaps the host when the URL is http(s)://OldHost (case-insensitive) followed by end,
    /// '/', '?' or '#'. Anything else, including lookalike hosts, is returned unchanged.</summary>
    public static string? Rewrite(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (!url.StartsWith(scheme + OldHost, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = url[(scheme.Length + OldHost.Length)..];
            if (rest.Length > 0 && rest[0] is not ('/' or '?' or '#')) return url;
            return scheme + NewHost + rest;
        }
        return url;
    }

    public static async Task<int> RunAsync(AppDbContext db)
    {
        // Cheap SQL pre-filter on the host (LIKE is case-insensitive for ASCII on SQLite; Railway/
        // Postgres LIKE is case-sensitive, so mixed-case hosts there are still handled by Rewrite on
        // whatever the filter returns, and lowercase hosts are what the app ever stored).
        var needle = OldHost;
        var changed = 0;

        foreach (var u in await db.Users.Where(u => u.AvatarUrl != null && u.AvatarUrl.Contains(needle)).ToListAsync())
        {
            var n = Rewrite(u.AvatarUrl);
            if (n != u.AvatarUrl) { u.AvatarUrl = n; changed++; }
        }
        foreach (var m in await db.Messages.Where(m => m.AttachmentUrl != null && m.AttachmentUrl.Contains(needle)).ToListAsync())
        {
            var n = Rewrite(m.AttachmentUrl);
            if (n != m.AttachmentUrl) { m.AttachmentUrl = n; changed++; }
        }
        foreach (var d in await db.DirectMessages.Where(d => d.AttachmentUrl != null && d.AttachmentUrl.Contains(needle)).ToListAsync())
        {
            var n = Rewrite(d.AttachmentUrl);
            if (n != d.AttachmentUrl) { d.AttachmentUrl = n; changed++; }
        }
        foreach (var s in await db.Servers.Where(s => s.OwnerId == 0 && s.Name == "FatGuysSpeak").ToListAsync())
        {
            s.Name = "Squelch";
            changed++;
        }

        if (changed > 0) await db.SaveChangesAsync();
        return changed;
    }
}
