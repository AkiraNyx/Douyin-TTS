using ProtoBuf;

namespace DouyinTTS.Core.Live.Protocol;

// 抖音弹幕 WebSocket 协议结构（Protobuf 定义）

[ProtoContract]
public class PushFrame
{
    [ProtoMember(1)] public long SeqId { get; set; }
    [ProtoMember(2)] public long LogId { get; set; }
    [ProtoMember(3)] public long Service { get; set; }
    [ProtoMember(4)] public long Method { get; set; }
    [ProtoMember(5)] public HeadersList? HeadersList { get; set; }
    [ProtoMember(6)] public string PayloadEncoding { get; set; } = string.Empty;
    [ProtoMember(7)] public string PayloadType { get; set; } = string.Empty;
    [ProtoMember(8)] public byte[] Payload { get; set; } = [];
}

[ProtoContract]
public class HeadersList
{
    [ProtoMember(1)] public List<HeadersEntry> Headers { get; set; } = [];
}

[ProtoContract]
public class HeadersEntry
{
    [ProtoMember(1)] public string Key { get; set; } = string.Empty;
    [ProtoMember(2)] public string Value { get; set; } = string.Empty;
}

[ProtoContract]
public class Response
{
    [ProtoMember(1)] public List<Message> MessagesList { get; set; } = [];
    [ProtoMember(2)] public string Cursor { get; set; } = string.Empty;
    [ProtoMember(3)] public long FetchInterval { get; set; }
    [ProtoMember(4)] public long Now { get; set; }
    [ProtoMember(5)] public string InternalExt { get; set; } = string.Empty;
    [ProtoMember(6)] public int FetchType { get; set; }
    [ProtoMember(7)] public RouteParamsMap? RouteParams { get; set; }
    [ProtoMember(8)] public long HeartbeatDuration { get; set; }
    [ProtoMember(9)] public int NeedAck { get; set; }
    [ProtoMember(10)] public string PushServer { get; set; } = string.Empty;
}

[ProtoContract]
public class RouteParamsMap
{
    [ProtoMember(1)] public List<RouteParamsEntry> Params { get; set; } = [];
}

[ProtoContract]
public class RouteParamsEntry
{
    [ProtoMember(1)] public string Key { get; set; } = string.Empty;
    [ProtoMember(2)] public string Value { get; set; } = string.Empty;
}

[ProtoContract]
public class Message
{
    [ProtoMember(1)] public string Method { get; set; } = string.Empty;
    [ProtoMember(2)] public byte[] Payload { get; set; } = [];
    [ProtoMember(3)] public long MsgId { get; set; }
    [ProtoMember(4)] public long MsgType { get; set; }
    [ProtoMember(5)] public long Offset { get; set; }
    [ProtoMember(6)] public bool NeedWrdsStore { get; set; }
    [ProtoMember(7)] public long WsTime { get; set; }
    [ProtoMember(9)] public long InternalExt { get; set; }
    [ProtoMember(10)] public long NotifyType { get; set; }
    [ProtoMember(11)] public long Rid { get; set; }
}

[ProtoContract]
public class ChatMessage
{
    [ProtoMember(1)] public CommonInfo? Common { get; set; }
    [ProtoMember(2)] public UserInfo? User { get; set; }
    [ProtoMember(3)] public string Content { get; set; } = string.Empty;
    [ProtoMember(4)] public long? Priority { get; set; }
    [ProtoMember(5)] public long? Duration { get; set; }
}

[ProtoContract]
public class GiftMessage
{
    [ProtoMember(1)] public CommonInfo? Common { get; set; }
    [ProtoMember(2)] public GiftIdInfo? GiftId { get; set; }
    [ProtoMember(3)] public long? FanTicketCount { get; set; }
    [ProtoMember(4)] public long? GroupCount { get; set; }
    [ProtoMember(5)] public long? RepeatCount { get; set; }
    [ProtoMember(6)] public long? ComboCount { get; set; }
    [ProtoMember(7)] public UserInfo? User { get; set; }
    [ProtoMember(8)] public UserInfo? ToUser { get; set; }
    [ProtoMember(9)] public long? RepeatEnd { get; set; }
    [ProtoMember(10)] public TextEffectInfo? TextEffect { get; set; }
    [ProtoMember(11)] public long? GroupId { get; set; }
    [ProtoMember(12)] public long? IncomeTaskgifts { get; set; }
    [ProtoMember(13)] public long? RoomFanTicketCount { get; set; }
    [ProtoMember(14)] public GiftInfo? Gift { get; set; }
}

[ProtoContract]
public class GiftIdInfo
{
    [ProtoMember(1)] public long? GiftId { get; set; }
    [ProtoMember(2)] public long? FanTicket { get; set; }
    [ProtoMember(3)] public long? GroupId { get; set; }
    [ProtoMember(4)] public long? GiftDetails { get; set; }
    [ProtoMember(5)] public long? MonitorScene { get; set; }
}

[ProtoContract]
public class GiftInfo
{
    [ProtoMember(1)] public long? GiftId { get; set; }
    [ProtoMember(2)] public string GiftName { get; set; } = string.Empty;
}

[ProtoContract]
public class TextEffectInfo
{
    [ProtoMember(1)] public TextEffectDetail? PortraitDetail { get; set; }
    [ProtoMember(2)] public TextEffectDetail? LandscapeDetail { get; set; }
}

[ProtoContract]
public class TextEffectDetail
{
    [ProtoMember(1)] public List<TextPiece>? TextPieces { get; set; }
}

[ProtoContract]
public class TextPiece
{
    [ProtoMember(1)] public string? Text { get; set; }
}

[ProtoContract]
public class CommonInfo
{
    [ProtoMember(1)] public long? Method { get; set; }
    [ProtoMember(2)] public long? MsgId { get; set; }
    [ProtoMember(3)] public long? RoomId { get; set; }
    [ProtoMember(4)] public long? CreateTime { get; set; }
    [ProtoMember(5)] public long? Monitor { get; set; }
    [ProtoMember(6)] public long? IsShowMsg { get; set; }
    [ProtoMember(7)] public DescribeInfo? Describe { get; set; }
    [ProtoMember(8)] public string? DisplayText { get; set; }
    [ProtoMember(9)] public long? FoldType { get; set; }
    [ProtoMember(10)] public long? AnchorFoldType { get; set; }
    [ProtoMember(11)] public long? PriorityScore { get; set; }
    [ProtoMember(12)] public long? LogId { get; set; }
    [ProtoMember(13)] public long? MsgProcessFilterK { get; set; }
    [ProtoMember(14)] public long? MsgProcessFilterV { get; set; }
    [ProtoMember(15)] public long? User { get; set; }
    [ProtoMember(16)] public long? Room { get; set; }
}

[ProtoContract]
public class DescribeInfo
{
    [ProtoMember(1)] public string? Text { get; set; }
}

[ProtoContract]
public class UserInfo
{
    [ProtoMember(1)] public long Id { get; set; }
    [ProtoMember(2)] public long ShortId { get; set; }
    [ProtoMember(3)] public string Nickname { get; set; } = string.Empty;
    [ProtoMember(4)] public int Gender { get; set; }
    [ProtoMember(9)] public string? SecUid { get; set; }
    [ProtoMember(11)] public string? Level { get; set; }
    [ProtoMember(16)] public PayGradeInfo? PayGrade { get; set; }
    [ProtoMember(17)] public FansClubInfo? FansClub { get; set; }
}

[ProtoContract]
public class PayGradeInfo
{
    [ProtoMember(1)] public long Level { get; set; }
    [ProtoMember(2)] public string? Name { get; set; }
}

[ProtoContract]
public class FansClubInfo
{
    [ProtoMember(1)] public long Level { get; set; }
    [ProtoMember(2)] public string? Name { get; set; }
}

[ProtoContract]
public class MemberMessage
{
    [ProtoMember(1)] public CommonInfo? Common { get; set; }
    [ProtoMember(2)] public UserInfo? User { get; set; }
    [ProtoMember(3)] public long? MemberCount { get; set; }
    [ProtoMember(4)] public long? Operator { get; set; }
    [ProtoMember(5)] public long? IsSetToAdmin { get; set; }
    [ProtoMember(6)] public long? IsTopUser { get; set; }
    [ProtoMember(7)] public long? RankScore { get; set; }
    [ProtoMember(8)] public long? TopUserNo { get; set; }
    [ProtoMember(9)] public long? EnterType { get; set; }
    [ProtoMember(10)] public long? Action { get; set; }
}

[ProtoContract]
public class LikeMessage
{
    [ProtoMember(1)] public CommonInfo? Common { get; set; }
    [ProtoMember(2)] public UserInfo? User { get; set; }
    [ProtoMember(3)] public long? Count { get; set; }
    [ProtoMember(4)] public long? Total { get; set; }
    [ProtoMember(5)] public long? Color { get; set; }
}

[ProtoContract]
public class WebcastRoomMessage
{
    [ProtoMember(1)] public CommonInfo? Common { get; set; }
    [ProtoMember(2)] public long? RoomId { get; set; }
    [ProtoMember(3)] public long? Status { get; set; }
}

[ProtoContract]
public class SocialMessage
{
    [ProtoMember(1)] public CommonInfo? Common { get; set; }
    [ProtoMember(2)] public UserInfo? User { get; set; }
    [ProtoMember(3)] public long? ShareType { get; set; }
    [ProtoMember(4)] public long? Action { get; set; }
    [ProtoMember(5)] public string? ShareTarget { get; set; }
}
