using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TeaPremium.Application.Media;

namespace TeaPremium.Infrastructure.Media;

// Decode the entire payload, not merely a magic-byte probe. No network demuxers, shell or client paths.
public sealed class FfmpegContentValidator(MediaStorageOptions options) : IMediaContentValidator
{
    public async Task<MediaDimensions?> ValidateAsync(StoredMedia content, CancellationToken ct)
    {
        var format = Format(content);
        if (format is null) return null;
        var directory = Path.Combine(Path.GetTempPath(), "tp-media-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = Path.Combine(directory, "input");
        try
        {
            await File.WriteAllBytesAsync(file, content.Bytes, ct);
            var input = new[] { "-v", "error", "-max_alloc", "67108864", "-max_pixels", "40000000", "-protocol_whitelist", "file", "-f", format, "-threads", "1", "-i", file };
            var probe = await RunAsync(options.FfprobePath, [.. input, "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height", "-of", "json"], ct);
            if (probe is null) return null;
            using var doc = JsonDocument.Parse(probe);
            var streams = doc.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() != 1) return null;
            var stream = streams[0];
            if (!stream.TryGetProperty("width", out var width) || !stream.TryGetProperty("height", out var height)) return null;
            var w = width.GetInt32(); var h = height.GetInt32();
            if (w < 1 || h < 1 || (long)w * h > 40_000_000) return null; // Decoder memory budget.
            var codec = stream.GetProperty("codec_name").GetString();
            if (content.MimeType.StartsWith("image/", StringComparison.Ordinal) && codec != (content.MimeType switch
            { "image/jpeg" => "mjpeg", "image/png" => "png", "image/webp" => "webp", "image/avif" => "av1", _ => "" })) return null;
            var decoded = await RunAsync(options.FfmpegPath,
                ["-nostdin", "-xerror", "-err_detect", "explode", .. input, "-map", "0:v:0", "-map", "0:a?", "-sn", "-dn", "-threads", "1", "-f", "null", "-"], ct);
            if (decoded is null) return null;
            return content.MimeType.StartsWith("image/", StringComparison.Ordinal) ? new(w, h) : new(null, null);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string? Format(StoredMedia content)
    {
        ReadOnlySpan<byte> bytes = content.Bytes;
        return content.MimeType switch
        {
            "image/jpeg" when bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[^2] == 255 && bytes[^1] == 217 => "jpeg_pipe",
            "image/png" when bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && bytes.Length >= 20
                && bytes[^8..].SequenceEqual(new byte[] { 73, 69, 78, 68, 174, 66, 96, 130 }) => "png_pipe",
            "image/webp" when bytes.Length >= 20 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]) == bytes.Length - 8 => "webp_pipe",
            "image/avif" when IsoBoxes(bytes, "avif", "avis") => "mov",
            "video/mp4" when IsoBoxes(bytes, "isom", "iso2", "iso4", "iso5", "iso6", "mp41", "mp42", "avc1", "M4V ", "MSNV", "dash") => "mov",
            "video/quicktime" when IsoBoxes(bytes, "qt  ") => "mov",
            _ => null
        };
    }

    private static bool IsoBoxes(ReadOnlySpan<byte> bytes, params string[] brands)
    {
        if (bytes.Length < 16 || !bytes[4..8].SequenceEqual("ftyp"u8) || !brands.Contains(Encoding.ASCII.GetString(bytes[8..12]), StringComparer.Ordinal)) return false;
        long offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8) return false;
            var box = bytes[(int)offset..];
            long size = BinaryPrimitives.ReadUInt32BigEndian(box);
            var header = 8;
            if (size == 1)
            {
                if (box.Length < 16) return false;
                var large = BinaryPrimitives.ReadUInt64BigEndian(box[8..]);
                if (large > int.MaxValue) return false;
                size = (long)large; header = 16;
            }
            if (size == 0) size = box.Length;
            if (size < header || size > box.Length) return false;
            offset += size;
        }
        return offset == bytes.Length;
    }

    private static async Task<string?> RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new MediaStorageException(); }
        var stdout = DrainAsync(process.StandardOutput, 8192);
        var stderr = DrainAsync(process.StandardError, 0);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            await stderr;
            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None); await stdout; await stderr;
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }
    private static async Task<string> DrainAsync(StreamReader reader, int keep)
    {
        var text = new StringBuilder(); var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
            if (text.Length < keep) text.Append(buffer, 0, Math.Min(read, keep - text.Length));
        return text.ToString();
    }
}
