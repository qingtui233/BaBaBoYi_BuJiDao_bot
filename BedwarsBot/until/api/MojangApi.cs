using Newtonsoft.Json.Linq;

namespace BedwarsBot.api;

public class MojangApi
{
    private readonly HttpClient _httpClient;

    public MojangApi(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<MojangQueryResult> GetUuidByNameAsync(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return MojangQueryResult.Fail("正版ID不能为空");
        }

        var url = $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(playerName)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 BedwarsBot/1.0");

            using var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent ||
                response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return MojangQueryResult.Fail("未找到该正版ID");
            }

            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return MojangQueryResult.Fail($"Mojang API返回 {(int)response.StatusCode}");
            }

            var obj = JObject.Parse(content);
            var rawId = obj["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(rawId))
            {
                return MojangQueryResult.Fail("Mojang未返回uuid");
            }

            var dashedUuid = NormalizeToDashedUuid(rawId);
            if (string.IsNullOrWhiteSpace(dashedUuid))
            {
                return MojangQueryResult.Fail("Mojang uuid格式异常");
            }

            return MojangQueryResult.Ok(dashedUuid);
        }
        catch (Exception ex)
        {
            return MojangQueryResult.Fail(ex.Message);
        }
    }

    private static string? NormalizeToDashedUuid(string raw)
    {
        var hex = raw.Replace("-", string.Empty).Trim();
        if (hex.Length != 32) return null;
        return $"{hex[0..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}

public readonly record struct MojangQueryResult(bool Success, string? Uuid, string? ErrorMessage)
{
    public static MojangQueryResult Ok(string uuid) => new(true, uuid, null);
    public static MojangQueryResult Fail(string error) => new(false, null, error);
}
