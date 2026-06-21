using System.Text.Json;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Server;

/// <summary>
/// Guards the PorkChop voice-toggle wiring. The new SpeakReply field must default to true so that
/// older clients (which post a SendMessageRequest with no SpeakReply at all) keep getting voiced
/// PorkChop replies, while new clients can opt out by sending speakReply=false.
/// </summary>
public class SendMessageRequestTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MissingSpeakReply_DefaultsToTrue()
    {
        var req = JsonSerializer.Deserialize<SendMessageRequest>("""{"content":"hi"}""", Web);
        Assert.NotNull(req);
        Assert.True(req!.SpeakReply);   // old clients omit the field → voice stays on
    }

    [Fact]
    public void ExplicitSpeakReplyFalse_IsHonored()
    {
        var req = JsonSerializer.Deserialize<SendMessageRequest>(
            """{"content":"@PorkChop hi","speakReply":false}""", Web);
        Assert.NotNull(req);
        Assert.False(req!.SpeakReply);   // new client with voice toggle off → text only
    }
}
