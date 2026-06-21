using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace FatGuysSpeak.Tests.Server;

public class SoundboardTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly SoundsController _ctrl;
    private readonly string _contentRoot;
    private GuildServer _server = null!;
    private User _owner = null!;
    private User _member = null!;

    public SoundboardTests()
    {
        _testDb = new TestDb();
        _contentRoot = Path.Combine(Path.GetTempPath(), "fgs_sound_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(_contentRoot);
        _ctrl = new SoundsController(_testDb.Db, TestHelpers.NullTts(), env.Object);
    }

    public void Dispose()
    {
        _testDb.Dispose();
        try { Directory.Delete(_contentRoot, true); } catch { }
    }

    private async Task SeedAsync()
    {
        (_server, _owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "owner");
        _member = new User { Username = "member", Email = "m@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(_member);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = _member.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();
    }

    // Build a minimal valid 16-bit PCM WAV.
    private static byte[] BuildWav(short[] samples, int sampleRate, int channels = 1)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = samples.Length * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);                    // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);   // byte rate
        w.Write((short)(channels * 2));       // block align
        w.Write((short)16);                   // bits per sample
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }

    private static IFormFile FormFile(byte[] content, string fileName) =>
        new FormFile(new MemoryStream(content), 0, content.Length, "file", fileName)
        { Headers = new HeaderDictionary(), ContentType = "audio/wav" };

    // ── WavAudio decode ─────────────────────────────────────────────────────────

    [Fact]
    public void WavAudio_DecodesMonoPcm()
    {
        var wav = BuildWav([100, -200, 300, -400], 8000);
        var decoded = WavAudio.DecodeToMono(wav);
        Assert.NotNull(decoded);
        Assert.Equal(8000, decoded!.Value.sampleRate);
        Assert.Equal(new short[] { 100, -200, 300, -400 }, decoded.Value.samples);
    }

    [Fact]
    public void WavAudio_DownmixesStereoToMono()
    {
        // interleaved L,R,L,R → averaged: (100+300)/2=200, (-100+ -300)/2=-200
        var wav = BuildWav([100, 300, -100, -300], 44100, channels: 2);
        var decoded = WavAudio.DecodeToMono(wav);
        Assert.NotNull(decoded);
        Assert.Equal(new short[] { 200, -200 }, decoded!.Value.samples);
    }

    [Fact]
    public void WavAudio_RejectsGarbage()
    {
        Assert.Null(WavAudio.DecodeToMono(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Null(WavAudio.DecodeToMono(System.Text.Encoding.ASCII.GetBytes("not a wav file at all, just text here padding padding")));
    }

    // ── Upload / list / delete ──────────────────────────────────────────────────

    [Fact]
    public async Task Upload_ValidWav_CreatesClipAndFile()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.Upload(_server.Id, "airhorn", "📯", FormFile(BuildWav([1, 2, 3, 4], 22050), "airhorn.wav"));

        var dto = Assert.IsType<OkObjectResult>(result.Result).Value as SoundClipDto;
        Assert.NotNull(dto);
        Assert.Equal("airhorn", dto!.Name);
        var clip = _testDb.Db.SoundClips.Single();
        Assert.True(File.Exists(Path.Combine(_contentRoot, "uploads", clip.FileName)));
    }

    [Fact]
    public async Task Upload_NonWavExtension_BadRequest()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.Upload(_server.Id, "nope", null, FormFile(BuildWav([1], 8000), "clip.mp3"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Upload_GarbageBytes_BadRequest()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.Upload(_server.Id, "broken", null, FormFile(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }, "broken.wav"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Upload_NonMember_Forbidden()
    {
        await SeedAsync();
        var outsider = new User { Username = "outsider", Email = "o@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(outsider);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_ctrl, outsider.Id, outsider.Username);

        var result = await _ctrl.Upload(_server.Id, "x", null, FormFile(BuildWav([1], 8000), "x.wav"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task List_ReturnsServerClips()
    {
        await SeedAsync();
        _testDb.Db.SoundClips.AddRange(
            new SoundClip { ServerId = _server.Id, Name = "boo", UploadedById = _owner.Id, FileName = "a.wav" },
            new SoundClip { ServerId = _server.Id, Name = "yay", UploadedById = _owner.Id, FileName = "b.wav" });
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.List(_server.Id);

        Assert.Equal(2, result.Value!.Count);
    }

    [Fact]
    public async Task Delete_ByUploader_Removes()
    {
        await SeedAsync();
        var clip = new SoundClip { ServerId = _server.Id, Name = "mine", UploadedById = _member.Id, FileName = "m.wav" };
        _testDb.Db.SoundClips.Add(clip);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.Delete(clip.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(_testDb.Db.SoundClips.Any());
    }

    [Fact]
    public async Task Delete_ByOtherMember_Forbidden()
    {
        await SeedAsync();
        var clip = new SoundClip { ServerId = _server.Id, Name = "owners", UploadedById = _owner.Id, FileName = "o.wav" };
        _testDb.Db.SoundClips.Add(clip);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);   // a plain member who didn't upload it

        var result = await _ctrl.Delete(clip.Id);

        Assert.IsType<ForbidResult>(result);
        Assert.True(_testDb.Db.SoundClips.Any());
    }

    [Fact]
    public async Task Delete_ByAdmin_Removes()
    {
        await SeedAsync();
        var clip = new SoundClip { ServerId = _server.Id, Name = "members", UploadedById = _member.Id, FileName = "m.wav" };
        _testDb.Db.SoundClips.Add(clip);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);     // owner is Admin

        var result = await _ctrl.Delete(clip.Id);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Play_NotInVoice_BadRequest()
    {
        await SeedAsync();
        var clip = new SoundClip { ServerId = _server.Id, Name = "x", UploadedById = _owner.Id, FileName = "x.wav" };
        _testDb.Db.SoundClips.Add(clip);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);   // not in any voice channel

        var result = await _ctrl.Play(clip.Id);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
