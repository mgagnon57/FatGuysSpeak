namespace FatGuysSpeak.Shared;

public record RegisterRequest(string Username, string Password, string Email);
public record LoginRequest(string Username, string Password);
public record AuthResponse(string Token, string Username, int UserId);

public record ServerDto(int Id, string Name, string? Description, string OwnerId, int MemberCount);
public record CreateServerRequest(string Name, string? Description);

public record ChannelDto(int Id, string Name, ChannelType Type, int ServerId, int Position);
public record CreateChannelRequest(string Name, ChannelType Type);

public record MessageDto(int Id, string Content, string AuthorUsername, int AuthorId, DateTime CreatedAt, int ChannelId, MessageSource Source = MessageSource.Text);
public record SendMessageRequest(string Content, MessageSource Source = MessageSource.Text);

public enum MessageSource { Text, Voice }

public record UserDto(int Id, string Username, UserStatus Status);

public enum ChannelType { Text, Voice }
public enum UserStatus { Offline, Online, Away, DoNotDisturb }

public record VoiceStateDto(int UserId, string Username, int? ChannelId, bool Muted, bool Deafened);
public record JoinVoiceRequest(int ChannelId);

public record LinkPreviewDto(string Url, string? Title, string? Description, string? ImageUrl, string? SiteName);
