using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.Services;

public class ApiService
{
    private HttpClient _http;
    private string? _token;

    // Beta default: the hosted Azure server, so a fresh install connects with no configuration.
    // Users can still point at any server (e.g. http://localhost:5238 for local dev) in Settings.
    public const string DefaultServerUrl = "https://fatguysspeak-server.azurewebsites.net";

    public string ServerUrl { get; private set; }

    public ApiService()
    {
        ServerUrl = Preferences.Get("server_url", DefaultServerUrl).TrimEnd('/');
        _http = BuildClient(ServerUrl);
    }

    public void SetServerUrl(string url)
    {
        var normalized = url.TrimEnd('/');
        ValidateServerUrl(normalized);
        ServerUrl = normalized;
        Preferences.Set("server_url", normalized);
        _http = BuildClient(normalized);
        if (_token is not null)
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _token);
    }

    private static void ValidateServerUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Invalid server URL: {url}");
        var host = uri.Host;
        bool isLocal = host == "localhost" || host == "127.0.0.1" || host == "::1";
        if (!isLocal && uri.Scheme != "https")
            throw new ArgumentException("Remote server connections require HTTPS.");
    }

    private static HttpClient BuildClient(string baseUrl) =>
        new() { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromSeconds(30) };

    public int CurrentUserId { get; private set; }
    public string CurrentUsername { get; private set; } = "";
    public string? CurrentAvatarUrl { get; private set; }

    public void SetToken(string token)
    {
        _token = token;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void SetCurrentUser(int userId, string username, string? avatarUrl = null)
    {
        CurrentUserId = userId;
        CurrentUsername = username;
        CurrentAvatarUrl = avatarUrl;
    }

    public void UpdateAvatarUrl(string url) => CurrentAvatarUrl = url;

    public void ClearToken()
    {
        _token = null;
        CurrentUserId = 0;
        CurrentUsername = "";
        CurrentAvatarUrl = null;
        _http.DefaultRequestHeaders.Authorization = null;
    }

    public string? Token => _token;

    public async Task<AuthResponse?> RegisterAsync(RegisterRequest req)
    {
        var resp = await _http.PostAsJsonAsync("api/auth/register", req);
        if (!resp.IsSuccessStatusCode)
            throw new Exception((await resp.Content.ReadAsStringAsync()).Trim('"'));
        return await resp.Content.ReadFromJsonAsync<AuthResponse>();
    }

    public async Task<AuthResponse?> LoginAsync(LoginRequest req)
    {
        var resp = await _http.PostAsJsonAsync("api/auth/login", req);
        if (!resp.IsSuccessStatusCode)
            throw new Exception((await resp.Content.ReadAsStringAsync()).Trim('"'));
        return await resp.Content.ReadFromJsonAsync<AuthResponse>();
    }

    public async Task<string?> GetServerVersionAsync()
    {
        try
        {
            var v = await _http.GetFromJsonAsync<VersionInfo>("api/version");
            return v?.Version;
        }
        catch { return null; }
    }

    public async Task<GoogleConfigResponse?> GetGoogleConfigAsync()
    {
        try { return await _http.GetFromJsonAsync<GoogleConfigResponse>("api/auth/external/google/config"); }
        catch { return null; }
    }

    public async Task<AuthResponse?> ExchangeGoogleCodeAsync(GoogleCodeExchangeRequest req)
    {
        var resp = await _http.PostAsJsonAsync("api/auth/external/google/exchange", req);
        if (!resp.IsSuccessStatusCode)
            throw new Exception((await resp.Content.ReadAsStringAsync()).Trim('"'));
        return await resp.Content.ReadFromJsonAsync<AuthResponse>();
    }

    public Task<List<ServerDto>?> GetServersAsync() =>
        _http.GetFromJsonAsync<List<ServerDto>>("api/servers");

    public Task<List<ChannelDto>?> GetChannelsAsync(int serverId) =>
        _http.GetFromJsonAsync<List<ChannelDto>>($"api/servers/{serverId}/channels");

    public async Task<ChannelDto?> CreateChannelAsync(int serverId, CreateChannelRequest req)
    {
        var resp = await _http.PostAsJsonAsync($"api/servers/{serverId}/channels", req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ChannelDto>();
    }

    public Task<List<UserDto>?> GetMembersAsync(int serverId) =>
        _http.GetFromJsonAsync<List<UserDto>>($"api/servers/{serverId}/members");

    public Task<List<ServerMemberDto>?> GetMemberRolesAsync(int serverId) =>
        _http.GetFromJsonAsync<List<ServerMemberDto>>($"api/servers/{serverId}/members/details");

    public async Task<bool> SetMemberRoleAsync(int serverId, int userId, ServerRole role)
    {
        var resp = await _http.PutAsJsonAsync(
            $"api/servers/{serverId}/members/{userId}/role", new SetRoleRequest(role));
        return resp.IsSuccessStatusCode;
    }

    public Task<List<MessageDto>?> GetMessagesAsync(int channelId) =>
        _http.GetFromJsonAsync<List<MessageDto>>($"api/channels/{channelId}/messages");

    public Task<List<MessageDto>?> GetMessagesAfterAsync(int channelId, int afterId) =>
        _http.GetFromJsonAsync<List<MessageDto>>($"api/channels/{channelId}/messages?afterId={afterId}");

    // Older-history page: the newest messages strictly before beforeId, oldest-first.
    public Task<List<MessageDto>?> GetMessagesBeforeAsync(int channelId, int beforeId, int limit = 50) =>
        _http.GetFromJsonAsync<List<MessageDto>>($"api/channels/{channelId}/messages?beforeId={beforeId}&limit={limit}");

    // Create a poll in a channel; returns the posted poll message (or null on failure).
    public async Task<MessageDto?> CreatePollAsync(int channelId, string question, List<string> options)
    {
        var resp = await _http.PostAsJsonAsync($"api/channels/{channelId}/polls", new CreatePollRequest(question, options));
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<MessageDto>() : null;
    }

    // Cast/change/retract a vote; returns the poll with this user's vote applied.
    public async Task<PollDto?> VotePollAsync(int pollId, int optionId)
    {
        var resp = await _http.PostAsJsonAsync($"api/polls/{pollId}/vote", new PollVoteRequest(optionId));
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<PollDto>() : null;
    }

    // Admin-only: fire PorkChop's idle roast in a channel on demand (for testing / fun).
    public async Task<bool> NudgeChannelAsync(int channelId)
    {
        try { var r = await _http.PostAsync($"api/channels/{channelId}/nudge", null); return r.IsSuccessStatusCode; }
        catch { return false; }
    }

    // PorkChop's personal "what you missed since last online" recap for the current user and chat source.
    public async Task<CatchupDto?> GetCatchupAsync(MessageSource source)
    {
        try { return await _http.GetFromJsonAsync<CatchupDto>($"api/catchup?source={source}"); }
        catch { return null; }
    }

    // PorkChop's recap for a channel's completed (UTC) day and chat source (Text/Voice are
    // summarized separately); null when not available (today, no key, error).
    public async Task<DailySummaryDto?> GetDaySummaryAsync(int channelId, string date, MessageSource source)
    {
        try { return await _http.GetFromJsonAsync<DailySummaryDto>($"api/channels/{channelId}/summary?date={date}&source={source}"); }
        catch { return null; }
    }

    public Task<List<MessageDto>?> SearchMessagesAsync(int channelId, string query) =>
        _http.GetFromJsonAsync<List<MessageDto>>(
            $"api/channels/{channelId}/messages/search?q={Uri.EscapeDataString(query)}");

    public async Task<(MessageDto? Dto, string? Error)> SendMessageAsync(int channelId, string content, MessageSource source = MessageSource.Text, string? attachmentUrl = null, int? replyToId = null, string? attachmentFileName = null, int? threadId = null, bool speakReply = true)
    {
        var resp = await _http.PostAsJsonAsync($"api/channels/{channelId}/messages", new SendMessageRequest(content, source, attachmentUrl, replyToId, attachmentFileName, threadId, speakReply));
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<MessageDto>(), null);
        return (null, await resp.Content.ReadAsStringAsync());
    }

    public async Task<bool> MuteUserAsync(int serverId, int userId, int seconds)
    {
        var r = await _http.PutAsJsonAsync($"api/servers/{serverId}/members/{userId}/mute", new MuteUserRequest(seconds));
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> TempBanUserAsync(int serverId, int userId, int seconds)
    {
        var r = await _http.PutAsJsonAsync($"api/servers/{serverId}/members/{userId}/tempban", new TempBanRequest(seconds));
        return r.IsSuccessStatusCode;
    }

    public Task<List<MessageDto>?> GetThreadMessagesAsync(int channelId, int messageId) =>
        _http.GetFromJsonAsync<List<MessageDto>>($"api/channels/{channelId}/messages/{messageId}/thread");

    public string GetServerIconUrl(int serverId) => $"{ServerUrl}/api/servers/{serverId}/icon";

    public async Task<bool> UploadServerIconAsync(int serverId, Stream stream, string fileName, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", Path.GetFileName(fileName));
        var resp = await _http.PutAsync($"api/servers/{serverId}/icon", content);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteServerIconAsync(int serverId)
    {
        var resp = await _http.DeleteAsync($"api/servers/{serverId}/icon");
        return resp.IsSuccessStatusCode;
    }

    public async Task<string?> UploadAvatarAsync(Stream stream, string fileName, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", Path.GetFileName(fileName));
        var resp = await _http.PostAsync("api/users/me/avatar", content);
        if (!resp.IsSuccessStatusCode) return null;
        var dto = await resp.Content.ReadFromJsonAsync<AttachmentDto>();
        return dto?.Url;
    }

    public async Task<AttachmentDto?> UploadAttachmentAsync(Stream stream, string fileName, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", Path.GetFileName(fileName));
        var resp = await _http.PostAsync("api/attachments", content);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new Exception(err.Trim('"'));
        }
        return await resp.Content.ReadFromJsonAsync<AttachmentDto>();
    }

    // ── Soundboard ──────────────────────────────────────────────────────────────
    public async Task<List<SoundClipDto>> GetSoundsAsync(int serverId)
    {
        try { return await _http.GetFromJsonAsync<List<SoundClipDto>>($"api/servers/{serverId}/sounds") ?? []; }
        catch { return []; }
    }

    public async Task<SoundClipDto?> UploadSoundAsync(int serverId, string name, string? emoji, Stream wav, string fileName)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(name), "name" },
        };
        if (!string.IsNullOrWhiteSpace(emoji)) content.Add(new StringContent(emoji), "emoji");
        var fileContent = new StreamContent(wav);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", Path.GetFileName(fileName));
        var resp = await _http.PostAsync($"api/servers/{serverId}/sounds", content);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new Exception(err.Trim('"'));
        }
        return await resp.Content.ReadFromJsonAsync<SoundClipDto>();
    }

    public async Task<bool> PlaySoundAsync(int soundId)
    {
        var resp = await _http.PostAsync($"api/sounds/{soundId}/play", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteSoundAsync(int soundId)
    {
        var resp = await _http.DeleteAsync($"api/sounds/{soundId}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<MessageDto?> EditMessageAsync(int channelId, int messageId, string newContent)
    {
        var resp = await _http.PutAsJsonAsync($"api/channels/{channelId}/messages/{messageId}", new EditMessageRequest(newContent));
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<MessageDto>() : null;
    }

    public async Task<bool> DeleteMessageAsync(int channelId, int messageId)
    {
        var resp = await _http.DeleteAsync($"api/channels/{channelId}/messages/{messageId}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> JoinServerAsync(int serverId)
    {
        var r = await _http.PostAsync($"api/servers/{serverId}/join", null);
        return r.IsSuccessStatusCode;
    }

    public Task<ServerInviteDto?> GetInviteAsync(int serverId) =>
        _http.GetFromJsonAsync<ServerInviteDto>($"api/servers/{serverId}/invite");

    public Task<ServerInviteDto?> ResetInviteAsync(int serverId) =>
        _http.PostAsync($"api/servers/{serverId}/invite/reset", null)
             .ContinueWith(t => t.Result.IsSuccessStatusCode
                 ? t.Result.Content.ReadFromJsonAsync<ServerInviteDto>().Result
                 : null);

    public Task<ServerInviteDto?> PreviewInviteAsync(string code) =>
        _http.GetFromJsonAsync<ServerInviteDto>($"api/servers/by-invite/{code}");

    public async Task<ServerDto?> JoinByInviteAsync(string code)
    {
        var r = await _http.PostAsync($"api/servers/by-invite/{code}/join", null);
        return r.IsSuccessStatusCode
            ? await r.Content.ReadFromJsonAsync<ServerDto>()
            : null;
    }

    public Task<UserProfileDto?> GetUserProfileAsync(int userId, int serverId) =>
        _http.GetFromJsonAsync<UserProfileDto>($"api/users/{userId}/profile?serverId={serverId}");

    public async Task UpdateStatusAsync(UserStatus status, string? statusText = null)
    {
        await _http.PutAsJsonAsync("api/users/me/status", new UpdateStatusRequest(status, statusText));
    }

    public async Task<bool> UpdateBioAsync(string? bio)
    {
        var resp = await _http.PutAsJsonAsync("api/users/me/bio", new UpdateBioRequest(bio));
        return resp.IsSuccessStatusCode;
    }

    public Task<List<BlockedUserDto>?> GetBlockedUsersAsync() =>
        _http.GetFromJsonAsync<List<BlockedUserDto>>("api/users/me/blocks");

    public async Task<bool> BlockUserAsync(int userId)
    {
        var resp = await _http.PostAsync($"api/users/me/blocks/{userId}", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> UnblockUserAsync(int userId)
    {
        var resp = await _http.DeleteAsync($"api/users/me/blocks/{userId}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<ReactionsUpdatedDto?> ToggleReactionAsync(int channelId, int messageId, string emoji)
    {
        var encoded = Uri.EscapeDataString(emoji);
        var resp = await _http.PostAsync(
            $"api/channels/{channelId}/messages/{messageId}/reactions/{encoded}", null);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<ReactionsUpdatedDto>()
            : null;
    }

    public Task<List<DirectConversationDto>?> GetDmConversationsAsync() =>
        _http.GetFromJsonAsync<List<DirectConversationDto>>("api/dm");

    public async Task<DirectConversationDto?> OpenDmConversationAsync(int userId)
    {
        var resp = await _http.PostAsync($"api/dm/open/{userId}", null);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<DirectConversationDto>() : null;
    }

    public Task<List<DirectMessageDto>?> GetDmMessagesAsync(int conversationId) =>
        _http.GetFromJsonAsync<List<DirectMessageDto>>($"api/dm/{conversationId}/messages");

    public async Task<DirectMessageDto?> SendDmAsync(int conversationId, string? content, string? attachmentUrl = null, string? attachmentFileName = null)
    {
        var resp = await _http.PostAsJsonAsync($"api/dm/{conversationId}/messages",
            new SendDirectMessageRequest(content, attachmentUrl, attachmentFileName));
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<DirectMessageDto>() : null;
    }

    public async Task<bool> DeleteDmMessageAsync(int conversationId, int messageId)
    {
        var resp = await _http.DeleteAsync($"api/dm/{conversationId}/messages/{messageId}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<List<MessageDto>?> GetChannelPinsAsync(int channelId)
    {
        var resp = await _http.GetAsync($"api/channels/{channelId}/pins");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<List<MessageDto>>();
    }

    public async Task<bool> PinChannelMessageAsync(int channelId, int messageId)
    {
        var resp = await _http.PostAsync($"api/channels/{channelId}/messages/{messageId}/pin", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> UnpinChannelMessageAsync(int channelId, int messageId)
    {
        var resp = await _http.DeleteAsync($"api/channels/{channelId}/messages/{messageId}/pin");
        return resp.IsSuccessStatusCode;
    }

    public async Task<List<DirectMessageDto>?> GetDmPinsAsync(int conversationId)
    {
        var resp = await _http.GetAsync($"api/dm/{conversationId}/pins");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<List<DirectMessageDto>>();
    }

    public async Task<bool> PinDmMessageAsync(int conversationId, int messageId)
    {
        var resp = await _http.PostAsync($"api/dm/{conversationId}/messages/{messageId}/pin", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> UnpinDmMessageAsync(int conversationId, int messageId)
    {
        var resp = await _http.DeleteAsync($"api/dm/{conversationId}/messages/{messageId}/pin");
        return resp.IsSuccessStatusCode;
    }

    public async Task<DmReadStateDto?> MarkDmAsReadAsync(int conversationId)
    {
        var resp = await _http.PostAsync($"api/dm/{conversationId}/read", null);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<DmReadStateDto>();
    }

    public Task<List<CategoryDto>?> GetCategoriesAsync(int serverId) =>
        _http.GetFromJsonAsync<List<CategoryDto>>($"api/servers/{serverId}/categories");

    public async Task<CategoryDto?> CreateCategoryAsync(int serverId, CreateCategoryRequest req)
    {
        var r = await _http.PostAsJsonAsync($"api/servers/{serverId}/categories", req);
        return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<CategoryDto>() : null;
    }

    public Task RenameCategoryAsync(int serverId, int categoryId, RenameCategoryRequest req) =>
        _http.PutAsJsonAsync($"api/servers/{serverId}/categories/{categoryId}", req);

    public Task DeleteCategoryAsync(int serverId, int categoryId) =>
        _http.DeleteAsync($"api/servers/{serverId}/categories/{categoryId}");

    public Task SetChannelCategoryAsync(int serverId, int channelId, SetChannelCategoryRequest req) =>
        _http.PutAsJsonAsync($"api/servers/{serverId}/channels/{channelId}/category", req);

    public async Task<bool> SetSlowmodeAsync(int serverId, int channelId, int seconds)
    {
        var r = await _http.PutAsJsonAsync($"api/servers/{serverId}/channels/{channelId}/slowmode", new SetSlowmodeRequest(seconds));
        return r.IsSuccessStatusCode;
    }

    public Task<List<WordFilterDto>?> GetWordFiltersAsync(int serverId) =>
        _http.GetFromJsonAsync<List<WordFilterDto>>($"api/servers/{serverId}/wordfilter");

    public async Task<WordFilterDto?> AddWordFilterAsync(int serverId, string pattern)
    {
        var r = await _http.PostAsJsonAsync($"api/servers/{serverId}/wordfilter", new AddWordFilterRequest(pattern));
        return r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<WordFilterDto>() : null;
    }

    public async Task<bool> RemoveWordFilterAsync(int serverId, int filterId)
    {
        var r = await _http.DeleteAsync($"api/servers/{serverId}/wordfilter/{filterId}");
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> SetServerNotifLevelAsync(int serverId, NotifLevel level)
    {
        var resp = await _http.PutAsJsonAsync($"api/servers/{serverId}/notif", new SetNotifLevelRequest(level));
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetChannelNotifLevelAsync(int channelId, NotifLevel level)
    {
        var resp = await _http.PutAsJsonAsync($"api/channels/{channelId}/notif", new SetNotifLevelRequest(level));
        return resp.IsSuccessStatusCode;
    }

    public async Task<LinkPreviewDto?> GetLinkPreviewAsync(string url)
    {
        try
        {
            var encoded = Uri.EscapeDataString(url);
            var resp = await _http.GetAsync($"api/preview?url={encoded}");
            if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
            return resp.IsSuccessStatusCode
                ? await resp.Content.ReadFromJsonAsync<LinkPreviewDto>()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public Task<List<GifResult>?> GetTrendingGifsAsync(int limit = 24) =>
        _http.GetFromJsonAsync<List<GifResult>>($"api/gifs/trending?limit={limit}");

    public Task<List<GifResult>?> SearchGifsAsync(string query, int limit = 24) =>
        _http.GetFromJsonAsync<List<GifResult>>($"api/gifs/search?q={Uri.EscapeDataString(query)}&limit={limit}");
}

public record GifResult(string PreviewUrl, string Url);
