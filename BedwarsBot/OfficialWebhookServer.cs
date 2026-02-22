using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NSec.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BedwarsBot;

public sealed class OfficialWebhookServer
{
    private sealed record TrustedProxyRule(IPAddress Network, int PrefixLength);
    private sealed record ResolvedClientInfo(string RemoteIp, string ClientIp, string Proto, bool UsedForwardedHeaders);

    private readonly QQBotV2 _qqBot;
    private readonly WebhookConfig _webhookConfig;
    private readonly HttpListener _listener = new();
    private readonly string _callbackPath;
    private readonly Key _signatureKey;
    private readonly List<TrustedProxyRule> _trustedProxyRules;
    private readonly bool _trustForwardedHeadersFromAnySource;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public OfficialWebhookServer(QQBotV2 qqBot, BotConfig botConfig)
    {
        _qqBot = qqBot;
        _webhookConfig = botConfig.Webhook ?? new WebhookConfig();
        _callbackPath = NormalizePath(_webhookConfig.CallbackPath);

        var prefixes = _webhookConfig.ListenPrefixes ?? new List<string>();
        if (prefixes.Count == 0)
        {
            prefixes.Add("http://+:5001/");
        }

        foreach (var prefix in prefixes.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            _listener.Prefixes.Add(prefix.Trim());
        }

        if (_listener.Prefixes.Count == 0)
        {
            _listener.Prefixes.Add("http://+:5001/");
        }

        (_trustedProxyRules, _trustForwardedHeadersFromAnySource) = BuildTrustedProxyRules(_webhookConfig.TrustedProxyIps);

        var webhookSecret = string.IsNullOrWhiteSpace(_webhookConfig.Secret)
            ? botConfig.ClientSecret
            : _webhookConfig.Secret;
        var seed = BuildEd25519Seed(webhookSecret);
        _signatureKey = Key.Import(
            SignatureAlgorithm.Ed25519,
            seed,
            KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.None });
    }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _loopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));

        Console.WriteLine($"[Webhook] Server started: {string.Join(", ", _listener.Prefixes.Cast<string>())}");
        Console.WriteLine($"[Webhook] Callback path: {_callbackPath}");
        Console.WriteLine($"[Webhook] Signature header verify: {(_webhookConfig.VerifySignature ? "on" : "off")}");
        Console.WriteLine("[Webhook] HTTPS redirect: off (TLS should be terminated by upstream reverse proxy).");
        if (_webhookConfig.TrustForwardedHeaders)
        {
            var trustedText = _trustForwardedHeadersFromAnySource
                ? "ALL (no TrustedProxyIps configured)"
                : string.Join(", ", _webhookConfig.TrustedProxyIps.Where(x => !string.IsNullOrWhiteSpace(x)));
            Console.WriteLine($"[Webhook] Forwarded headers trust: on, trusted proxies: {trustedText}");
        }
        else
        {
            Console.WriteLine("[Webhook] Forwarded headers trust: off");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try
        {
            _cts?.Cancel();
            _listener.Stop();
            if (_loopTask != null)
            {
                await _loopTask;
            }
        }
        catch
        {
        }
        finally
        {
            _listener.Close();
            _signatureKey.Dispose();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync();
                _ = Task.Run(() => ProcessRequestAsync(context), cancellationToken);
            }
            catch (HttpListenerException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Webhook] Accept error: {ex.Message}");
                if (context != null)
                {
                    TryWriteText(context.Response, 500, "internal error");
                }
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            var requestPath = NormalizePath(request.Url?.AbsolutePath);
            var client = ResolveClientInfo(request);
            Console.WriteLine($"[Webhook] Request {request.HttpMethod} {requestPath}, remote={client.RemoteIp}, client={client.ClientIp}, proto={client.Proto}, forwarded={client.UsedForwardedHeaders}");

            if (!string.Equals(requestPath, _callbackPath, StringComparison.OrdinalIgnoreCase))
            {
                TryWriteText(response, 404, "not found");
                return;
            }

            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                TryWriteText(response, 405, "method not allowed");
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                TryWriteText(response, 400, "empty body");
                return;
            }

            JObject payload;
            try
            {
                payload = JObject.Parse(body);
            }
            catch
            {
                TryWriteText(response, 400, "invalid json");
                return;
            }

            var op = payload["op"]?.Value<int>() ?? -1;
            if (op == 13)
            {
                if (!TryBuildValidationAck(payload, out var validationAck))
                {
                    TryWriteText(response, 400, "invalid op13 payload");
                    return;
                }

                WriteJson(response, 200, validationAck);
                return;
            }

            if (_webhookConfig.VerifySignature && !VerifySignature(request.Headers, body))
            {
                TryWriteText(response, 401, "invalid signature");
                return;
            }

            await _qqBot.HandleWebhookPayloadAsync(payload);
            WriteJson(response, 200, BuildDispatchAck(payload));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Webhook] Process error: {ex.Message}");
            TryWriteText(context.Response, 500, "internal error");
        }
    }

    private bool TryBuildValidationAck(JObject payload, out object ack)
    {
        ack = new { code = 0 };

        var plainToken = payload.SelectToken("d.plain_token")?.ToString()
                         ?? payload["plain_token"]?.ToString();
        var eventTs = payload.SelectToken("d.event_ts")?.ToString()
                      ?? payload["event_ts"]?.ToString();

        if (string.IsNullOrWhiteSpace(plainToken) || string.IsNullOrWhiteSpace(eventTs))
        {
            return false;
        }

        var signature = SignHex(eventTs + plainToken);
        ack = new
        {
            plain_token = plainToken,
            signature
        };

        Console.WriteLine("[Webhook] op=13 callback validation acknowledged.");
        return true;
    }

    private object BuildDispatchAck(JObject payload)
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

        return new
        {
            op = 12
        };
    }

    private bool VerifySignature(NameValueCollection headers, string body)
    {
        var signatureHex =
            headers["X-Signature-Ed25519"]
            ?? headers["x-signature-ed25519"]
            ?? string.Empty;
        var timestamp =
            headers["X-Signature-Timestamp"]
            ?? headers["x-signature-timestamp"]
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(signatureHex) || string.IsNullOrWhiteSpace(timestamp))
        {
            Console.WriteLine("[Webhook] Missing signature headers.");
            return false;
        }

        if (!TryParseHex(signatureHex, out var signatureBytes))
        {
            Console.WriteLine("[Webhook] Signature is not valid hex.");
            return false;
        }

        var data = Encoding.UTF8.GetBytes(timestamp + body);
        var ok = SignatureAlgorithm.Ed25519.Verify(_signatureKey.PublicKey, data, signatureBytes);
        if (!ok)
        {
            Console.WriteLine("[Webhook] Signature verification failed.");
        }

        return ok;
    }

    private ResolvedClientInfo ResolveClientInfo(HttpListenerRequest request)
    {
        var remoteIp = NormalizeIpAddress(request.RemoteEndPoint?.Address);
        var remoteIpText = remoteIp?.ToString() ?? "unknown";
        var proto = request.Url?.Scheme ?? "http";
        if (!_webhookConfig.TrustForwardedHeaders || !IsTrustedForwardedSource(remoteIp))
        {
            return new ResolvedClientInfo(remoteIpText, remoteIpText, proto, false);
        }

        var forwardedProtoRaw = GetHeaderValue(request.Headers, "X-Forwarded-Proto");
        if (!string.IsNullOrWhiteSpace(forwardedProtoRaw))
        {
            var first = forwardedProtoRaw.Split(',')[0].Trim();
            if (string.Equals(first, "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "https", StringComparison.OrdinalIgnoreCase))
            {
                proto = first.ToLowerInvariant();
            }
        }

        var clientIp = TryExtractClientIpFromForwardedFor(GetHeaderValue(request.Headers, "X-Forwarded-For"));
        if (string.IsNullOrWhiteSpace(clientIp))
        {
            clientIp = TryExtractClientIpFromForwardedFor(GetHeaderValue(request.Headers, "X-Real-IP"));
        }

        if (string.IsNullOrWhiteSpace(clientIp))
        {
            clientIp = remoteIpText;
        }

        return new ResolvedClientInfo(remoteIpText, clientIp, proto, true);
    }

    private bool IsTrustedForwardedSource(IPAddress? remoteIp)
    {
        if (_trustForwardedHeadersFromAnySource)
        {
            return true;
        }

        if (remoteIp == null)
        {
            return false;
        }

        var normalized = NormalizeIpAddress(remoteIp);
        if (normalized == null)
        {
            return false;
        }

        foreach (var rule in _trustedProxyRules)
        {
            if (IsInCidr(normalized, rule.Network, rule.PrefixLength))
            {
                return true;
            }
        }

        return false;
    }

    private static (List<TrustedProxyRule> Rules, bool TrustAnySource) BuildTrustedProxyRules(List<string>? configured)
    {
        var rules = new List<TrustedProxyRule>();
        var hasAnyRule = false;

        foreach (var raw in configured ?? Enumerable.Empty<string>())
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            hasAnyRule = true;
            if (text == "*" || text.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return (new List<TrustedProxyRule>(), true);
            }

            if (TryParseTrustedProxyRule(text, out var rule))
            {
                rules.Add(rule);
            }
            else
            {
                Console.WriteLine($"[Webhook] Ignored invalid TrustedProxyIps item: {text}");
            }
        }

        if (!hasAnyRule)
        {
            return (rules, true);
        }

        return (rules, false);
    }

    private static bool TryParseTrustedProxyRule(string text, out TrustedProxyRule rule)
    {
        rule = new TrustedProxyRule(IPAddress.None, 32);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (!trimmed.Contains('/'))
        {
            if (!IPAddress.TryParse(trimmed, out var ip))
            {
                return false;
            }

            var normalizedIp = NormalizeIpAddress(ip);
            if (normalizedIp == null)
            {
                return false;
            }

            var prefix = normalizedIp.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            rule = new TrustedProxyRule(normalizedIp, prefix);
            return true;
        }

        var slash = trimmed.IndexOf('/');
        if (slash <= 0 || slash == trimmed.Length - 1)
        {
            return false;
        }

        var ipPart = trimmed[..slash].Trim();
        var prefixPart = trimmed[(slash + 1)..].Trim();
        if (!IPAddress.TryParse(ipPart, out var cidrIp))
        {
            return false;
        }

        var normalized = NormalizeIpAddress(cidrIp);
        if (normalized == null)
        {
            return false;
        }

        if (!int.TryParse(prefixPart, out var prefixLength))
        {
            return false;
        }

        var maxPrefix = normalized.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefix)
        {
            return false;
        }

        rule = new TrustedProxyRule(normalized, prefixLength);
        return true;
    }

    private static bool IsInCidr(IPAddress ip, IPAddress network, int prefixLength)
    {
        var normalizedIp = NormalizeIpAddress(ip);
        var normalizedNetwork = NormalizeIpAddress(network);
        if (normalizedIp == null || normalizedNetwork == null)
        {
            return false;
        }

        if (normalizedIp.AddressFamily != normalizedNetwork.AddressFamily)
        {
            return false;
        }

        var ipBytes = normalizedIp.GetAddressBytes();
        var networkBytes = normalizedNetwork.GetAddressBytes();

        var fullBytes = prefixLength / 8;
        var remainderBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (ipBytes[i] != networkBytes[i])
            {
                return false;
            }
        }

        if (remainderBits == 0)
        {
            return true;
        }

        var mask = 0xFF << (8 - remainderBits);
        return (ipBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static string? TryExtractClientIpFromForwardedFor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        foreach (var part in raw.Split(','))
        {
            var candidate = part.Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            if (IPAddress.TryParse(candidate, out var parsed))
            {
                var normalized = NormalizeIpAddress(parsed);
                return normalized?.ToString();
            }
        }

        return null;
    }

    private static string GetHeaderValue(NameValueCollection headers, string key)
    {
        return headers[key] ?? headers[key.ToLowerInvariant()] ?? string.Empty;
    }

    private static IPAddress? NormalizeIpAddress(IPAddress? ip)
    {
        if (ip == null)
        {
            return null;
        }

        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private string SignHex(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var signature = SignatureAlgorithm.Ed25519.Sign(_signatureKey, bytes);
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    private static byte[] BuildEd25519Seed(string secret)
    {
        var raw = secret?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Webhook secret is empty.");
        }

        // Official algorithm: repeat secret until length >= 32, then take first 32 bytes as seed.
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

    private static string NormalizePath(string? path)
    {
        var p = (path ?? "/").Trim();
        if (!p.StartsWith('/'))
        {
            p = "/" + p;
        }

        if (p.Length > 1)
        {
            p = p.TrimEnd('/');
        }

        return p;
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

    private static void WriteJson(HttpListenerResponse response, int statusCode, object payload)
    {
        var json = JsonConvert.SerializeObject(payload);
        TryWriteText(response, statusCode, json, "application/json; charset=utf-8");
    }

    private static void TryWriteText(HttpListenerResponse response, int statusCode, string text, string contentType = "text/plain; charset=utf-8")
    {
        try
        {
            var buffer = Encoding.UTF8.GetBytes(text);
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = buffer.LongLength;
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        catch
        {
        }
        finally
        {
            try { response.OutputStream.Close(); } catch { }
            try { response.Close(); } catch { }
        }
    }
}
