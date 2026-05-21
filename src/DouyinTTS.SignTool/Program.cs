using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.ClearScript.V8;

// Read JSON from stdin: { "md5": "..." }
var input = Console.In.ReadToEnd();
var doc = JsonDocument.Parse(input);
var md5 = doc.RootElement.GetProperty("md5").GetString()!;

// Load webmssdk.js
var assembly = Assembly.GetExecutingAssembly();
using var stream = assembly.GetManifestResourceStream("DouyinTTS.SignTool.webmssdk.js")
    ?? assembly.GetManifestResourceStream("webmssdk.js")
    ?? throw new FileNotFoundException("webmssdk.js not found");
using var reader = new StreamReader(stream);
var jsCode = reader.ReadToEnd();

var ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

using var engine = new V8ScriptEngine();
engine.Evaluate($@"
var navigator = {{ userAgent: '{ua}', appVersion: '5.0 (Windows)' }};
var window = globalThis;
var document = {{ createElement: function() {{ return {{}} }}, cookie: '', getElementById: function() {{ return null }} }};
var location = {{ href: 'https://live.douyin.com/', protocol: 'https:', host: 'live.douyin.com' }};
var screen = {{ width: 1920, height: 1080 }};
" + jsCode);

var result = engine.Script.get_sign(new Dictionary<string, object> { ["X-MS-STUB"] = md5 });
Console.Write(result?.ToString() ?? "");
