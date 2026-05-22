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

            // 注入最小化浏览器环境（与 Goja 行为一致：缺少的 API 抛出错误，被 try-catch 捕获返回默认值）
            // 关键：不提供 Image/indexedDB/DOMException/canvas context，让指纹收集返回与 Goja 相同的默认值
            var uaEscaped = userAgent.Replace("'", "\\'");
            var browserMock = $@"
                document = {{
                    'cookie': '',
                    'referrer': 'https://live.douyin.com/',
                    'createElement': function() {{ return {{}}; }},
                    'body': {{ 'clientWidth': 0, 'clientHeight': 0 }},
                    'getElementsByTagName': function() {{ return []; }},
                    'getElementById': function() {{ return null; }},
                    'createEvent': function() {{ return {{ 'initEvent': function(){{}} }}; }},
                    'addEventListener': function(){{}}
                }};
                window = {{
                    'sessionStorage': null,
                    'localStorage': null,
                    'addEventListener': function(){{}},
                    'location': {{ 'href': 'https://live.douyin.com/' }},
                    'screen': {{ 'width': 1920, 'height': 1080 }}
                }};
                navigator = {{
                    'userAgent': '{uaEscaped}',
                    'platform': 'Win32',
                    'language': 'zh-CN',
                    'cookieEnabled': true
                }};
                self = this;
                globalThis = this;
            ";
            _engine.Execute(browserMock);

            // 加载 webmssdk.js（来自 douyinLive 项目，替代 byted_acrawler sign.js）
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("DouyinTTS.Core.Live.Protocol.webmssdk.js")
                ?? throw new FileNotFoundException("webmssdk.js embedded resource not found");
            using var reader = new StreamReader(stream);
            var jsCode = reader.ReadToEnd();
            _engine.Execute(jsCode);

            // 验证 get_sign 函数是否正常工作
            try
            {
                var testResult = _engine.Invoke("get_sign", "test123");
                var testSig = testResult?.ToString() ?? "";
                System.Diagnostics.Debug.WriteLine($"[签名验证] get_sign('test123') = '{testSig}' (长度={testSig.Length})");
                if (string.IsNullOrEmpty(testSig))
                    System.Diagnostics.Debug.WriteLine("[签名验证] 警告: get_sign 返回空值，crawler 可能未正确初始化");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[签名验证] 错误: {ex.Message}");
            }

            // 加载 a_bogus.js（SM3 + RC4 独立实现）
            using var abStream = assembly.GetManifestResourceStream("DouyinTTS.Core.Live.Protocol.a_bogus.js")
                ?? throw new FileNotFoundException("a_bogus.js embedded resource not found");
            using var abReader = new StreamReader(abStream);
            var abCode = abReader.ReadToEnd();
            _engine.Execute(abCode);
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
    /// 生成 a_bogus 签名（用于 HTTP im/fetch 请求）
    /// </summary>
    public static string GetABogus(string queryParams, string userAgent)
    {
        lock (_lock)
        {
            if (_engine == null) return "";

            try
            {
                // 转义参数中的单引号和反斜杠
                var escapedParams = queryParams.Replace("\\", "\\\\").Replace("'", "\\'");
                var escapedUA = userAgent.Replace("\\", "\\\\").Replace("'", "\\'");

                // 构建 window_env_str（与 douyinLive 一致的浏览器环境指纹）
                var envStr = "1920|1080|1920|1040|0|30|0|0|1872|92|1920|1040|1857|92|1|24|Win32";
                var escapedEnv = envStr.Replace("'", "\\'");

                // 调用 a_bogus.js 中的 generate_a_bogus 函数
                var result = _engine.Evaluate($"generate_a_bogus('{escapedParams}', '{escapedUA}', '{escapedEnv}')")?.ToString();
                return result ?? "";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ABogus] 计算失败: {ex.Message}");
                return "";
            }
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

    public static string BuildWebSocketUrl(string roomId, string pushId, string userAgent, long fetchTime,
        string? realCursor = null, string? realInternalExt = null)
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
        // 使用与 douyinLive 一致的 cursor（硬编码旧时间戳）
        p.Add("cursor", realCursor ?? $"d-1_u-1_fh-{cursorFh}_t-1719159695790_r-1");
        p.Add("internal_ext", realInternalExt ?? $"internal_src:dim|wss_push_room_id:{roomId}|wss_push_did:{pushId}|first_req_ms:{fetchTime}|fetch_time:{fetchTime}|seq:1|wss_info:0-{fetchTime}-0-0|wrds_v:{wrdsV}");
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
        System.Diagnostics.Debug.WriteLine($"[签名详情] md5Stub={md5Stub}, signature={signature}");

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
