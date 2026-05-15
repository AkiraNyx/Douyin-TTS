namespace DouyinTTS.Core.Live.Models;

public enum LiveEventType
{
    Danmaku,
    Gift,
    Member,
    Like,
    System
}

public abstract class LiveEvent
{
    public LiveEventType Type { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string UserId { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
}

public class DanmakuEvent : LiveEvent
{
    public DanmakuEvent() => Type = LiveEventType.Danmaku;
    public string Content { get; init; } = string.Empty;
}

public class GiftEvent : LiveEvent
{
    public GiftEvent() => Type = LiveEventType.Gift;
    public string GiftName { get; init; } = string.Empty;
    public int GiftCount { get; init; }
    public long GiftValue { get; init; }
}

public class MemberEvent : LiveEvent
{
    public MemberEvent() => Type = LiveEventType.Member;
}

public class LikeEvent : LiveEvent
{
    public LikeEvent() => Type = LiveEventType.Like;
    public int Count { get; init; }
}

public class SystemEvent : LiveEvent
{
    public SystemEvent() => Type = LiveEventType.System;
    public string Message { get; init; } = string.Empty;
}
