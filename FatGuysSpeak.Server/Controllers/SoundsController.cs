using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Controllers;

[ApiController]
[Authorize]
public class SoundsController(AppDbContext db, TtsService tts, IWebHostEnvironment env) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private const int MaxBytes = 2 * 1024 * 1024;     // 2 MB
    private const int MaxPlaySeconds = 8;             // clips are short by design

    [HttpGet("api/servers/{serverId}/sounds")]
    public async Task<ActionResult<List<SoundClipDto>>> List(int serverId)
    {
        if (!await db.ServerMembers.AnyAsync(m => m.ServerId == serverId && m.UserId == UserId)) return Forbid();
        return await db.SoundClips.Where(s => s.ServerId == serverId)
            .OrderBy(s => s.Name)
            .Select(s => new SoundClipDto(s.Id, s.ServerId, s.Name, s.Emoji)).ToListAsync();
    }

    [HttpPost("api/servers/{serverId}/sounds")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("messages")]
    [RequestSizeLimit(MaxBytes + 4096)]
    public async Task<ActionResult<SoundClipDto>> Upload(int serverId, [FromForm] string name, [FromForm] string? emoji, IFormFile file)
    {
        if (!await db.ServerMembers.AnyAsync(m => m.ServerId == serverId && m.UserId == UserId)) return Forbid();

        name = (name ?? "").Trim();
        if (name.Length == 0) return BadRequest("Sound needs a name.");
        if (name.Length > 40) name = name[..40];
        if (file is null || file.Length == 0) return BadRequest("No file provided.");
        if (file.Length > MaxBytes) return BadRequest("File too large. Maximum is 2 MB.");
        if (Path.GetExtension(file.FileName).ToLowerInvariant() != ".wav")
            return BadRequest("Only 16-bit PCM .wav files are supported.");

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        if (WavAudio.DecodeToMono(bytes) is null)
            return BadRequest("That file isn't a readable 16-bit PCM WAV.");

        var uploadsDir = Path.Combine(env.ContentRootPath, "uploads");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"sound_{serverId}_{Guid.NewGuid():N}.wav";
        await System.IO.File.WriteAllBytesAsync(Path.Combine(uploadsDir, fileName), bytes);

        var clip = new SoundClip
        {
            ServerId     = serverId,
            Name         = name,
            Emoji        = string.IsNullOrWhiteSpace(emoji) ? null : emoji!.Trim(),
            UploadedById = UserId,
            FileName     = fileName,
        };
        db.SoundClips.Add(clip);
        await db.SaveChangesAsync();
        return Ok(new SoundClipDto(clip.Id, clip.ServerId, clip.Name, clip.Emoji));
    }

    [HttpDelete("api/sounds/{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var clip = await db.SoundClips.FindAsync(id);
        if (clip is null) return NotFound();
        var member = await db.ServerMembers.FirstOrDefaultAsync(m => m.ServerId == clip.ServerId && m.UserId == UserId);
        if (member is null) return Forbid();
        if (clip.UploadedById != UserId && member.Role < ServerRole.Admin) return Forbid();   // uploader or admin

        db.SoundClips.Remove(clip);
        await db.SaveChangesAsync();
        try
        {
            var p = Path.Combine(env.ContentRootPath, "uploads", clip.FileName);
            if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
        }
        catch { /* best-effort file cleanup */ }
        return NoContent();
    }

    // Fire the clip into whatever voice channel the caller is currently sitting in.
    [HttpPost("api/sounds/{id}/play")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("messages")]
    public async Task<IActionResult> Play(int id)
    {
        var clip = await db.SoundClips.FindAsync(id);
        if (clip is null) return NotFound();
        if (!await db.ServerMembers.AnyAsync(m => m.ServerId == clip.ServerId && m.UserId == UserId)) return Forbid();
        if (!ChatHub.VoiceChannelSnapshot.TryGetValue(UserId, out var channelId))
            return BadRequest("Join a voice channel first.");

        var path = Path.Combine(env.ContentRootPath, "uploads", clip.FileName);
        if (!System.IO.File.Exists(path)) return NotFound();

        var decoded = WavAudio.DecodeToMono(await System.IO.File.ReadAllBytesAsync(path));
        if (decoded is null) return BadRequest("Sound file is unreadable.");

        var samples48 = TtsService.Resample(decoded.Value.samples, decoded.Value.sampleRate, TtsService.VoiceSampleRate);
        var maxSamples = MaxPlaySeconds * TtsService.VoiceSampleRate;
        if (samples48.Length > maxSamples) samples48 = samples48[..maxSamples];

        _ = tts.PlaySamples48Async(channelId, samples48);
        return Accepted();
    }
}
