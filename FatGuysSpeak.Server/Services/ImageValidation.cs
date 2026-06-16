namespace FatGuysSpeak.Server.Services;

/// <summary>
/// Validates that an uploaded image's leading bytes (magic number) match its claimed
/// extension, so a non-image (or polyglot) can't be stored under an image name.
/// </summary>
public static class ImageValidation
{
    public static bool MatchesExtension(ReadOnlySpan<byte> head, string ext) => ext switch
    {
        ".jpg" or ".jpeg" => head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF,
        ".png" => head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
                  && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A,
        ".gif" => head.Length >= 6 && head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F'
                  && head[3] == (byte)'8' && (head[4] == (byte)'7' || head[4] == (byte)'9') && head[5] == (byte)'a',
        ".webp" => head.Length >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
                   && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P',
        _ => false
    };

    /// <summary>Reads the first 12 bytes of the form file and checks them against the extension.</summary>
    public static async Task<bool> IsValidImageAsync(IFormFile file, string ext, CancellationToken ct = default)
    {
        var buf = new byte[12];
        await using var probe = file.OpenReadStream();
        int read = 0;
        while (read < buf.Length)
        {
            int n = await probe.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        return MatchesExtension(buf.AsSpan(0, read), ext);
    }
}
