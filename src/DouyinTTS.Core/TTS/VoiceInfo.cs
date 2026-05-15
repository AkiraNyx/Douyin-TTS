namespace DouyinTTS.Core.TTS;

public class VoiceInfo
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Locale { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;

    public static List<VoiceInfo> GetChineseVoices() =>
    [
        new() { Name = "zh-CN-XiaoxiaoNeural", DisplayName = "晓晓（女）", Locale = "zh-CN", Gender = "Female" },
        new() { Name = "zh-CN-YunxiNeural", DisplayName = "云希（男）", Locale = "zh-CN", Gender = "Male" },
        new() { Name = "zh-CN-YunjianNeural", DisplayName = "云健（男）", Locale = "zh-CN", Gender = "Male" },
        new() { Name = "zh-CN-XiaoyiNeural", DisplayName = "晓依（女）", Locale = "zh-CN", Gender = "Female" },
        new() { Name = "zh-CN-YunyangNeural", DisplayName = "云扬（男）", Locale = "zh-CN", Gender = "Male" },
        new() { Name = "zh-CN-XiaochenNeural", DisplayName = "晓辰（女）", Locale = "zh-CN", Gender = "Female" },
        new() { Name = "zh-CN-XiaohanNeural", DisplayName = "晓涵（女）", Locale = "zh-CN", Gender = "Female" },
        new() { Name = "zh-CN-XiaomengNeural", DisplayName = "晓梦（女）", Locale = "zh-CN", Gender = "Female" },
        new() { Name = "zh-CN-XiaomoNeural", DisplayName = "晓墨（女）", Locale = "zh-CN", Gender = "Female" },
        new() { Name = "zh-CN-XiaoqiuNeural", DisplayName = "晓秋（女）", Locale = "zh-CN", Gender = "Female" },
        new() { Name = "zh-CN-XiaoruiNeural", DisplayName = "晓睿（女）", Locale = "zh-CN", Gender = "Female" },
        new() { Name = "zh-CN-XiaoshuangNeural", DisplayName = "晓双（女）", Locale = "zh-CN", Gender = "Female" },
        new() { Name = "zh-CN-XiaoyanNeural", DisplayName = "晓颜（女）", Locale = "zh-CN", Gender = "Female" },
        new() { Name = "zh-CN-XiaozhenNeural", DisplayName = "晓甄（女）", Locale = "zh-CN", Gender = "Female" },
        new() { Name = "zh-CN-YunfengNeural", DisplayName = "云枫（男）", Locale = "zh-CN", Gender = "Male" },
        new() { Name = "zh-CN-YunhaoNeural", DisplayName = "云皓（男）", Locale = "zh-CN", Gender = "Male" },
        new() { Name = "zh-CN-YunxiaNeural", DisplayName = "云夏（男）", Locale = "zh-CN", Gender = "Male" },
        new() { Name = "zh-CN-YunzeNeural", DisplayName = "云泽（男）", Locale = "zh-CN", Gender = "Male" },
    ];

    public override string ToString() => DisplayName;
}
