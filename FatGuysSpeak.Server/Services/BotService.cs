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
        var reply = await CallClaudeAsync(triggerContent, contextLines);
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

    private const string AdviceSystem = "You are PorkChop, a friendly, down-to-earth advisor in a chat app called FatGuysSpeak. People mention you (@PorkChop) when they have a question or want advice. Read their message and the recent conversation, then give practical, helpful advice or a clear answer. Be concise and conversational, and don't be afraid to have a little personality.";

    private const string SummarySystem = "You are PorkChop. Summarize ONE day of chat in a single channel of FatGuysSpeak. In 2-5 sentences, recap the main topics discussed, any decisions or plans made, and anything notable that was shared (links, files, jokes that landed). Be friendly and concise, refer to people by name, and don't invent anything that isn't in the transcript. If it was just light small talk, say so briefly.";

    private Task<string?> CallClaudeAsync(string userMessage, List<string> contextLines)
    {
        var content = contextLines.Count > 0
            ? string.Join("\n", contextLines) + "\n\n" + userMessage
            : userMessage;
        return PostToClaudeAsync(AdviceSystem, content);
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

    private async Task<string?> PostToClaudeAsync(string system, string content)
    {
        try
        {
            var client = httpFactory.CreateClient("anthropic");
            var payload = new
            {
                model      = _model,
                max_tokens = 1024,
                system,
                messages   = new[] { new { role = "user", content } }
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
}
