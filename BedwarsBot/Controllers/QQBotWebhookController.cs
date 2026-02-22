using System.Text;
using Microsoft.AspNetCore.Mvc;
using NSec.Cryptography;
using Newtonsoft.Json.Linq;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace BedwarsBot.Controllers;

[ApiController]
[Route("api/qqbot")]
public class QQBotWebhookController : ControllerBase
{
    private readonly ILogger<QQBotWebhookController> _logger;
    private static int _libsodiumLoadState;
    private static string? _libsodiumLoadError;
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ProcessedEventCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan ProcessedEventTtl = TimeSpan.FromMinutes(10);
    private static long _processedEventCounter;

    public QQBotWebhookController(ILogger<QQBotWebhookController> logger)
    {
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> ReceiveWebhookAsync()
    {
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var rawJson = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return BadRequest(new { code = 400, message = "empty body" });
            }

            // 打印链路信息，方便核对是否正确解析了转发头
            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            _logger.LogInformation(
                "[QQWebhook] 收到请求。RemoteIp={RemoteIp}, XFF={XFF}, XRealIP={XRealIP}, XFP={XFP}, BodyLength={BodyLength}",
                remoteIp,
                Request.Headers["X-Forwarded-For"].ToString(),
                Request.Headers["X-Real-IP"].ToString(),
                Request.Headers["X-Forwarded-Proto"].ToString(),
                rawJson.Length);

            JObject payload;
            try
            {
                payload = JObject.Parse(rawJson);
            }
            catch
            {
                return BadRequest(new { code = 400, message = "invalid json" });
            }

            BotConfig? botConfig;
            try
            {
                botConfig = ConfigManager.LoadConfig();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[QQWebhook] 读取配置失败。");
                return StatusCode(500, new { code = 500, message = "config load failed", detail = ex.Message });
            }

            if (botConfig == null)
            {
                return StatusCode(500, new { code = 500, message = "config not loaded" });
            }

            var webhookConfig = botConfig.Webhook ?? new WebhookConfig();
            var webhookSecret = string.IsNullOrWhiteSpace(webhookConfig.Secret)
                ? botConfig.ClientSecret
                : webhookConfig.Secret;
            if (string.IsNullOrWhiteSpace(webhookSecret))
            {
                _logger.LogError("[QQWebhook] secret 为空，请检查配置！");
                return StatusCode(500, new { code = 500, message = "secret is empty" });
            }

            var op = payload["op"]?.Value<int>() ?? -1;

            // 官方 URL 校验：op=13，返回 plain_token + signature
            if (op == 13)
            {
                var plainToken = payload.SelectToken("d.plain_token")?.ToString()
                                 ?? payload["plain_token"]?.ToString();
                var eventTs = payload.SelectToken("d.event_ts")?.ToString()
                              ?? payload["event_ts"]?.ToString();

                if (string.IsNullOrWhiteSpace(plainToken) || string.IsNullOrWhiteSpace(eventTs))
                {
                    return BadRequest(new { code = 400, message = "invalid op13 payload" });
                }

                try
                {
                    var signature = SignHex(eventTs + plainToken, webhookSecret);
                    _logger.LogInformation("[QQWebhook] op=13 URL 校验响应完成。plain_token={PlainToken}", plainToken);
                    return Ok(new { plain_token = plainToken, signature });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[QQWebhook] op=13 签名失败。");
                    return StatusCode(500, new { code = 500, message = "op13 sign failed", detail = ex.Message });
                }
            }

            // 非 op=13 请求按配置校验签名头，防止伪造请求
            if (webhookConfig.VerifySignature && !VerifySignature(Request.Headers, rawJson, webhookSecret))
            {
                _logger.LogWarning("[QQWebhook] 签名校验失败。缺少或无效的 X-Signature-* 头。op={Op}", op);
                return Unauthorized(new { code = 401, message = "invalid signature" });
            }

            var dedupKey = BuildEventDedupKey(payload, rawJson);
            if (!TryMarkEventAsNew(dedupKey))
            {
                _logger.LogInformation(
                    "[QQWebhook] 重复事件已忽略。key={DedupKey}, op={Op}, t={EventType}",
                    dedupKey,
                    op,
                    payload["t"]?.ToString() ?? string.Empty);
                return Ok(BuildDispatchAck(payload));
            }

            var dispatchPayload = (JObject)payload.DeepClone();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Program.DispatchOfficialWebhookAsync(dispatchPayload);
                }
                catch (Exception dispatchEx)
                {
                    _logger.LogError(dispatchEx, "[QQWebhook] 后台事件处理失败。key={DedupKey}", dedupKey);
                }
            });

            // 立即回 ACK，避免腾讯侧因超时重推导致重复执行
            return Ok(BuildDispatchAck(payload));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[QQWebhook] 未处理异常。");
            return StatusCode(500, new { code = 500, message = "webhook internal error", detail = ex.Message });
        }
    }

    private static bool VerifySignature(IHeaderDictionary headers, string body, string secret)
    {
        EnsureLibsodiumLoaded();

        var signatureHex = headers["X-Signature-Ed25519"].ToString();
        if (string.IsNullOrWhiteSpace(signatureHex))
        {
            signatureHex = headers["x-signature-ed25519"].ToString();
        }

        var timestamp = headers["X-Signature-Timestamp"].ToString();
        if (string.IsNullOrWhiteSpace(timestamp))
        {
            timestamp = headers["x-signature-timestamp"].ToString();
        }

        if (string.IsNullOrWhiteSpace(signatureHex) || string.IsNullOrWhiteSpace(timestamp))
        {
            return false;
        }

        if (!TryParseHex(signatureHex, out var signatureBytes))
        {
            return false;
        }

        var seed = BuildEd25519Seed(secret);
        using var key = Key.Import(
            SignatureAlgorithm.Ed25519,
            seed,
            KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.None });
        var data = Encoding.UTF8.GetBytes(timestamp + body);
        return SignatureAlgorithm.Ed25519.Verify(key.PublicKey, data, signatureBytes);
    }

    private static string SignHex(string plainText, string secret)
    {
        EnsureLibsodiumLoaded();

        var seed = BuildEd25519Seed(secret);
        using var key = Key.Import(
            SignatureAlgorithm.Ed25519,
            seed,
            KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.None });
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var signature = SignatureAlgorithm.Ed25519.Sign(key, bytes);
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    private static byte[] BuildEd25519Seed(string secret)
    {
        var raw = secret?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Webhook secret is empty.");
        }

        var seedText = raw;
        while (seedText.Length < 32)
        {
            seedText += raw;
        }

        var seedUtf8 = Encoding.UTF8.GetBytes(seedText);
        if (seedUtf8.Length < 32)
        {
            throw new InvalidOperationException("Webhook seed bytes are shorter than 32.");
        }

        var seed = new byte[32];
        Buffer.BlockCopy(seedUtf8, 0, seed, 0, 32);
        return seed;
    }

    private static bool TryParseHex(string value, out byte[] bytes)
    {
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (value.Length % 2 != 0)
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static object BuildDispatchAck(JObject payload)
    {
        var seqToken = payload["s"];
        if (seqToken != null && seqToken.Type == JTokenType.Integer)
        {
            return new
            {
                op = 12,
                d = new
                {
                    seq = seqToken.Value<long>()
                }
            };
        }

        return new { op = 12 };
    }

    private static string BuildEventDedupKey(JObject payload, string rawJson)
    {
        var idCandidates = new[]
        {
            payload["id"]?.ToString(),
            payload.SelectToken("d.id")?.ToString(),
            payload.SelectToken("d.event_id")?.ToString(),
            payload.SelectToken("d.msg_id")?.ToString(),
            payload.SelectToken("d.message_id")?.ToString()
        };

        foreach (var id in idCandidates)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                return $"id:{id}";
            }
        }

        var eventType = payload["t"]?.ToString() ?? string.Empty;
        var seq = payload["s"]?.ToString() ?? string.Empty;
        var group = payload.SelectToken("d.group_openid")?.ToString()
                    ?? payload.SelectToken("d.group_id")?.ToString()
                    ?? string.Empty;
        var author = payload.SelectToken("d.author.id")?.ToString() ?? string.Empty;
        var content = payload.SelectToken("d.content")?.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(eventType)
            || !string.IsNullOrWhiteSpace(seq)
            || !string.IsNullOrWhiteSpace(group)
            || !string.IsNullOrWhiteSpace(author)
            || !string.IsNullOrWhiteSpace(content))
        {
            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            return $"evt:{eventType}|s:{seq}|g:{group}|a:{author}|c:{contentHash}";
        }

        var rawHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawJson)));
        return $"raw:{rawHash}";
    }

    private static bool TryMarkEventAsNew(string dedupKey)
    {
        var now = DateTimeOffset.UtcNow;
        if ((Interlocked.Increment(ref _processedEventCounter) & 0x3F) == 0)
        {
            foreach (var kv in ProcessedEventCache)
            {
                if (kv.Value <= now)
                {
                    ProcessedEventCache.TryRemove(kv.Key, out _);
                }
            }
        }

        while (true)
        {
            if (ProcessedEventCache.TryGetValue(dedupKey, out var expiresAt))
            {
                if (expiresAt > now)
                {
                    return false;
                }

                ProcessedEventCache.TryRemove(dedupKey, out _);
                continue;
            }

            if (ProcessedEventCache.TryAdd(dedupKey, now + ProcessedEventTtl))
            {
                return true;
            }
        }
    }

    private static void EnsureLibsodiumLoaded()
    {
        var state = Volatile.Read(ref _libsodiumLoadState);
        if (state == 2)
        {
            return;
        }

        if (state == 3)
        {
            throw new DllNotFoundException(_libsodiumLoadError ?? "libsodium load failed.");
        }

        if (Interlocked.CompareExchange(ref _libsodiumLoadState, 1, 0) != 0)
        {
            SpinWait.SpinUntil(() =>
            {
                var current = Volatile.Read(ref _libsodiumLoadState);
                return current == 2 || current == 3;
            }, TimeSpan.FromSeconds(3));

            if (Volatile.Read(ref _libsodiumLoadState) == 3)
            {
                throw new DllNotFoundException(_libsodiumLoadError ?? "libsodium load failed.");
            }

            return;
        }

        try
        {
            TryLoadLibsodiumCore();
            Volatile.Write(ref _libsodiumLoadState, 2);
        }
        catch (Exception ex)
        {
            _libsodiumLoadError = ex.Message;
            Volatile.Write(ref _libsodiumLoadState, 3);
            throw;
        }
    }

    private static void TryLoadLibsodiumCore()
    {
        // 非 Windows 平台交给 NSec 默认加载逻辑
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var baseDir = AppContext.BaseDirectory;
        var candidates = BuildLibsodiumCandidates(baseDir);
        Exception? lastError = null;

        foreach (var file in candidates)
        {
            if (!System.IO.File.Exists(file))
            {
                continue;
            }

            try
            {
                NativeLibrary.Load(file);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        try
        {
            NativeLibrary.Load("libsodium");
            return;
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        var attempted = string.Join(" | ", candidates);
        var detail = lastError?.Message ?? "unknown";
        throw new DllNotFoundException(
            $"Unable to load libsodium.dll. Tried: {attempted}. BaseDir={baseDir}. Error={detail}",
            lastError);
    }

    private static List<string> BuildLibsodiumCandidates(string baseDir)
    {
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64"
        };

        return new List<string>
        {
            Path.Combine(baseDir, "libsodium.dll"),
            Path.Combine(baseDir, "runtimes", rid, "native", "libsodium.dll"),
            Path.Combine(baseDir, "runtimes", "win-x64", "native", "libsodium.dll"),
            Path.Combine(baseDir, "runtimes", "win-x86", "native", "libsodium.dll")
        };
    }
}
