using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.ClearScript.V8;

namespace DouyinTTS.Core.Live.Protocol;

/// <summary>
/// 抖音 WebSocket 签名生成器（使用 sign.js byted_acrawler）
/// </summary>
public static class DouyinSignature
{
    private static V8ScriptEngine? _engine;
    private static readonly object _lock = new();

    private static readonly string[] SignParams =
    [
        "live_id", "aid", "version_code", "webcast_sdk_version",
        "room_id", "sub_room_id", "sub_channel_id", "did_rule",
        "user_unique_id", "device_platform", "device_type", "ac", "identity"
    ];

    public static void Initialize(string userAgent)
    {
        lock (_lock)
        {
            if (_engine != null) return;

            _engine = new V8ScriptEngine();

            // 注入 DOM 模拟
            var uaEscaped = userAgent.Replace("'", "\\'");
            _engine.Execute($$"""
                document = {};
                window = {};
                navigator = {'userAgent': '{{uaEscaped}}'};
                """);

            // 加载 sign.js
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("DouyinTTS.Core.Live.Protocol.sign.js")
                ?? throw new FileNotFoundException("sign.js embedded resource not found");
            using var reader = new StreamReader(stream);
            var jsCode = reader.ReadToEnd();
            _engine.Execute(jsCode);
        }
    }

    /// <summary>
    /// 生成 X-Bogus 签名（线程安全）
    /// </summary>
    public static string GetSignature(string md5Input)
    {
        lock (_lock)
        {
            if (_engine == null)
                throw new InvalidOperationException("签名引擎未初始化，请先调用 Initialize()");

            var result = _engine.Invoke("get_sign", md5Input);
            return result?.ToString() ?? "";
        }
    }

    /// <summary>
    /// 从 URL 查询参数中提取 13 个签名参数，计算 MD5
    /// </summary>
    public static string ComputeStub(string queryParams)
    {
        var pairs = queryParams.Split('&')
           .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        var joined = string.Join(",", SignParams.Select(key => $"{key}={pairs.GetValueOrDefault(key, "")}"));
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash).ToLower();
    }

    public static string BuildWebSocketUrl(string roomId, string pushId, string userAgent, long fetchTime)
    {
        // douyinLive: browser_version = UA 去掉 "Mozilla" 前缀，空格编码为 %20
        var browserVersion = userAgent.StartsWith("Mozilla")
            ? userAgent["Mozilla".Length..].TrimStart('/').Replace(" ", "%20")
            : userAgent.Replace(" ", "%20");

        // 使用固定值（与 douyinLive 一致）
        var cursorFh = "7383731312643626035";
        var wrdsV = "7382620942951772256";

        // 按 douyinLive 源码的精确参数顺序构建（字典保证顺序）
        var p = new OrderedParams();
        p.Add("app_name", "douyin_web");
        p.Add("version_code", "180800");
        p.Add("webcast_sdk_version", "1.0.14-beta.0");
        p.Add("update_version_code", "1.0.14-beta.0");
        p.Add("compress", "gzip");
        p.Add("device_platform", "web");
        p.Add("cookie_enabled", "true");
        p.Add("screen_width", "1920");
        p.Add("screen_height", "1080");
        p.Add("browser_language", "zh-CN");
        p.Add("browser_platform", "Win32");
        p.Add("browser_name", "Mozilla");
        p.Add("browser_version", browserVersion);
        p.Add("browser_online", "true");
        p.Add("tz_name", "Asia/Shanghai");
        p.Add("cursor", $"d-1_u-1_fh-{cursorFh}_t-{fetchTime}_r-1");
        p.Add("internal_ext", $"internal_src:dim|wss_push_room_id:{roomId}|wss_push_did:{pushId}|first_req_ms:{fetchTime}|fetch_time:{fetchTime}|seq:1|wss_info:0-{fetchTime}-0-0|wrds_v:{wrdsV}");
        p.Add("host", "https://live.douyin.com");
        p.Add("aid", "6383");
        p.Add("live_id", "1");
        p.Add("did_rule", "3");
        p.Add("endpoint", "live_pc");
        p.Add("support_wrds", "1");
        p.Add("user_unique_id", pushId);
        p.Add("im_path", "/webcast/im/fetch/");
        p.Add("identity", "audience");
        p.Add("need_persist_msg_count", "15");
        p.Add("insert_task_id", "");
        p.Add("live_reason", "");
        p.Add("room_id", roomId);
        p.Add("heartbeatDuration", "0");

        var queryParams = p.ToString();

        // 使用真实签名
        var md5Stub = ComputeStub(queryParams);
        var signature = GetSignature(md5Stub);

        var endpoint = "webcast5-ws-web-lf";
        return $"wss://{endpoint}.douyin.com/webcast/im/push/v2/?{queryParams}&signature={signature}";
    }

    /// <summary>
    /// 保持参数插入顺序的字典（保证签名和 URL 参数顺序一致）
    /// </summary>
    private sealed class OrderedParams
    {
        private readonly List<KeyValuePair<string, string>> _pairs = new();

        public void Add(string key, string value) => _pairs.Add(new(key, value));

        public override string ToString()
        {
            return string.Join("&", _pairs.Select(kv =>
                $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        }
    }

    public static void Dispose()
    {
        lock (_lock)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
