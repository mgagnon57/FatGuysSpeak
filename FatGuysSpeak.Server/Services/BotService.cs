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

        var recent = await db.Messages
            .Where(m => m.ChannelId == channelId && !m.IsDeleted && m.Source != MessageSource.AI)
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

    private async Task<string?> CallClaudeAsync(string userMessage, List<string> contextLines)
    {
        try
        {
            var client = httpFactory.CreateClient("anthropic");
            var content = contextLines.Count > 0
                ? string.Join("\n", contextLines) + "\n\n" + userMessage
                : userMessage;

            var payload = new
            {
                model      = _model,
                max_tokens = 1024,
                system     = "You are PorkChop, a friendly, down-to-earth advisor in a chat app called FatGuysSpeak. People mention you (@PorkChop) when they have a question or want advice. Read their message and the recent conversation, then give practical, helpful advice or a clear answer. Be concise and conversational, and don't be afraid to have a little personality.",
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
