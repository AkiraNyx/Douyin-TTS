namespace DouyinTTS.Core.Live.Models;

public enum LiveEventType
{
    Danmaku,
    Gift,
    Member,
    Like,
    System,
    Debug
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
    public int TotalLiked { get; init; }
}

public class SystemEvent : LiveEvent
{
    public SystemEvent() => Type = LiveEventType.System;
    public string Message { get; init; } = string.Empty;
}

public class RoomStatsEvent : LiveEvent
{
    public RoomStatsEvent() => Type = LiveEventType.System;
    public int ViewerCount { get; init; }
}

public class DebugEvent : LiveEvent
{
    public DebugEvent() => Type = LiveEventType.Debug;
    public string Method { get; init; } = string.Empty;
    public string? Error { get; init; }
    public int PayloadSize { get; init; }
}
