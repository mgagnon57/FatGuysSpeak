using System.Text;
using System.Threading.RateLimiting;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
builder.Services.AddSingleton<FatGuysSpeak.Server.Services.ServerMetricsService>();
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

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
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
            }
        };
    });

// Rate limiters: auth (per-IP) + messages (per-user)
builder.Services.AddRateLimiter(opt =>
{
    opt.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = 10;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiter.QueueLimit = 0;
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

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddHttpClient("preview", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; FatGuysSpeak/1.0)");
    c.Timeout = TimeSpan.FromSeconds(8);
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
            new FatGuysSpeak.Server.Models.Channel { Name = "lobby",          Type = FatGuysSpeak.Shared.ChannelType.Text, ServerId = server.Id, Position = 0 },
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
}

app.UseCors();
app.UseRateLimiter();

var uploadsPath = Path.Combine(app.Environment.ContentRootPath, "uploads");
Directory.CreateDirectory(uploadsPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsPath),
    RequestPath = "/uploads"
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
