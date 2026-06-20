using System.Net.Http.Json;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Services;

public class BotService(IHttpClientFactory httpFactory, IConfiguration config, IServiceScopeFactory scopeFactory, IHubContext<ChatHub> hub)
{
    public const string BotUsername = "PorkChop";
    public static int BotUserId { get; set; }

    private readonly string _apiKey = config["Anthropic:ApiKey"] ?? "";
    private readonly string _model  = config["Anthropic:Model"]  ?? "claude-haiku-4-5-20251001";

    private readonly bool _idleNudges    = config.GetValue("PorkChop:IdleNudges", true);
    // Join announcements: on/off toggle, plus a per-user cooldown so quick reconnects don't spam.
    private readonly bool _announceJoins = config.GetValue("PorkChop:AnnounceJoins", true);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _lastJoinAnnounce = new();
    private static readonly TimeSpan JoinCooldown = TimeSpan.FromMinutes(30);

    public async Task RespondAsync(int channelId, int serverId, string triggerContent)
    {
        if (string.IsNullOrEmpty(_apiKey) || BotUserId == 0)
        {
            Console.WriteLine($"[PorkChop] not responding — Anthropic API key set: {!string.IsNullOrEmpty(_apiKey)}, bot user id: {BotUserId}. Set Anthropic:ApiKey and restart to enable replies.");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Include PorkChop's own recent replies (Source == AI) so a back-and-forth stays on topic —
        // it can see what it just said instead of starting each follow-up half-blind.
        var recent = await db.Messages
            .Where(m => m.ChannelId == channelId && !m.IsDeleted)
            .Include(m => m.Author)
            .OrderByDescending(m => m.CreatedAt)
            .Take(8)
            .ToListAsync();
        recent.Reverse();

        var contextLines = recent.Select(m => $"{m.Author.Username}: {m.Content}").ToList();
        // Give PorkChop any images shared in the recent context so it can answer about them.
        var env = scope.ServiceProvider.GetService<IHostEnvironment>();
        var images = env is null ? [] : ReadRecentImages(recent, env.ContentRootPath);
        var reply = await CallClaudeAsync(triggerContent, contextLines, images);
        if (reply is null) { Console.WriteLine("[PorkChop] no reply produced (see any Anthropic error above)."); return; }

        var msg = new Message
        {
            Content   = reply,
            AuthorId  = BotUserId,
            ChannelId = channelId,
            Source    = MessageSource.AI,
        };
        db.Messages.Add(msg);
        await db.SaveChangesAsync();

        var dto = new MessageDto(msg.Id, reply, BotUsername, BotUserId, msg.CreatedAt, channelId, MessageSource.AI);
        await hub.Clients.Group($"channel-{channelId}").SendAsync("ReceiveMessage", dto);
        await hub.Clients.Group($"server-{serverId}").SendAsync("NewMessageNotification", dto);
    }

    private const string JoinAnnounceSystem = "You are PorkChop, the foul-mouthed mascot of a PRIVATE, friends-only chat app called FatGuysSpeak, announcing to the channel that one of the crew just (re)joined. These are close friends who roast the absolute hell out of each other, so go HARD — don't hold back. Write one to three sentences of raunchy, profane, R-rated (X-rated if it lands) welcome that completely busts their balls. Swear freely, get filthy, lean all the way into crude fat-guy humor and dirty sex jokes — nothing's too lowbrow. Use their name, tear into their profile bio, and if you're given a sample of their past messages, dig into it for their actual interests, opinions and likes/dislikes and roast THOSE specifically — make it clear you know exactly who this degenerate is. Address the channel about them, not the person directly — e.g. \"look what the fuck just waddled back in...\". It's savage but it's love between buddies, so keep the ammo about THEM — their habits, their bio, their general degeneracy — not hateful identity-based slurs. No hashtags, and don't wrap the whole thing in quotation marks.";

    private const string IdleNudgeSystem = "You are PorkChop, the foul-mouthed mascot of a PRIVATE, friends-only chat app called FatGuysSpeak. A bunch of the crew are sitting in a voice channel together but nobody's said a goddamn word in minutes — total dead air. Bust their balls about it: one or two raunchy, profane, R-to-X-rated sentences calling them out for sitting there silent (probably just stuffing their faces, scrolling their phones, or jerking around). Use their names. Same savage-but-loving tone friends bust each other's chops with — crude as hell, but it's love, not hateful identity slurs. No hashtags, and don't wrap the whole thing in quotation marks. This line will be both posted as text AND spoken out loud, so make it land when read OR heard.";

    /// <summary>Generates a raunchy "you're all sitting in voice saying nothing" roast, personalized
    /// to exactly who's in the channel and what PorkChop has learned about each of them from their own
    /// chat, posts it, and returns the line (so it can also be spoken). No-op when disabled, keyless,
    /// or the bot isn't set up.</summary>
    public async Task<string?> GenerateAndPostIdleNudgeAsync(int channelId, IReadOnlyList<int> userIds)
    {
        if (!_idleNudges || string.IsNullOrEmpty(_apiKey) || BotUserId == 0) return null;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var channel = await db.Channels.FindAsync(channelId);
        if (channel is null) return null;

        // Build a per-person dossier from each present user's own recent text + voice messages, so the
        // roast is specific to who's actually here and what they're into — different crowd, different jokes.
        var dossiers = new List<string>();
        foreach (var uid in userIds.Take(8))
        {
            var u = await db.Users.FindAsync(uid);
            if (u is null) continue;
            var history = await db.Messages
                .Where(m => m.AuthorId == uid && !m.IsDeleted
                            && (m.Source == MessageSource.Text || m.Source == MessageSource.Voice))
                .OrderByDescending(m => m.CreatedAt).Take(20)
                .Select(m => m.Content).ToListAsync();
            history.Reverse();
            var bio = string.IsNullOrWhiteSpace(u.Bio) ? "" : $"\n  bio: {u.Bio}";
            var sample = history.Count > 0 ? "\n  recent messages: " + string.Join(" | ", history) : "\n  (not much on record yet)";
            dossiers.Add($"- {u.Username}{bio}{sample}");
        }
        if (dossiers.Count == 0) return null;

        var prompt = "These degenerates are sitting in the voice channel together right now and nobody's said a word. "
            + "Here's who's here and what each of them is into (from their own chat), so make the roast personal to THIS crowd:\n\n"
            + string.Join("\n", dossiers)
            + "\n\nRoast them for sitting there silent — aim it at who's actually present and the shit they're into.";
        var text = await PostToClaudeAsync(IdleNudgeSystem, prompt);
        if (text is null) return null;

        var msg = new Message { Content = text, AuthorId = BotUserId, ChannelId = channelId, Source = MessageSource.AI };
        db.Messages.Add(msg);
        await db.SaveChangesAsync();

        var dto = new MessageDto(msg.Id, text, BotUsername, BotUserId, msg.CreatedAt, channelId, MessageSource.AI);
        await hub.Clients.Group($"channel-{channelId}").SendAsync("ReceiveMessage", dto);
        await hub.Clients.Group($"server-{channel.ServerId}").SendAsync("NewMessageNotification", dto);
        return text;
    }

    /// <summary>Posts a PorkChop welcome/roast into the user's main channel when they join the chat.
    /// Skips quick reconnects (awaySince within the cooldown) and is deduped per user, so app
    /// restarts and network blips don't spam. No-op when disabled, keyless, or the bot isn't set up.</summary>
    public async Task AnnounceJoinAsync(int userId, DateTime? awaySince)
    {
        if (!_announceJoins || string.IsNullOrEmpty(_apiKey) || BotUserId == 0 || userId == BotUserId) return;

        // Only greet a real arrival: a brand-new user (never seen) or someone back after a real absence.
        if (awaySince is DateTime seen && DateTime.UtcNow - seen < JoinCooldown) return;

        // In-memory dedupe so multiple connections / rapid reconnects can't double-announce.
        var now = DateTime.UtcNow;
        if (_lastJoinAnnounce.TryGetValue(userId, out var last) && now - last < JoinCooldown) return;
        _lastJoinAnnounce[userId] = now;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await db.Users.FindAsync(userId);
        if (user is null) return;

        var serverIds = await db.ServerMembers.Where(m => m.UserId == userId).Select(m => m.ServerId).ToListAsync();
        if (serverIds.Count == 0) return;
        var channel = await db.Channels.Where(c => serverIds.Contains(c.ServerId))
            .OrderByDescending(c => c.IsDefault).ThenBy(c => c.Position).FirstOrDefaultAsync();
        if (channel is null) return;

        var bio = string.IsNullOrWhiteSpace(user.Bio) ? "(no bio set)" : user.Bio;

        // Mine this person's own recent text + voice messages so the roast hits their real
        // interests, opinions, and likes/dislikes — not just their name.
        var history = await db.Messages
            .Where(m => m.AuthorId == userId && !m.IsDeleted
                        && (m.Source == MessageSource.Text || m.Source == MessageSource.Voice))
            .OrderByDescending(m => m.CreatedAt)
            .Take(50)
            .Select(m => m.Content)
            .ToListAsync();
        history.Reverse();
        var historyBlock = history.Count > 0
            ? "\n\nHere's a sample of THIS person's own recent messages (text + voice transcripts). Mine it for what they're actually into — their interests, hot takes, likes and dislikes — and roast those specifically:\n"
              + string.Join("\n", history.Select(h => "- " + h))
            : "";

        var prompt = $"A user just joined the chat.\nUsername: {user.Username}\nProfile bio: {bio}{historyBlock}\n\nWrite the welcome announcement.";
        var text = await PostToClaudeAsync(JoinAnnounceSystem, prompt);
        if (text is null) return;

        var msg = new Message { Content = text, AuthorId = BotUserId, ChannelId = channel.Id, Source = MessageSource.AI };
        db.Messages.Add(msg);
        await db.SaveChangesAsync();

        var dto = new MessageDto(msg.Id, text, BotUsername, BotUserId, msg.CreatedAt, channel.Id, MessageSource.AI);
        await hub.Clients.Group($"channel-{channel.Id}").SendAsync("ReceiveMessage", dto);
        await hub.Clients.Group($"server-{channel.ServerId}").SendAsync("NewMessageNotification", dto);
    }

    private const string AdviceSystem = "You are PorkChop, a friendly, down-to-earth advisor in a chat app called FatGuysSpeak. People mention you (@PorkChop) when they have a question or want advice. Read their message and the recent conversation, then give practical, helpful advice or a clear answer. If an image has been shared in the conversation, you can see it — describe it or answer questions about it. Be concise and conversational, and don't be afraid to have a little personality.";

    private const string SummarySystem = "You are PorkChop. Summarize ONE day of chat in a single channel of FatGuysSpeak. In 2-5 sentences, recap the main topics discussed, any decisions or plans made, and anything notable that was shared (links, files, jokes that landed). Be friendly and concise, refer to people by name, and don't invent anything that isn't in the transcript. If it was just light small talk, say so briefly.";

    private Task<string?> CallClaudeAsync(string userMessage, List<string> contextLines, List<ClaudeImage>? images = null)
    {
        var content = contextLines.Count > 0
            ? string.Join("\n", contextLines) + "\n\n" + userMessage
            : userMessage;
        return PostToClaudeAsync(AdviceSystem, content, images);
    }

    /// <summary>Lazily returns the cached PorkChop recap for a channel's completed (UTC) day and
    /// chat source (text vs voice are summarized separately), generating and storing it on first
    /// request. Returns null for today/future days, or when generation isn't possible.</summary>
    public async Task<DailySummaryDto?> GetOrCreateDailySummaryAsync(int channelId, DateTime dayUtc, MessageSource source)
    {
        var date = dayUtc.Date;
        if (date >= DateTime.UtcNow.Date) return null;   // only summarize days that are over

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cached = await db.DailyChatSummaries.FirstOrDefaultAsync(s => s.ChannelId == channelId && s.Date == date && s.Source == source);
        if (cached is not null)
            return new DailySummaryDto(date.ToString("yyyy-MM-dd"), cached.Summary, cached.MessageCount);

        var next = date.AddDays(1);
        var lines = await db.Messages
            .Where(m => m.ChannelId == channelId && !m.IsDeleted && m.Source == source
                        && m.CreatedAt >= date && m.CreatedAt < next)
            .Include(m => m.Author)
            .OrderBy(m => m.CreatedAt)
            .Select(m => m.Author.Username + ": " + m.Content)
            .ToListAsync();

        string summary;
        if (lines.Count == 0)
            summary = source == MessageSource.Voice ? "Quiet day — nobody spoke in this channel." : "Quiet day — no messages in this channel.";
        else
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                Console.WriteLine("[PorkChop] cannot summarize — no Anthropic API key set.");
                return null;
            }
            var lead = source == MessageSource.Voice
                ? "Here is a transcript of the SPOKEN voice conversation for this day (auto-transcribed, so wording may be rough; each line is \"speaker: words\"). Summarize what was talked about:\n\n"
                : "Here is the full text chat transcript for this day (each line is \"username: message\"). Summarize it:\n\n";
            var result = await PostToClaudeAsync(SummarySystem, lead + string.Join("\n", lines));
            if (result is null) return null;   // generation failed — don't cache, allow a later retry
            summary = result;
        }

        db.DailyChatSummaries.Add(new DailyChatSummary
        {
            ChannelId = channelId, Date = date, Source = source, Summary = summary,
            MessageCount = lines.Count, GeneratedAt = DateTime.UtcNow,
        });
        try { await db.SaveChangesAsync(); } catch { /* concurrent generation — ignore */ }
        return new DailySummaryDto(date.ToString("yyyy-MM-dd"), summary, lines.Count);
    }

    private const string WeeklyDigestSystem = "You are PorkChop, writing the weekly digest for a whole FatGuysSpeak server. You're given a week of messages from every channel, grouped by channel. In a few short paragraphs, give the crew a friendly big-picture recap of the week: the main things that happened in each active channel, recurring themes across the server, any decisions or plans, and notable moments. Refer to people and channels by name, keep it tight, and don't invent anything that isn't in the transcript. Do NOT add your own title or heading line (no \"Weekly Digest\" header) — the message already has one, so start straight into the recap.";

    /// <summary>Generates the server-wide digest for a completed week and posts it as a PorkChop
    /// message into the server's default channel. Idempotent per (server, weekStart). Returns the
    /// stored digest, or null when there's nothing to summarize / generation isn't possible.</summary>
    public async Task<WeeklyDigest?> GenerateAndPostWeeklyDigestAsync(int serverId, DateTime weekStartUtc)
    {
        var weekStart = weekStartUtc.Date;
        var weekEnd   = weekStart.AddDays(7);
        if (weekEnd > DateTime.UtcNow) return null;   // only summarize weeks that are fully over

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (await db.WeeklyDigests.AnyAsync(w => w.ServerId == serverId && w.WeekStart == weekStart))
            return null;   // already posted

        var rows = await db.Messages
            .Where(m => !m.IsDeleted && m.Source != MessageSource.AI
                        && m.CreatedAt >= weekStart && m.CreatedAt < weekEnd
                        && m.Channel.ServerId == serverId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { Channel = m.Channel.Name, User = m.Author.Username, m.Content })
            .ToListAsync();
        if (rows.Count == 0) return null;   // quiet week — nothing worth a digest

        if (string.IsNullOrEmpty(_apiKey) || BotUserId == 0)
        {
            Console.WriteLine("[PorkChop] cannot post weekly digest — API key set: {0}, bot user id: {1}.", !string.IsNullOrEmpty(_apiKey), BotUserId);
            return null;
        }

        var transcript = "Here is the full week of chat across the server, grouped by channel. Write the weekly digest:\n\n"
            + string.Join("\n\n", rows.GroupBy(r => r.Channel)
                .Select(g => $"#{g.Key}\n" + string.Join("\n", g.Select(r => $"{r.User}: {r.Content}"))));
        var text = await PostToClaudeAsync(WeeklyDigestSystem, transcript);
        if (text is null) return null;   // generation failed — retry next pass

        var digest = new WeeklyDigest { ServerId = serverId, WeekStart = weekStart, Summary = text, MessageCount = rows.Count };
        db.WeeklyDigests.Add(digest);

        var channel = await db.Channels.Where(c => c.ServerId == serverId)
            .OrderByDescending(c => c.IsDefault).ThenBy(c => c.Position).FirstOrDefaultAsync();
        if (channel is null) return null;

        var label = weekStart.ToString("MMM d");
        var msg = new Message
        {
            Content   = $"📅 **Weekly digest — week of {label}**\n\n{text}",
            AuthorId  = BotUserId,
            ChannelId = channel.Id,
            Source    = MessageSource.AI,
        };
        db.Messages.Add(msg);
        try { await db.SaveChangesAsync(); }
        catch { return null; }   // concurrent generation — another pass won the race

        var dto = new MessageDto(msg.Id, msg.Content, BotUsername, BotUserId, msg.CreatedAt, channel.Id, MessageSource.AI);
        await hub.Clients.Group($"channel-{channel.Id}").SendAsync("ReceiveMessage", dto);
        await hub.Clients.Group($"server-{serverId}").SendAsync("NewMessageNotification", dto);
        return digest;
    }

    private const string CatchupSystem = "You are PorkChop, giving one person a quick personal catch-up on what they missed in FatGuysSpeak while they were away. You're given the messages posted since they were last online, grouped by channel. In a few short, friendly sentences, tell them what they missed: the main conversations per channel, anything aimed at them or that needs a reply, and any decisions or plans. Refer to people and channels by name, keep it brief, and don't invent anything that isn't in the transcript.";

    /// <summary>Builds a personal "what you missed" recap for one user, for one chat source (the tab
    /// they're viewing — Text vs Voice are caught up separately), covering messages posted since they
    /// were last online (LastSeenAt, or the last 24h if never recorded). Excludes the user's own
    /// messages and respects per-channel read permissions. Ephemeral — not cached.</summary>
    public async Task<CatchupDto> GenerateCatchupAsync(int userId, MessageSource source)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await db.Users.FindAsync(userId);
        var since = user?.LastSeenAt ?? DateTime.UtcNow.AddHours(-24);

        var memberships = await db.ServerMembers.Where(m => m.UserId == userId).ToListAsync();
        var serverIds   = memberships.Select(m => m.ServerId).ToList();
        var roleByServer = memberships.ToDictionary(m => m.ServerId, m => m.Role);

        var rows = await db.Messages
            .Where(m => !m.IsDeleted && m.Source == source && m.AuthorId != userId
                        && m.CreatedAt >= since && serverIds.Contains(m.Channel.ServerId))
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.ChannelId, ServerId = m.Channel.ServerId, Channel = m.Channel.Name, User = m.Author.Username, m.Content })
            .ToListAsync();

        // Respect per-channel read permissions: drop channels the user isn't allowed to read.
        var minReadByChannel = await db.ChannelPermissions.ToDictionaryAsync(p => p.ChannelId, p => p.MinRoleToRead);
        var visible = rows.Where(r => !minReadByChannel.TryGetValue(r.ChannelId, out var min)
                                      || (roleByServer.TryGetValue(r.ServerId, out var role) && role >= min)).ToList();

        var kind = source == MessageSource.Voice ? "voice" : "chat";
        if (visible.Count == 0)
            return new CatchupDto($"You're all caught up — nothing new in {kind} since you were last here. 👍", 0, user?.LastSeenAt);

        if (string.IsNullOrEmpty(_apiKey))
        {
            Console.WriteLine("[PorkChop] cannot generate catch-up — no Anthropic API key set.");
            return new CatchupDto($"You missed {visible.Count} {kind} message(s), but PorkChop isn't configured to summarize them.", visible.Count, user?.LastSeenAt);
        }

        var lead = source == MessageSource.Voice
            ? "Here is the SPOKEN voice conversation (auto-transcribed) posted since this person was last online, grouped by channel. Give them their personal voice catch-up:\n\n"
            : "Here is everything posted in text chat since this person was last online, grouped by channel. Give them their personal catch-up:\n\n";
        var transcript = lead
            + string.Join("\n\n", visible.GroupBy(r => r.Channel)
                .Select(g => $"#{g.Key}\n" + string.Join("\n", g.Select(r => $"{r.User}: {r.Content}"))));
        var text = await PostToClaudeAsync(CatchupSystem, transcript);
        return new CatchupDto(text ?? "PorkChop couldn't put together a recap right now — try again in a moment.", visible.Count, user?.LastSeenAt);
    }

    private async Task<string?> PostToClaudeAsync(string system, string content, List<ClaudeImage>? images = null)
    {
        try
        {
            var client = httpFactory.CreateClient("anthropic");

            // With images, the user content must be an array of blocks (text + image); otherwise a
            // plain string keeps the request simple.
            object messageContent = images is { Count: > 0 }
                ? new object[] { new { type = "text", text = content } }
                    .Concat(images.Select(img => (object)new
                    {
                        type = "image",
                        source = new { type = "base64", media_type = img.MediaType, data = img.Base64 }
                    })).ToArray()
                : content;

            var payload = new
            {
                model      = _model,
                max_tokens = 1024,
                system,
                messages   = new[] { new { role = "user", content = messageContent } }
            };

            var res = await client.PostAsJsonAsync("messages", payload);
            if (!res.IsSuccessStatusCode)
            {
                Console.WriteLine($"[PorkChop] Anthropic API {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
                return null;
            }

            var data = await res.Content.ReadFromJsonAsync<AnthropicResponse>();
            return data?.Content?.FirstOrDefault(b => b.Type == "text")?.Text?.Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PorkChop] Anthropic call failed: {ex.Message}");
            return null;
        }
    }

    private record AnthropicResponse(List<ContentBlock>? Content);
    private record ContentBlock(string Type, string? Text);

    private record ClaudeImage(string MediaType, string Base64);

    // Read up to two recent image attachments (local /uploads only) so PorkChop can actually see
    // images people share. Skips non-images, externally-hosted URLs, path-escape attempts, and
    // anything over ~4 MB (Anthropic's per-image limit).
    private static List<ClaudeImage> ReadRecentImages(IEnumerable<Message> recent, string contentRoot)
    {
        var uploadsDir = Path.Combine(contentRoot, "uploads");
        var images = new List<ClaudeImage>();
        foreach (var m in recent.Reverse())   // newest first
        {
            if (images.Count >= 2) break;
            var url = m.AttachmentUrl;
            if (string.IsNullOrEmpty(url)) continue;
            var idx = url.IndexOf("/uploads/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var file = url[(idx + "/uploads/".Length)..];
            if (file.Contains('/') || file.Contains('\\') || file.Contains("..")) continue;
            var media = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png"            => "image/png",
                ".gif"            => "image/gif",
                ".webp"           => "image/webp",
                _                 => null,
            };
            if (media is null) continue;
            var path = Path.Combine(uploadsDir, file);
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024) continue;
            images.Add(new ClaudeImage(media, Convert.ToBase64String(bytes)));
        }
        images.Reverse();   // back to chronological order
        return images;
    }
}
