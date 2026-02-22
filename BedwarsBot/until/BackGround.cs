using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace BedwarsBot;

public sealed class BackGround
{
    private const int MaxBackgroundDimension = 1920;
    private const int MaxBackgroundBytes = 2_500_000;
    private readonly BotDataStore _store;
    private readonly HttpClient _httpClient;

    public BackGround(BotDataStore store, HttpClient httpClient)
    {
        _store = store;
        _httpClient = httpClient;
    }

    public async Task<BackgroundSaveResult> SaveBackgroundAsync(string bjdUuid, ImagePayload payload)
    {
        if (string.IsNullOrWhiteSpace(bjdUuid))
        {
            return BackgroundSaveResult.Fail("❌ 未找到绑定 UUID，无法保存背景。");
        }

        var bytes = await ResolveImageBytesAsync(payload);
        if (bytes == null || bytes.Length == 0)
        {
            return BackgroundSaveResult.Fail("❌ 未能解析图片，请重新发送图片卡片。");
        }

        var processed = TryCompressBackground(bytes);
        var outputBytes = processed.Bytes;
        var ext = processed.Extension;
        var safeUuid = string.Concat((bjdUuid ?? string.Empty).Where(ch => char.IsLetterOrDigit(ch) || ch == '-'));
        if (string.IsNullOrWhiteSpace(safeUuid))
        {
            return BackgroundSaveResult.Fail("❌ 绑定 UUID 非法，无法保存背景。");
        }

        var fileName = $"{safeUuid}{ext}";
        var outputPath = Path.Combine(_store.BackgroundDirectory, fileName);

        if (_store.TryGetBackgroundBinding(bjdUuid, out var existing)
            && !string.IsNullOrWhiteSpace(existing.FileName)
            && !string.Equals(existing.FileName, fileName, StringComparison.OrdinalIgnoreCase))
        {
            var oldPath = Path.Combine(_store.BackgroundDirectory, existing.FileName);
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }
        }

        File.WriteAllBytes(outputPath, outputBytes);
        _store.UpsertBackgroundBinding(bjdUuid, fileName);
        return BackgroundSaveResult.Ok("✅ 背景图片已保存，后续查询将使用该背景。");
    }

    public string? TryBuildBackgroundDataUri(string bjdUuid)
    {
        return TryBuildBackgroundDataUriWithReason(bjdUuid, out _);
    }

    public string? TryBuildBackgroundDataUriWithReason(string bjdUuid, out string reason)
    {
        if (string.IsNullOrWhiteSpace(bjdUuid))
        {
            reason = "目标UUID为空";
            return null;
        }

        if (!_store.TryGetBackgroundBinding(bjdUuid, out var binding))
        {
            reason = $"未找到背景绑定记录(uuid={bjdUuid})";
            return null;
        }

        if (string.IsNullOrWhiteSpace(binding.FileName))
        {
            reason = $"背景绑定记录存在但文件名为空(uuid={bjdUuid})";
            return null;
        }

        var path = Path.Combine(_store.BackgroundDirectory, binding.FileName);
        if (!File.Exists(path))
        {
            reason = $"背景文件不存在(path={path})";
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
        {
            reason = $"背景文件为空(path={path})";
            return null;
        }

        var mime = ResolveMimeType(binding.FileName, bytes);
        reason = "ok";
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private async Task<byte[]?> ResolveImageBytesAsync(ImagePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.Base64))
        {
            var bytes = TryDecodeBase64(payload.Base64);
            if (bytes != null) return bytes;
        }

        if (!string.IsNullOrWhiteSpace(payload.File))
        {
            if (payload.File.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = TryDecodeBase64(payload.File);
                if (bytes != null) return bytes;
            }
        }

        var pathCandidate = payload.Path ?? payload.File;
        if (!string.IsNullOrWhiteSpace(pathCandidate))
        {
            var filePath = TryNormalizeLocalPath(pathCandidate);
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                return await File.ReadAllBytesAsync(filePath);
            }
        }

        var urlCandidate = payload.Url ?? payload.File;
        if (!string.IsNullOrWhiteSpace(urlCandidate) && Uri.TryCreate(urlCandidate, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                return await _httpClient.GetByteArrayAsync(uri);
            }

            if (uri.Scheme == Uri.UriSchemeFile && File.Exists(uri.LocalPath))
            {
                return await File.ReadAllBytesAsync(uri.LocalPath);
            }
        }

        return null;
    }

    private static string? TryNormalizeLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                return uri.LocalPath;
            }
        }

        return path;
    }

    private static byte[]? TryDecodeBase64(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var raw = text.Trim();
        if (raw.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[9..];
        }

        var match = Regex.Match(raw, @"^data:.*;base64,(.+)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            raw = match.Groups[1].Value;
        }

        try
        {
            return Convert.FromBase64String(raw);
        }
        catch
        {
            return null;
        }
    }

    private static string DetectImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return ".png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return ".gif";
        }

        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return ".webp";
        }

        return ".png";
    }

    private static (byte[] Bytes, string Extension) TryCompressBackground(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var image = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: false);

            var targetSize = GetTargetSize(image.Width, image.Height);
            var shouldResize = targetSize.width != image.Width || targetSize.height != image.Height;
            var shouldCompress = bytes.Length > MaxBackgroundBytes || shouldResize;

            if (!shouldCompress)
            {
                var ext = DetectImageExtension(bytes);
                return (bytes, ext);
            }

            using var resized = shouldResize ? ResizeImage(image, targetSize.width, targetSize.height) : new Bitmap(image);
            using var output = new MemoryStream();

            var jpegCodec = GetImageEncoder(ImageFormat.Jpeg);
            if (jpegCodec != null)
            {
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
                resized.Save(output, jpegCodec, encoderParams);
                return (output.ToArray(), ".jpg");
            }

            resized.Save(output, ImageFormat.Png);
            return (output.ToArray(), ".png");
        }
        catch
        {
            var ext = DetectImageExtension(bytes);
            return (bytes, ext);
        }
    }

    private static (int width, int height) GetTargetSize(int width, int height)
    {
        var max = Math.Max(width, height);
        if (max <= MaxBackgroundDimension)
        {
            return (width, height);
        }

        var scale = (double)MaxBackgroundDimension / max;
        var newWidth = Math.Max(1, (int)Math.Round(width * scale));
        var newHeight = Math.Max(1, (int)Math.Round(height * scale));
        return (newWidth, newHeight);
    }

    private static Bitmap ResizeImage(Image image, int width, int height)
    {
        var dest = new Bitmap(width, height);
        dest.SetResolution(image.HorizontalResolution, image.VerticalResolution);

        using var graphics = Graphics.FromImage(dest);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(image, new Rectangle(0, 0, width, height));

        return dest;
    }

    private static ImageCodecInfo? GetImageEncoder(ImageFormat format)
    {
        return ImageCodecInfo.GetImageEncoders().FirstOrDefault(codec => codec.FormatID == format.Guid);
    }

    private static string ResolveMimeType(string fileName, byte[] bytes)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".png" => "image/png",
            _ => DetectImageExtension(bytes) switch
            {
                ".jpg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/png"
            }
        };
    }
}

public readonly record struct ImagePayload(string? Url, string? File, string? Path, string? Base64);

public readonly record struct BackgroundSaveResult(bool Success, string Message)
{
    public static BackgroundSaveResult Ok(string message) => new(true, message);
    public static BackgroundSaveResult Fail(string message) => new(false, message);
}
