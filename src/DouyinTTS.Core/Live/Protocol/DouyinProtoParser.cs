using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;
using ProtoBuf;

namespace DouyinTTS.Core.Live.Protocol;

public class DouyinProtoParser
{
    private readonly ILogger? _logger;

    public DouyinProtoParser(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 编码一个 PushFrame 为 WebSocket 二进制消息
    /// </summary>
    public static byte[] EncodeFrame(PushFrame frame)
    {
        using var ms = new MemoryStream();
        Serializer.Serialize(ms, frame);
        return ms.ToArray();
    }

    /// <summary>
    /// 解码 WebSocket 二进制消息为 PushFrame
    /// </summary>
    public static PushFrame? DecodeFrame(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            return Serializer.Deserialize<PushFrame>(ms);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 PushFrame 解析出 Response（gzip 解压后 protobuf 解码）
    /// </summary>
    public Response? ParseResponse(PushFrame frame)
    {
        try
        {
            if (frame.Payload == null || frame.Payload.Length == 0)
                return null;

            byte[] payload = frame.Payload;

            // 检查是否需要 gzip 解压
            if (frame.PayloadEncoding == "gzip" || IsGzip(payload))
            {
                payload = DecompressGzip(payload);
            }

            using var ms = new MemoryStream(payload);
            return Serializer.Deserialize<Response>(ms);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "解析 Response 失败");
            return null;
        }
    }

    /// <summary>
    /// 从 Response 中提取所有 LiveEvent
    /// </summary>
    public List<Models.LiveEvent> ExtractEvents(Response response)
    {
        var events = new List<Models.LiveEvent>();

        if (response.MessagesList == null) return events;

        foreach (var msg in response.MessagesList)
        {
            try
            {
                var liveEvent = ParseMessage(msg);
                if (liveEvent != null)
                {
                    events.Add(liveEvent);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "解析消息失败: {Method}", msg.Method);
            }
        }

        return events;
    }

    private Models.LiveEvent? ParseMessage(Message msg)
    {
        if (msg.Payload == null || msg.Payload.Length == 0)
            return null;

        using var ms = new MemoryStream(msg.Payload);

        return msg.Method switch
        {
            "WebcastChatMessage" => ParseChatMessage(ms),
            "WebcastGiftMessage" => ParseGiftMessage(ms),
            "WebcastMemberMessage" => ParseMemberMessage(ms),
            "WebcastLikeMessage" => ParseLikeMessage(ms),
            "WebcastRoomMessage" => ParseRoomMessage(ms),
            _ => null
        };
    }

    private Models.DanmakuEvent ParseChatMessage(MemoryStream ms)
    {
        var chat = Serializer.Deserialize<ChatMessage>(ms);
        return new Models.DanmakuEvent
        {
            UserId = chat.Common?.User?.ToString() ?? string.Empty,
            UserName = chat.Common?.DisplayText ?? string.Empty,
            Content = chat.Content
        };
    }

    private Models.GiftEvent ParseGiftMessage(MemoryStream ms)
    {
        var gift = Serializer.Deserialize<GiftMessage>(ms);
        return new Models.GiftEvent
        {
            UserId = gift.User?.Id.ToString() ?? string.Empty,
            UserName = gift.User?.Nickname ?? string.Empty,
            GiftName = gift.Gift?.GiftName ?? "礼物",
            GiftCount = (int)(gift.GroupCount ?? 1),
            GiftValue = gift.FanTicketCount ?? 0
        };
    }

    private Models.MemberEvent ParseMemberMessage(MemoryStream ms)
    {
        var member = Serializer.Deserialize<MemberMessage>(ms);
        return new Models.MemberEvent
        {
            UserId = member.User?.Id.ToString() ?? string.Empty,
            UserName = member.User?.Nickname ?? string.Empty
        };
    }

    private Models.LikeEvent ParseLikeMessage(MemoryStream ms)
    {
        var like = Serializer.Deserialize<LikeMessage>(ms);
        return new Models.LikeEvent
        {
            UserId = like.User?.Id.ToString() ?? string.Empty,
            UserName = like.User?.Nickname ?? string.Empty,
            Count = (int)(like.Count ?? 1)
        };
    }

    private Models.SystemEvent ParseRoomMessage(MemoryStream ms)
    {
        var room = Serializer.Deserialize<WebcastRoomMessage>(ms);
        return new Models.SystemEvent
        {
            Message = room.Common?.Describe?.Text ?? "系统消息"
        };
    }

    /// <summary>
    /// 构建加入房间的请求包
    /// </summary>
    public static byte[] BuildJoinRoomPayload(long roomId, string cookie, string token)
    {
        var webcastId = $"live_id=1,aid=6383,version_code=180200";

        var frame = new PushFrame
        {
            SeqId = 1,
            LogId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Service = 1,
            Method = 2,
            PayloadEncoding = "protobuf",
            HeadersList = new HeadersList
            {
                Headers =
                [
                    new HeadersEntry { Key = "webcast_aid", Value = "6383" },
                    new HeadersEntry { Key = "webcast_language", Value = "zh" },
                    new HeadersEntry { Key = "live_id", Value = roomId.ToString() },
                    new HeadersEntry { Key = "cookie", Value = cookie },
                    new HeadersEntry { Key = "token", Value = token },
                ]
            }
        };

        return EncodeFrame(frame);
    }

    /// <summary>
    /// 构建心跳包
    /// </summary>
    public static byte[] BuildHeartbeatPayload()
    {
        var frame = new PushFrame
        {
            SeqId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LogId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Service = 1,
            Method = 1,
            PayloadEncoding = string.Empty,
            Payload = []
        };

        return EncodeFrame(frame);
    }

    /// <summary>
    /// 构建 ACK 响应包
    /// </summary>
    public static byte[] BuildAckPayload(long logId)
    {
        var frame = new PushFrame
        {
            SeqId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LogId = logId,
            Service = 1,
            Method = 4,
            PayloadEncoding = string.Empty,
            Payload = []
        };

        return EncodeFrame(frame);
    }

    private static bool IsGzip(byte[] data)
    {
        return data.Length >= 2 && data[0] == 0x1f && data[1] == 0x8b;
    }

    private static byte[] DecompressGzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
