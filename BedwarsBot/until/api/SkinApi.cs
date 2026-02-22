namespace BedwarsBot.api;

public class SkinApi
{
    private readonly HttpClient _httpClient;

    public SkinApi(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SkinDownloadResult> DownloadFaceByUuidAsync(string mojangUuid, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(mojangUuid))
        {
            return SkinDownloadResult.Fail("正版UUID不能为空");
        }

        var compactUuid = mojangUuid.Replace("-", string.Empty);
        var faceUrl = $"https://skins.mcstats.com/face/{compactUuid}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, faceUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 BedwarsBot/1.0");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return SkinDownloadResult.Fail($"皮肤接口返回 {(int)response.StatusCode}");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 64)
            {
                return SkinDownloadResult.Fail("皮肤图片内容异常");
            }

            await File.WriteAllBytesAsync(outputPath, bytes);
            return SkinDownloadResult.Ok();
        }
        catch (Exception ex)
        {
            return SkinDownloadResult.Fail(ex.Message);
        }
    }
}

public readonly record struct SkinDownloadResult(bool Success, string? ErrorMessage)
{
    public static SkinDownloadResult Ok() => new(true, null);
    public static SkinDownloadResult Fail(string error) => new(false, error);
}
