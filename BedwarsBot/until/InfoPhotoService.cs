using BedwarsBot.api;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;

namespace BedwarsBot;

public class InfoPhotoService
{
    private readonly BotDataStore _store;
    private readonly MojangApi _mojangApi;
    private readonly SkinApi _skinApi;
    private readonly HttpClient _httpClient;

    public InfoPhotoService(BotDataStore store, HttpClient httpClient)
    {
        _store = store;
        _httpClient = httpClient;
        _mojangApi = new MojangApi(httpClient);
        _skinApi = new SkinApi(httpClient);
    }

    public async Task<SkinAddResult> AddSkinAsync(string qq, string officialId, BindService bindService)
    {
        if (!bindService.TryGetBindingByQq(qq, out var binding))
        {
            return SkinAddResult.Fail("❌ 你还没有绑定布吉岛账号，请先执行 !bind 布吉岛用户名");
        }

        var saveResult = await ResolveAndSaveAvatarAsync(binding.BjdUuid, officialId);
        if (!saveResult.Success)
        {
            return SkinAddResult.Fail($"❌ 绑定皮肤失败: {saveResult.ErrorMessage}");
        }

        _store.UpsertSkinBinding(binding.BjdUuid, officialId, saveResult.FileName!);
        return SkinAddResult.Ok($"✅ 皮肤绑定成功: {binding.BjdUuid} -> {officialId}");
    }

    public async Task<SkinAddResult> AddSkinFromUploadAsync(string qq, ImagePayload payload, BindService bindService)
    {
        if (!bindService.TryGetBindingByQq(qq, out var binding))
        {
            return SkinAddResult.Fail("❌ 你还没有绑定布吉岛账号，请先执行 !bind 布吉岛用户名");
        }

        var saveResult = await ResolveAndSaveAvatarFromUploadAsync(binding.BjdUuid, payload);
        if (!saveResult.Success)
        {
            return SkinAddResult.Fail($"❌ 本地皮肤上传失败: {saveResult.ErrorMessage}");
        }

        _store.UpsertSkinBinding(binding.BjdUuid, "local_upload", saveResult.FileName!);
        return SkinAddResult.Ok($"✅ 本地皮肤上传成功: {binding.BjdUuid} -> local_upload");
    }

    public string? TryBuildAvatarDataUri(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return null;
        if (!_store.TryGetSkinBinding(uuid, out var skinBinding)) return null;

        var filePath = Path.Combine(_store.AvatarDirectory, skinBinding.AvatarFileName);
        if (!File.Exists(filePath)) return null;

        var bytes = File.ReadAllBytes(filePath);
        if (bytes.Length == 0) return null;
        return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
    }

    private async Task<AvatarDownloadResult> ResolveAndSaveAvatarAsync(string bjdUuid, string officialId)
    {
        var safeUuid = string.Concat((bjdUuid ?? string.Empty).Where(ch => char.IsLetterOrDigit(ch) || ch == '-'));
        if (string.IsNullOrWhiteSpace(safeUuid))
        {
            return AvatarDownloadResult.Fail("绑定的布吉岛 UUID 非法");
        }

        var fileName = $"{safeUuid}.png";
        var outputPath = Path.Combine(_store.AvatarDirectory, fileName);

        var mojangResult = await _mojangApi.GetUuidByNameAsync(officialId);
        if (!mojangResult.Success || string.IsNullOrWhiteSpace(mojangResult.Uuid))
        {
            return AvatarDownloadResult.Fail(mojangResult.ErrorMessage ?? "Mojang uuid 查询失败");
        }

        var downloadResult = await _skinApi.DownloadFaceByUuidAsync(mojangResult.Uuid, outputPath);
        if (!downloadResult.Success)
        {
            return AvatarDownloadResult.Fail(downloadResult.ErrorMessage ?? "皮肤下载失败");
        }

        return AvatarDownloadResult.Ok(fileName);
    }

    private async Task<AvatarDownloadResult> ResolveAndSaveAvatarFromUploadAsync(string bjdUuid, ImagePayload payload)
    {
        var safeUuid = string.Concat((bjdUuid ?? string.Empty).Where(ch => char.IsLetterOrDigit(ch) || ch == '-'));
        if (string.IsNullOrWhiteSpace(safeUuid))
        {
            return AvatarDownloadResult.Fail("绑定的布吉岛 UUID 非法");
        }

        var fileName = $"{safeUuid}.png";
        var outputPath = Path.Combine(_store.AvatarDirectory, fileName);

        byte[]? sourceBytes;
        try
        {
            sourceBytes = await ResolveImageBytesAsync(payload);
        }
        catch (Exception ex)
        {
            return AvatarDownloadResult.Fail($"读取上传文件失败: {ex.Message}");
        }

        if (sourceBytes == null || sourceBytes.Length == 0)
        {
            return AvatarDownloadResult.Fail("未能解析皮肤源文件，请重新发送 PNG 皮肤文件");
        }

        var avatarBytes = TryExtractAvatarPng(sourceBytes, out var parseError);
        if (avatarBytes == null || avatarBytes.Length == 0)
        {
            return AvatarDownloadResult.Fail(parseError);
        }

        try
        {
            await File.WriteAllBytesAsync(outputPath, avatarBytes);
        }
        catch (Exception ex)
        {
            return AvatarDownloadResult.Fail($"保存头像文件失败: {ex.Message}");
        }

        return AvatarDownloadResult.Ok(fileName);
    }

    private async Task<byte[]?> ResolveImageBytesAsync(ImagePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.Base64))
        {
            var bytes = TryDecodeBase64(payload.Base64);
            if (bytes != null) return bytes;
        }

        if (!string.IsNullOrWhiteSpace(payload.File)
            && payload.File.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = TryDecodeBase64(payload.File);
            if (bytes != null) return bytes;
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
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(path, UriKind.Absolute, out var fileUri))
        {
            return fileUri.LocalPath;
        }

        return path;
    }

    private static byte[]? TryDecodeBase64(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

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

    private static byte[]? TryExtractAvatarPng(byte[] skinBytes, out string errorMessage)
    {
        errorMessage = "皮肤文件解析失败";
        try
        {
            using var input = new MemoryStream(skinBytes);
            using var skin = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: false);

            if (skin.Width < 64 || skin.Height < 32)
            {
                errorMessage = $"皮肤分辨率过小（{skin.Width}x{skin.Height}），至少需要 64x32";
                return null;
            }

            if (skin.Width % 64 != 0 || skin.Height % 32 != 0)
            {
                errorMessage = $"皮肤分辨率不符合 Minecraft 皮肤格式（{skin.Width}x{skin.Height}）";
                return null;
            }

            var scale = skin.Width / 64;
            if (scale <= 0 || skin.Height < 32 * scale)
            {
                errorMessage = "皮肤分辨率与标准皮肤比例不匹配";
                return null;
            }

            var partSize = 8 * scale;
            var headBaseRect = new Rectangle(8 * scale, 8 * scale, partSize, partSize);
            var headOverlayRect = new Rectangle(40 * scale, 8 * scale, partSize, partSize);

            using var face = new Bitmap(partSize, partSize, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(face))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.DrawImage(skin, new Rectangle(0, 0, partSize, partSize), headBaseRect, GraphicsUnit.Pixel);

                if (skin.Width >= headOverlayRect.Right && skin.Height >= headOverlayRect.Bottom)
                {
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.DrawImage(skin, new Rectangle(0, 0, partSize, partSize), headOverlayRect, GraphicsUnit.Pixel);
                }
            }

            const int avatarSize = 128;
            using var enlarged = new Bitmap(avatarSize, avatarSize, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(enlarged))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.DrawImage(face, new Rectangle(0, 0, avatarSize, avatarSize), new Rectangle(0, 0, partSize, partSize), GraphicsUnit.Pixel);
            }

            using var output = new MemoryStream();
            enlarged.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            errorMessage = $"皮肤文件读取失败: {ex.Message}";
            return null;
        }
    }
}

public readonly record struct SkinAddResult(bool Success, string Message)
{
    public static SkinAddResult Ok(string message) => new(true, message);
    public static SkinAddResult Fail(string message) => new(false, message);
}

public readonly record struct AvatarDownloadResult(bool Success, string? FileName, string? ErrorMessage)
{
    public static AvatarDownloadResult Ok(string fileName) => new(true, fileName, null);
    public static AvatarDownloadResult Fail(string error) => new(false, null, error);
}
