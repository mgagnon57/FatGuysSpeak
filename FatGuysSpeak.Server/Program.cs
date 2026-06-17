using System.Text;
using System.Threading.RateLimiting;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 26 * 1024 * 1024); // 26 MB — covers 25 MB file uploads

// Railway (and most cloud hosts) set DATABASE_URL as a postgres:// URI.
// Fall back to the config connection string (SQLite for local dev).
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrEmpty(databaseUrl))
{
    var uri = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':');
    var pgConn = $"Host={uri.Host};Port={uri.Port};" +
                 $"Database={uri.AbsolutePath.TrimStart('/')};" +
                 $"Username={userInfo[0]};Password={Uri.UnescapeDataString(userInfo[1])};" +
                 "SSL Mode=Require;Trust Server Certificate=true";
    builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(pgConn));
}
else
{
    var connStr = builder.Configuration.GetConnectionString("Default") ?? "Data Source=fatguys.db";
    builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlite(connStr));
}

builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<FatGuysSpeak.Server.Services.IGoogleTokenValidator, FatGuysSpeak.Server.Services.GoogleTokenValidator>();
builder.Services.AddHttpClient("google", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<FatGuysSpeak.Server.Services.IGoogleCodeExchanger, FatGuysSpeak.Server.Services.GoogleCodeExchanger>();
builder.Services.AddSingleton<FatGuysSpeak.Server.Services.ServerMetricsService>();
builder.Services.AddSingleton<FatGuysSpeak.Server.Services.OnlineTimeTracker>();
builder.Services.AddHttpClient("anthropic", c =>
{
    c.BaseAddress = new Uri("https://api.anthropic.com/v1/");
    c.DefaultRequestHeaders.Add("x-api-key", builder.Configuration["Anthropic:ApiKey"] ?? "");
    c.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<FatGuysSpeak.Server.Services.BotService>();

var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey))
    throw new InvalidOperationException("JWT key is not configured. Set Jwt:Key in appsettings or the JWT__Key environment variable.");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("JWT key is too short. It must be at least 32 bytes (256 bits).");

builder.Services.AddAuthentication(opt =>
{
    // Routes to JWT or Dashboard cookie depending on which is present
    opt.DefaultScheme = "SmartBearer";
    opt.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    opt.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddPolicyScheme("SmartBearer", "JWT or Dashboard Cookie", opt =>
{
    opt.ForwardDefaultSelector = ctx =>
        ctx.Request.Cookies.ContainsKey(".FatGuysSpeak.Dashboard")
            ? "Dashboard"
            : JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(opt =>
{
    opt.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
    };
    // Allow JWT via query string for SignalR WebSocket handshakes
    opt.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            var token = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                ctx.Token = token;
            return Task.CompletedTask;
        },
        OnTokenValidated = ctx =>
        {
            var bl = ctx.HttpContext.RequestServices.GetRequiredService<SessionBlacklistService>();
            var raw = ctx.HttpContext.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "")
                      ?? ctx.HttpContext.Request.Query["access_token"].FirstOrDefault()
                      ?? "";
            if (!string.IsNullOrEmpty(raw) && bl.IsRevoked(SessionBlacklistService.HashToken(raw)))
                ctx.Fail("Token has been revoked.");
            return Task.CompletedTask;
        }
    };
})
.AddCookie("Dashboard", opt =>
{
    opt.Cookie.Name = ".FatGuysSpeak.Dashboard";
    opt.LoginPath = "/dashboard/login";
    opt.AccessDeniedPath = "/dashboard/login";
    opt.Cookie.HttpOnly = true;
    opt.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
    opt.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
    opt.ExpireTimeSpan = TimeSpan.FromHours(8);
    opt.SlidingExpiration = true;
});

// Rate limiters: auth (per-IP) + messages (per-user)
builder.Services.AddRateLimiter(opt =>
{
    // Per-IP (not global) so one client can't exhaust the budget and lock out everyone else.
    opt.AddPolicy("auth", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = 10,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
        });
    });

    opt.AddPolicy("messages", httpContext =>
    {
        var userId = httpContext.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? httpContext.Connection.RemoteIpAddress?.ToString()
                     ?? "unknown";
        return RateLimitPartition.GetSlidingWindowLimiter(userId, _ => new SlidingWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = 30,
            SegmentsPerWindow = 6,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
        });
    });

    opt.AddPolicy("dashboard", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"dash:{ip}", _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = 5,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
        });
    });

    opt.OnRejected = async (context, token) =>
    {
        var svc = context.HttpContext.RequestServices.GetRequiredService<ServerMetricsService>();
        var username = context.HttpContext.User?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
        var ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        svc.RecordRateLimitHit(username is not null ? $"{username} ({ip})" : ip);
        context.HttpContext.Response.StatusCode = 429;
        await context.HttpContext.Response.WriteAsync("Rate limit exceeded. Slow down.", token);
    };

    opt.RejectionStatusCode = 429;
});

builder.Services.AddAuthorization(opt =>
{
    opt.AddPolicy("DashboardAdmin", policy =>
        policy.AddAuthenticationSchemes("Dashboard")
              .RequireAuthenticatedUser());
});
builder.Services.AddControllers();
builder.Services.AddHttpClient("preview", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; FatGuysSpeak/1.0)");
    c.Timeout = TimeSpan.FromSeconds(8);
}).ConfigurePrimaryHttpMessageHandler(FatGuysSpeak.Server.Services.SsrfGuard.CreateHandler);
builder.Services.AddHttpClient("giphy", c =>
{
    c.BaseAddress = new Uri("https://api.giphy.com/v1/gifs/");
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHostedService<FatGuysSpeak.Server.Services.TempBanCleanupService>();
builder.Services.AddHostedService<FatGuysSpeak.Server.Services.AuditLogCleanupService>();
builder.Services.AddSingleton<FatGuysSpeak.Server.Services.SessionBlacklistService>();
builder.Services.AddSingleton<FatGuysSpeak.Server.Services.WebhookDeliveryService>();
builder.Services.AddSingleton<FatGuysSpeak.Server.Services.AutomodService>();
builder.Services.AddHttpClient("webhook", c => c.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(FatGuysSpeak.Server.Services.SsrfGuard.CreateHandler);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "FatGuysSpeak API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization", Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer", BearerFormat = "JWT", In = Microsoft.OpenApi.Models.ParameterLocation.Header
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.Models.OpenApiSecurityScheme { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
    });
});
builder.Services.AddSignalR(opt =>
{
    opt.MaximumReceiveMessageSize = 4 * 1024 * 1024; // 4 MB — full-res JPEG frames at high quality
});
builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(p => p
        .SetIsOriginAllowed(origin => new Uri(origin).Host is "localhost" or "127.0.0.1")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    ctx.Database.EnsureCreated();
    // Non-destructive migration: add Source column only if it doesn't exist yet
    using var conn = ctx.Database.GetDbConnection();
    conn.Open();
    using var checkCmd = conn.CreateCommand();
    bool isPostgres = ctx.Database.ProviderName?.Contains("Npgsql") == true;
    if (isPostgres)
    {
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Messages' AND column_name='Source'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Messages\" ADD COLUMN \"Source\" INTEGER NOT NULL DEFAULT 0");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Messages' AND column_name='AttachmentUrl'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Messages\" ADD COLUMN \"AttachmentUrl\" TEXT");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Messages' AND column_name='IsDeleted'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Messages\" ADD COLUMN \"IsDeleted\" BOOLEAN NOT NULL DEFAULT FALSE");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Messages' AND column_name='EditedAt'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Messages\" ADD COLUMN \"EditedAt\" TIMESTAMP");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Users' AND column_name='AvatarUrl'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" ADD COLUMN \"AvatarUrl\" TEXT");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Messages' AND column_name='ReplyToId'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Messages\" ADD COLUMN \"ReplyToId\" INTEGER");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Servers' AND column_name='InviteCode'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"InviteCode\" TEXT");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Messages' AND column_name='AttachmentFileName'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Messages\" ADD COLUMN \"AttachmentFileName\" TEXT");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Channels' AND column_name='CategoryId'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Channels\" ADD COLUMN \"CategoryId\" INTEGER");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Users' AND column_name='Bio'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" ADD COLUMN \"Bio\" TEXT");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Channels' AND column_name='SlowmodeSeconds'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Channels\" ADD COLUMN \"SlowmodeSeconds\" INTEGER NOT NULL DEFAULT 0");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Messages' AND column_name='ThreadId'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Messages\" ADD COLUMN \"ThreadId\" INTEGER REFERENCES \"Messages\"(\"Id\")");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='ServerMembers' AND column_name='MutedUntil'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"ServerMembers\" ADD COLUMN \"MutedUntil\" TIMESTAMPTZ");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""TempBans"" (
                ""Id"" SERIAL PRIMARY KEY,
                ""ServerId"" INTEGER NOT NULL,
                ""UserId"" INTEGER NOT NULL,
                ""ActorId"" INTEGER NOT NULL,
                ""ExpiresAt"" TIMESTAMPTZ NOT NULL,
                ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW()
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""WordFilters"" (
                ""Id"" SERIAL PRIMARY KEY,
                ""ServerId"" INTEGER NOT NULL,
                ""Pattern"" TEXT NOT NULL,
                ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW()
            )");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Servers' AND column_name='IconData'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"IconData\" BYTEA");
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Servers' AND column_name='IconMimeType'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"IconMimeType\" TEXT");
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Users' AND column_name='LastSeenAt'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" ADD COLUMN \"LastSeenAt\" TIMESTAMP");
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Users' AND column_name='TotalOnlineSeconds'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" ADD COLUMN \"TotalOnlineSeconds\" BIGINT NOT NULL DEFAULT 0");
    }
    else
    {

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name='Source'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Messages ADD COLUMN Source INTEGER NOT NULL DEFAULT 0");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name='AttachmentUrl'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Messages ADD COLUMN AttachmentUrl TEXT");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name='IsDeleted'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Messages ADD COLUMN IsDeleted INTEGER NOT NULL DEFAULT 0");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name='EditedAt'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Messages ADD COLUMN EditedAt TEXT");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name='AvatarUrl'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN AvatarUrl TEXT");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name='ReplyToId'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Messages ADD COLUMN ReplyToId INTEGER");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Servers') WHERE name='InviteCode'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Servers ADD COLUMN InviteCode TEXT");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name='AttachmentFileName'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Messages ADD COLUMN AttachmentFileName TEXT");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Channels') WHERE name='CategoryId'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Channels ADD COLUMN CategoryId INTEGER");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name='Bio'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN Bio TEXT");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Channels') WHERE name='SlowmodeSeconds'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Channels ADD COLUMN SlowmodeSeconds INTEGER NOT NULL DEFAULT 0");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name='ThreadId'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Messages ADD COLUMN ThreadId INTEGER");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ServerMembers') WHERE name='MutedUntil'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE ServerMembers ADD COLUMN MutedUntil TEXT");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS TempBans (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ServerId INTEGER NOT NULL,
                UserId INTEGER NOT NULL,
                ActorId INTEGER NOT NULL,
                ExpiresAt TEXT NOT NULL,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS WordFilters (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ServerId INTEGER NOT NULL,
                Pattern TEXT NOT NULL,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            )");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Servers') WHERE name='IconData'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Servers ADD COLUMN IconData BLOB");
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Servers') WHERE name='IconMimeType'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Servers ADD COLUMN IconMimeType TEXT");
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name='LastSeenAt'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN LastSeenAt TEXT");
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name='TotalOnlineSeconds'";
        if ((long)checkCmd.ExecuteScalar()! == 0)
            ctx.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN TotalOnlineSeconds INTEGER NOT NULL DEFAULT 0");
    }

    // MessageReactions table (added for reactions feature)
    if (isPostgres)
    {
        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""MessageReactions"" (
                ""Id"" SERIAL PRIMARY KEY,
                ""MessageId"" INTEGER NOT NULL REFERENCES ""Messages""(""Id"") ON DELETE CASCADE,
                ""UserId"" INTEGER NOT NULL,
                ""Username"" TEXT NOT NULL,
                ""Emoji"" TEXT NOT NULL,
                UNIQUE(""MessageId"", ""UserId"", ""Emoji"")
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""AuditLogs"" (
                ""Id"" SERIAL PRIMARY KEY,
                ""ServerId"" INTEGER NOT NULL,
                ""ActorId"" INTEGER NOT NULL,
                ""ActorUsername"" TEXT NOT NULL,
                ""Action"" TEXT NOT NULL,
                ""TargetId"" INTEGER,
                ""TargetUsername"" TEXT,
                ""Detail"" TEXT,
                ""CreatedAt"" TIMESTAMP NOT NULL DEFAULT NOW()
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""ChannelPermissions"" (
                ""ChannelId"" INTEGER PRIMARY KEY NOT NULL,
                ""MinRoleToRead"" INTEGER NOT NULL DEFAULT 0,
                ""MinRoleToWrite"" INTEGER NOT NULL DEFAULT 0
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""DirectConversations"" (
                ""Id"" SERIAL PRIMARY KEY,
                ""User1Id"" INTEGER NOT NULL,
                ""User2Id"" INTEGER NOT NULL,
                ""CreatedAt"" TIMESTAMP NOT NULL DEFAULT NOW(),
                UNIQUE(""User1Id"", ""User2Id"")
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""DirectMessages"" (
                ""Id"" SERIAL PRIMARY KEY,
                ""ConversationId"" INTEGER NOT NULL REFERENCES ""DirectConversations""(""Id"") ON DELETE CASCADE,
                ""AuthorId"" INTEGER NOT NULL,
                ""Content"" TEXT NOT NULL DEFAULT '',
                ""CreatedAt"" TIMESTAMP NOT NULL DEFAULT NOW(),
                ""IsDeleted"" BOOLEAN NOT NULL DEFAULT FALSE,
                ""AttachmentUrl"" TEXT,
                ""AttachmentFileName"" TEXT
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""DirectConversationReads"" (
                ""ConversationId"" INTEGER NOT NULL REFERENCES ""DirectConversations""(""Id"") ON DELETE CASCADE,
                ""UserId"" INTEGER NOT NULL REFERENCES ""Users""(""Id"") ON DELETE CASCADE,
                ""LastReadAt"" TIMESTAMP NOT NULL DEFAULT NOW(),
                PRIMARY KEY (""ConversationId"", ""UserId"")
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""PinnedMessages"" (
                ""Id"" SERIAL PRIMARY KEY,
                ""MessageId"" INTEGER NOT NULL UNIQUE REFERENCES ""Messages""(""Id"") ON DELETE CASCADE,
                ""ChannelId"" INTEGER NOT NULL,
                ""PinnedById"" INTEGER NOT NULL,
                ""PinnedAt"" TIMESTAMP NOT NULL DEFAULT NOW()
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""PinnedDirectMessages"" (
                ""Id"" SERIAL PRIMARY KEY,
                ""DirectMessageId"" INTEGER NOT NULL UNIQUE REFERENCES ""DirectMessages""(""Id"") ON DELETE CASCADE,
                ""ConversationId"" INTEGER NOT NULL,
                ""PinnedById"" INTEGER NOT NULL,
                ""PinnedAt"" TIMESTAMP NOT NULL DEFAULT NOW()
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""Categories"" (
                ""Id"" SERIAL PRIMARY KEY,
                ""ServerId"" INTEGER NOT NULL REFERENCES ""GuildServers""(""Id"") ON DELETE CASCADE,
                ""Name"" TEXT NOT NULL,
                ""Position"" INTEGER NOT NULL DEFAULT 0
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""UserBlocks"" (
                ""BlockerId"" INTEGER NOT NULL REFERENCES ""Users""(""Id"") ON DELETE CASCADE,
                ""BlockedId"" INTEGER NOT NULL REFERENCES ""Users""(""Id"") ON DELETE CASCADE,
                ""CreatedAt"" TIMESTAMP NOT NULL DEFAULT NOW(),
                PRIMARY KEY (""BlockerId"", ""BlockedId"")
            )");
    }
    else
    {
        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS MessageReactions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MessageId INTEGER NOT NULL REFERENCES Messages(Id) ON DELETE CASCADE,
                UserId INTEGER NOT NULL,
                Username TEXT NOT NULL,
                Emoji TEXT NOT NULL,
                UNIQUE(MessageId, UserId, Emoji)
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS AuditLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ServerId INTEGER NOT NULL,
                ActorId INTEGER NOT NULL,
                ActorUsername TEXT NOT NULL,
                Action TEXT NOT NULL,
                TargetId INTEGER,
                TargetUsername TEXT,
                Detail TEXT,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ChannelPermissions (
                ChannelId INTEGER PRIMARY KEY NOT NULL,
                MinRoleToRead INTEGER NOT NULL DEFAULT 0,
                MinRoleToWrite INTEGER NOT NULL DEFAULT 0
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS DirectConversations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                User1Id INTEGER NOT NULL,
                User2Id INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(User1Id, User2Id)
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS DirectMessages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ConversationId INTEGER NOT NULL REFERENCES DirectConversations(Id) ON DELETE CASCADE,
                AuthorId INTEGER NOT NULL,
                Content TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                AttachmentUrl TEXT,
                AttachmentFileName TEXT
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS DirectConversationReads (
                ConversationId INTEGER NOT NULL REFERENCES DirectConversations(Id) ON DELETE CASCADE,
                UserId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                LastReadAt TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (ConversationId, UserId)
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS PinnedMessages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MessageId INTEGER NOT NULL UNIQUE REFERENCES Messages(Id) ON DELETE CASCADE,
                ChannelId INTEGER NOT NULL,
                PinnedById INTEGER NOT NULL,
                PinnedAt TEXT NOT NULL DEFAULT (datetime('now'))
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS PinnedDirectMessages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DirectMessageId INTEGER NOT NULL UNIQUE REFERENCES DirectMessages(Id) ON DELETE CASCADE,
                ConversationId INTEGER NOT NULL,
                PinnedById INTEGER NOT NULL,
                PinnedAt TEXT NOT NULL DEFAULT (datetime('now'))
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS Categories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ServerId INTEGER NOT NULL REFERENCES GuildServers(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL,
                Position INTEGER NOT NULL DEFAULT 0
            )");

        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS UserBlocks (
                BlockerId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                BlockedId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (BlockerId, BlockedId)
            )");
    }

    if (isPostgres)
    {
        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""UserChannelNotifs"" (
                ""UserId"" INTEGER NOT NULL,
                ""ChannelId"" INTEGER NOT NULL,
                ""Level"" INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (""UserId"", ""ChannelId"")
            )");
        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""UserServerNotifs"" (
                ""UserId"" INTEGER NOT NULL,
                ""ServerId"" INTEGER NOT NULL,
                ""Level"" INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (""UserId"", ""ServerId"")
            )");
    }
    else
    {
        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS UserChannelNotifs (
                UserId INTEGER NOT NULL,
                ChannelId INTEGER NOT NULL,
                Level INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (UserId, ChannelId)
            )");
        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS UserServerNotifs (
                UserId INTEGER NOT NULL,
                ServerId INTEGER NOT NULL,
                Level INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (UserId, ServerId)
            )");
    }

    // New tables: password reset, sessions, warnings, webhooks, group DMs, column additions
    if (isPostgres)
    {
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""PasswordResetTokens"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" INTEGER NOT NULL, ""Token"" TEXT NOT NULL UNIQUE, ""ExpiresAt"" TIMESTAMPTZ NOT NULL, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""IsUsed"" BOOLEAN NOT NULL DEFAULT FALSE)");
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""UserSessions"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" INTEGER NOT NULL, ""TokenHash"" TEXT NOT NULL UNIQUE, ""IpAddress"" TEXT, ""UserAgent"" TEXT, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""LastSeenAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""RevokedAt"" TIMESTAMPTZ)");
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""UserWarnings"" (""Id"" SERIAL PRIMARY KEY, ""ServerId"" INTEGER NOT NULL, ""UserId"" INTEGER NOT NULL, ""ActorId"" INTEGER NOT NULL, ""ActorUsername"" TEXT NOT NULL, ""Reason"" TEXT NOT NULL, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())");
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""Webhooks"" (""Id"" SERIAL PRIMARY KEY, ""ServerId"" INTEGER NOT NULL, ""Name"" TEXT NOT NULL, ""Url"" TEXT NOT NULL, ""Events"" TEXT NOT NULL DEFAULT 'message,member_join,member_leave', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""CreatedById"" INTEGER NOT NULL)");
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""GroupConversations"" (""Id"" SERIAL PRIMARY KEY, ""Name"" TEXT NOT NULL, ""CreatedById"" INTEGER NOT NULL, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())");
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""GroupConversationMembers"" (""GroupConversationId"" INTEGER NOT NULL REFERENCES ""GroupConversations""(""Id"") ON DELETE CASCADE, ""UserId"" INTEGER NOT NULL, ""JoinedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), PRIMARY KEY (""GroupConversationId"", ""UserId""))");
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""GroupMessages"" (""Id"" SERIAL PRIMARY KEY, ""GroupConversationId"" INTEGER NOT NULL REFERENCES ""GroupConversations""(""Id"") ON DELETE CASCADE, ""AuthorId"" INTEGER NOT NULL, ""Content"" TEXT NOT NULL DEFAULT '', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""IsDeleted"" BOOLEAN NOT NULL DEFAULT FALSE)");

        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Channels' AND column_name='Topic'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Channels\" ADD COLUMN \"Topic\" TEXT");
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Channels' AND column_name='IsNsfw'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Channels\" ADD COLUMN \"IsNsfw\" BOOLEAN NOT NULL DEFAULT FALSE");
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Channels' AND column_name='IsDefault'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Channels\" ADD COLUMN \"IsDefault\" BOOLEAN NOT NULL DEFAULT FALSE");
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Servers' AND column_name='VanityCode'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"VanityCode\" TEXT");
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='Servers' AND column_name='MinRoleToMentionEveryone'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE \"Servers\" ADD COLUMN \"MinRoleToMentionEveryone\" INTEGER NOT NULL DEFAULT 2");
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='WordFilters' AND column_name='Severity'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE \"WordFilters\" ADD COLUMN \"Severity\" INTEGER NOT NULL DEFAULT 1");
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='WordFilters' AND column_name='CaseSensitive'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE \"WordFilters\" ADD COLUMN \"CaseSensitive\" BOOLEAN NOT NULL DEFAULT FALSE");
        ctx.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Servers_VanityCode ON \"Servers\" (\"VanityCode\") WHERE \"VanityCode\" IS NOT NULL");
    }
    else
    {
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS PasswordResetTokens (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER NOT NULL, Token TEXT NOT NULL UNIQUE, ExpiresAt TEXT NOT NULL, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), IsUsed INTEGER NOT NULL DEFAULT 0)");
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS UserSessions (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER NOT NULL, TokenHash TEXT NOT NULL UNIQUE, IpAddress TEXT, UserAgent TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), LastSeenAt TEXT NOT NULL DEFAULT (datetime('now')), RevokedAt TEXT)");
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS UserWarnings (Id INTEGER PRIMARY KEY AUTOINCREMENT, ServerId INTEGER NOT NULL, UserId INTEGER NOT NULL, ActorId INTEGER NOT NULL, ActorUsername TEXT NOT NULL, Reason TEXT NOT NULL, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')))");
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS Webhooks (Id INTEGER PRIMARY KEY AUTOINCREMENT, ServerId INTEGER NOT NULL, Name TEXT NOT NULL, Url TEXT NOT NULL, Events TEXT NOT NULL DEFAULT 'message,member_join,member_leave', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), CreatedById INTEGER NOT NULL)");
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS GroupConversations (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, CreatedById INTEGER NOT NULL, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')))");
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS GroupConversationMembers (GroupConversationId INTEGER NOT NULL REFERENCES GroupConversations(Id) ON DELETE CASCADE, UserId INTEGER NOT NULL, JoinedAt TEXT NOT NULL DEFAULT (datetime('now')), PRIMARY KEY (GroupConversationId, UserId))");
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS GroupMessages (Id INTEGER PRIMARY KEY AUTOINCREMENT, GroupConversationId INTEGER NOT NULL REFERENCES GroupConversations(Id) ON DELETE CASCADE, AuthorId INTEGER NOT NULL, Content TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), IsDeleted INTEGER NOT NULL DEFAULT 0)");

        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Channels') WHERE name='Topic'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE Channels ADD COLUMN Topic TEXT");
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Channels') WHERE name='IsNsfw'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE Channels ADD COLUMN IsNsfw INTEGER NOT NULL DEFAULT 0");
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Channels') WHERE name='IsDefault'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE Channels ADD COLUMN IsDefault INTEGER NOT NULL DEFAULT 0");
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Servers') WHERE name='VanityCode'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE Servers ADD COLUMN VanityCode TEXT");
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Servers') WHERE name='MinRoleToMentionEveryone'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE Servers ADD COLUMN MinRoleToMentionEveryone INTEGER NOT NULL DEFAULT 2");
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WordFilters') WHERE name='Severity'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE WordFilters ADD COLUMN Severity INTEGER NOT NULL DEFAULT 1");
        checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WordFilters') WHERE name='CaseSensitive'";
        if ((long)checkCmd.ExecuteScalar()! == 0) ctx.Database.ExecuteSqlRaw("ALTER TABLE WordFilters ADD COLUMN CaseSensitive INTEGER NOT NULL DEFAULT 0");
        ctx.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Servers_VanityCode ON Servers (VanityCode) WHERE VanityCode IS NOT NULL");
    }

    // Monotonic counter table for never-reused channel ids (added for the channel-recycling fix).
    if (isPostgres)
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""AppSequences"" (""Name"" TEXT PRIMARY KEY, ""Value"" BIGINT NOT NULL)");
    else
        ctx.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS AppSequences (Name TEXT PRIMARY KEY, Value INTEGER NOT NULL)");

    // OAuth external logins (added for Google sign-in).
    if (isPostgres)
    {
        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""ExternalLogins"" (
                ""Id"" SERIAL PRIMARY KEY,
                ""UserId"" INTEGER NOT NULL,
                ""Provider"" TEXT NOT NULL,
                ""ProviderUserId"" TEXT NOT NULL,
                ""Email"" TEXT NOT NULL DEFAULT '',
                ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW()
            )");
        ctx.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ExternalLogins_Provider_ProviderUserId"" ON ""ExternalLogins"" (""Provider"", ""ProviderUserId"")");
    }
    else
    {
        ctx.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ExternalLogins (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                Provider TEXT NOT NULL,
                ProviderUserId TEXT NOT NULL,
                Email TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            )");
        ctx.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS IX_ExternalLogins_Provider_ProviderUserId ON ExternalLogins (Provider, ProviderUserId)");
    }

    // Seed bot user
    var botUser = ctx.Users.FirstOrDefault(u => u.Username == FatGuysSpeak.Server.Services.BotService.BotUsername);
    if (botUser is null)
    {
        botUser = new FatGuysSpeak.Server.Models.User
        {
            Username     = FatGuysSpeak.Server.Services.BotService.BotUsername,
            Email        = "bot@system.local",
            PasswordHash = "!",
            Status       = FatGuysSpeak.Shared.UserStatus.Online,
        };
        ctx.Users.Add(botUser);
        ctx.SaveChanges();
    }
    FatGuysSpeak.Server.Services.BotService.BotUserId = botUser.Id;

    if (!ctx.Servers.Any())
    {
        var server = new FatGuysSpeak.Server.Models.GuildServer { Name = "FatGuysSpeak", OwnerId = 0 };
        ctx.Servers.Add(server);
        ctx.SaveChanges();
        ctx.Channels.AddRange(
            new FatGuysSpeak.Server.Models.Channel { Name = "lobby",          Type = FatGuysSpeak.Shared.ChannelType.Text, ServerId = server.Id, Position = 0, IsDefault = true },
            new FatGuysSpeak.Server.Models.Channel { Name = "angry-fat-guys", Type = FatGuysSpeak.Shared.ChannelType.Text, ServerId = server.Id, Position = 1 }
        );
        ctx.SaveChanges();
    }
    else
    {
        var server = ctx.Servers.First();
        // Rename general → lobby if the old name still exists
        var generalChannel = ctx.Channels.FirstOrDefault(c => c.ServerId == server.Id && c.Name == "general");
        if (generalChannel is not null)
        {
            generalChannel.Name = "lobby";
            ctx.SaveChanges();
        }
        // Add angry-fat-guys channel if it was added after the initial seed
        if (!ctx.Channels.Any(c => c.ServerId == server.Id && c.Name == "angry-fat-guys"))
        {
            ctx.Channels.Add(new FatGuysSpeak.Server.Models.Channel { Name = "angry-fat-guys", Type = FatGuysSpeak.Shared.ChannelType.Text, ServerId = server.Id, Position = 1 });
            ctx.SaveChanges();
        }
    }

    // Backfill: every server needs exactly one default channel. If none is flagged
    // (e.g. an existing database), mark the lowest-position channel as the default.
    foreach (var srv in ctx.Servers.ToList())
    {
        if (!ctx.Channels.Any(c => c.ServerId == srv.Id && c.IsDefault))
        {
            var primary = ctx.Channels.Where(c => c.ServerId == srv.Id).OrderBy(c => c.Position).FirstOrDefault();
            if (primary is not null) primary.IsDefault = true;
        }
    }
    ctx.SaveChanges();

    var blacklist = scope.ServiceProvider.GetRequiredService<SessionBlacklistService>();
    await blacklist.RehydrateAsync(ctx);
}

// Validate dashboard credentials are configured before accepting traffic
var dashUser = app.Configuration["Dashboard:Username"];
var dashPass = app.Configuration["Dashboard:Password"];
if (string.IsNullOrWhiteSpace(dashUser) || string.IsNullOrWhiteSpace(dashPass))
    throw new InvalidOperationException("Dashboard credentials are not configured. Set Dashboard:Username and Dashboard:Password in appsettings.");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
// Forward proxy headers ONLY when actually behind a cloud proxy (Railway sets DATABASE_URL,
// and strips/overwrites client-supplied X-Forwarded-For at its edge). When running directly
// (local/self-hosted), do NOT trust forwarded headers — otherwise any client could spoof its
// IP, defeating per-IP rate limits and the dashboard's loopback (IsLocal) check.
if (!string.IsNullOrEmpty(databaseUrl))
{
    var forwardedOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    };
    forwardedOptions.KnownNetworks.Clear();
    forwardedOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedOptions);
}
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    // CSP for first-party HTML (dashboard/login). Skip Swagger UI, which needs inline script.
    if (!ctx.Request.Path.StartsWithSegments("/swagger"))
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
            "script-src 'self'; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'";
    if (ctx.Request.IsHttps)
        ctx.Response.Headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains";
    await next();
});
app.UseCors();
app.UseRateLimiter();

// Serve wwwroot (dashboard.js + self-hosted Chart.js) so the dashboard's JS loads
// under the strict CSP (script-src 'self') instead of inline scripts / a CDN.
app.UseStaticFiles();

var uploadsPath = Path.Combine(app.Environment.ContentRootPath, "uploads");
Directory.CreateDirectory(uploadsPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsPath),
    RequestPath = "/uploads",
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

#if WINDOWS
// Start ASP.NET Core in the background, then open the dashboard as a WPF window.
// Closing the window also stops the server.
await app.StartAsync();

var tcs = new TaskCompletionSource();
var wpfThread = new Thread(() =>
{
    var wpfApp = new System.Windows.Application();
    wpfApp.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
    wpfApp.Run(new FatGuysSpeak.Server.Dashboard.DashboardWindow());
    tcs.SetResult();
});
wpfThread.SetApartmentState(ApartmentState.STA);
wpfThread.IsBackground = false;
wpfThread.Start();

await tcs.Task;
await app.StopAsync();
#else
app.Run();
#endif
