using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DouyinTTS.Core.Live.Protocol;

/// <summary>
/// 通用 protobuf 字段值（手动解析，兼容任何 wire type）
/// </summary>
public readonly struct ProtoField
{
    public int FieldNumber { get; init; }
    public int WireType { get; init; }
    public ulong Varint { get; init; }
    public byte[]? Bytes { get; init; }

    public long AsInt64 => (long)Varint;
    public int AsInt32 => (int)Varint;
    public string AsString => Bytes != null ? Encoding.UTF8.GetString(Bytes) : string.Empty;
    public bool AsBool => Varint != 0;
}

public class DouyinProtoParser
{
    private readonly ILogger? _logger;

    public DouyinProtoParser(ILogger? logger = null)
    {
        _logger = logger;
    }

    #region 通用 protobuf 解析

    public static List<ProtoField> ReadFields(byte[] data, int offset = 0, int length = -1)
    {
        var fields = new List<ProtoField>();
        var pos = offset;
        var end = length < 0 ? data.Length : offset + length;

        while (pos < end)
        {
            if (!TryReadVarint(data, ref pos, end, out var tag)) break;
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);

            switch (wireType)
            {
                case 0: // varint
                    if (!TryReadVarint(data, ref pos, end, out var val)) return fields;
                    fields.Add(new ProtoField { FieldNumber = fieldNumber, WireType = 0, Varint = val });
                    break;

                case 2: // length-delimited
                    if (!TryReadVarint(data, ref pos, end, out var lenU)) return fields;
                    var len = (int)lenU;
                    if (len > end - pos) return fields;
                    var bytes = new byte[len];
                    Array.Copy(data, pos, bytes, 0, len);
                    pos += len;
                    fields.Add(new ProtoField { FieldNumber = fieldNumber, WireType = 2, Bytes = bytes });
                    break;

                case 5: // 32-bit fixed
                    if (pos + 4 > end) return fields;
                    fields.Add(new ProtoField { FieldNumber = fieldNumber, WireType = 5, Varint = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4)) });
                    pos += 4;
                    break;

                case 1: // 64-bit fixed
                    if (pos + 8 > end) return fields;
                    fields.Add(new ProtoField { FieldNumber = fieldNumber, WireType = 1, Varint = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos, 8)) });
                    pos += 8;
                    break;

                default:
                    return fields;
            }
        }

        return fields;
    }

    public static List<ProtoField> ReadSubMessage(List<ProtoField> fields, int fieldNumber)
    {
        var f = fields.LastOrDefault(x => x.FieldNumber == fieldNumber && x.WireType == 2);
        return f.Bytes != null ? ReadFields(f.Bytes) : new List<ProtoField>();
    }

    public static List<List<ProtoField>> ReadSubMessages(List<ProtoField> fields, int fieldNumber)
    {
        return fields
            .Where(x => x.FieldNumber == fieldNumber && x.WireType == 2 && x.Bytes != null)
            .Select(x => ReadFields(x.Bytes!))
            .ToList();
    }

    public static long GetInt64(List<ProtoField> fields, int fieldNumber, long defaultValue = 0)
    {
        var f = fields.LastOrDefault(x => x.FieldNumber == fieldNumber && x.WireType == 0);
        return f.FieldNumber == fieldNumber ? f.AsInt64 : defaultValue;
    }

    public static string GetString(List<ProtoField> fields, int fieldNumber, string defaultValue = "")
    {
        var f = fields.LastOrDefault(x => x.FieldNumber == fieldNumber && x.WireType == 2);
        return f.FieldNumber == fieldNumber ? f.AsString : defaultValue;
    }

    public static byte[] GetBytes(List<ProtoField> fields, int fieldNumber)
    {
        var f = fields.LastOrDefault(x => x.FieldNumber == fieldNumber && x.WireType == 2);
        return f.Bytes ?? [];
    }

    private static bool TryReadVarint(byte[] data, ref int pos, int end, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < end)
        {
            var b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift >= 64) return false;
        }
        return false;
    }

    #endregion

    #region 编码方法（protobuf-net 用于编码）

    public static byte[] BuildHeartbeatPayload()
    {
        // 只发送 payloadType="hb"，4 字节
        // field 7 (payloadType), wire type 2: tag = (7 << 3) | 2 = 0x3A
        return [0x3A, 0x02, 0x68, 0x62];
    }

    public static byte[] BuildAckPayload(long logId, string internalExt = "")
    {
        // ACK: logId + payloadType="ack" + payload=internalExt
        using var ms = new MemoryStream();
        // field 2 (logId), wire type 0: tag = (2 << 3) | 0 = 0x10
        WriteVarint(ms, 0x10);
        WriteVarint(ms, (ulong)logId);
        // field 7 (payloadType), wire type 2
        WriteTag(ms, 7, 2);
        WriteString(ms, "ack");
        // field 8 (payload), wire type 2
        if (!string.IsNullOrEmpty(internalExt))
        {
            var extBytes = Encoding.UTF8.GetBytes(internalExt);
            WriteTag(ms, 8, 2);
            WriteVarint(ms, (ulong)extBytes.Length);
            ms.Write(extBytes, 0, extBytes.Length);
        }
        return ms.ToArray();
    }

    private static void WriteTag(Stream stream, int fieldNumber, int wireType)
    {
        WriteVarint(stream, (ulong)((fieldNumber << 3) | wireType));
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value > 0x7F)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    #endregion

    #region PushFrame 解析

    public static PushFrame? DecodeFrame(byte[] data)
    {
        var frame = TryParsePushFrame(data, 0, data.Length);
        if (frame != null) return frame;

        if (data.Length > 4)
        {
            frame = TryParsePushFrame(data, 4, data.Length - 4);
            if (frame != null) return frame;
        }

        if (IsGzip(data))
        {
            try
            {
                var decompressed = DecompressGzip(data);
                frame = TryParsePushFrame(decompressed, 0, decompressed.Length);
                if (frame != null) return frame;
            }
            catch { }
        }

        return null;
    }

    private static PushFrame? TryParsePushFrame(byte[] data, int offset, int length)
    {
        try
        {
            var fields = ReadFields(data, offset, length);
            if (fields.Count == 0) return null;

            var headersFields = ReadSubMessage(fields, 5);
            HeadersList? headersList = null;
            if (headersFields.Count > 0)
            {
                headersList = new HeadersList();
                var entries = ReadSubMessages(headersFields, 1);
                foreach (var entryFields in entries)
                {
                    headersList.Headers.Add(new HeadersEntry
                    {
                        Key = GetString(entryFields, 1),
                        Value = GetString(entryFields, 2)
                    });
                }
            }

            return new PushFrame
            {
                SeqId = GetInt64(fields, 1),
                LogId = GetInt64(fields, 2),
                Service = GetInt64(fields, 3),
                Method = GetInt64(fields, 4),
                HeadersList = headersList,
                PayloadEncoding = GetString(fields, 6),
                PayloadType = GetString(fields, 7),
                Payload = GetBytes(fields, 8)
            };
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Response 解析

    public Response? ParseResponse(PushFrame frame)
    {
        try
        {
            if (frame.Payload == null || frame.Payload.Length == 0)
                return null;

            byte[] payload = frame.Payload;
            if (frame.PayloadEncoding == "gzip" || IsGzip(payload))
                payload = DecompressGzip(payload);

            var fields = ReadFields(payload);
            var response = new Response
            {
                Cursor = GetString(fields, 2),
                FetchInterval = GetInt64(fields, 3),
                Now = GetInt64(fields, 4),
                InternalExt = GetString(fields, 5),
                FetchType = (int)GetInt64(fields, 6),
                HeartbeatDuration = GetInt64(fields, 8),
                NeedAck = (int)GetInt64(fields, 9),
                PushServer = GetString(fields, 10)
            };

            // 解析 MessagesList（field 1）
            var msgFieldsList = ReadSubMessages(fields, 1);
            foreach (var msgFields in msgFieldsList)
            {
                response.MessagesList.Add(new Message
                {
                    Method = GetString(msgFields, 1),
                    Payload = GetBytes(msgFields, 2),
                    MsgId = GetInt64(msgFields, 3),
                    MsgType = GetInt64(msgFields, 4),
                    Offset = GetInt64(msgFields, 5),
                    NeedWrdsStore = GetInt64(msgFields, 6) != 0,
                    WsTime = GetInt64(msgFields, 7),
                    InternalExt = GetInt64(msgFields, 9),
                    NotifyType = GetInt64(msgFields, 10),
                    Rid = GetInt64(msgFields, 11)
                });
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "解析 Response 失败");
            return null;
        }
    }

    #endregion

    #region 事件提取

    public List<Models.LiveEvent> ExtractEvents(Response response)
    {
        var events = new List<Models.LiveEvent>();
        if (response.MessagesList == null) return events;

        foreach (var msg in response.MessagesList)
        {
            try
            {
                // WebcastGiftSortMessage 返回多个事件
                if (msg.Method == "WebcastGiftSortMessage")
                {
                    var payloadLen = msg.Payload?.Length ?? 0;
                    if (payloadLen > 0)
                    {
                        var giftEvents = ParseGiftSortMessage(msg.Payload!);
                        events.AddRange(giftEvents);
                    }
                    continue;
                }

                var liveEvent = ParseMessage(msg);
                if (liveEvent != null)
                    events.Add(liveEvent);
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

        try
        {
            // 精确匹配已知类型
            var result = msg.Method switch
            {
                "WebcastChatMessage" => (Models.LiveEvent?)ParseChatMessage(msg.Payload),
                "WebcastGiftMessage" => ParseGiftMessage(msg.Payload),
                "WebcastSocialMessage" => ParseSocialMessage(msg.Payload),
                "WebcastMemberMessage" => ParseMemberMessage(msg.Payload),
                "WebcastLikeMessage" => ParseLikeMessage(msg.Payload),
                "WebcastRoomMessage" => ParseRoomMessage(msg.Payload),
                "WebcastChatLikeMessage" => ParseChatLikeMessage(msg.Payload),
                "WebcastLightGiftMessage" => ParseLightGiftMessageDebug(msg.Payload),
                "WebcastGiftPlayEventMessage" => ParseLightGiftMessage(msg.Payload),
                "WebcastGiftSortMessage" => null, // handled in ExtractEvents
                "WebcastRoomRankMessage" => ParseRoomRankMessage(msg.Payload),
                "WebcastRoomStatsMessage" => ParseRoomStatsMessage(msg.Payload),
                "WebcastCommonDotMessage" => ParseCommonDotMessage(msg.Payload),
                "WebcastRoomDataSyncMessage" => ParseRoomDataSyncMessage(msg.Payload),
                _ => null
            };

            // 如果没匹配到，检查是否是礼物相关类型（大小写不敏感）
            if (result == null && msg.Method.Contains("Gift", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogDebug("发现未处理的礼物类型: {Method}", msg.Method);
                result = ParseLightGiftMessage(msg.Payload);
            }

            // 其他未知类型转储字段
            result ??= ParseUnknownMessage(msg.Method, msg.Payload);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "{Method} 解析失败, payload={Len}B", msg.Method, msg.Payload.Length);
            return null;
        }
    }

    private static string GetDisplayText(List<ProtoField> commonFields)
    {
        var displayText = GetString(commonFields, 8);
        if (!string.IsNullOrEmpty(displayText)) return displayText;

        var describeFields = ReadSubMessage(commonFields, 7);
        return GetString(describeFields, 1);
    }

    private static (string id, string name) GetUserInfo(List<ProtoField> userFields)
    {
        var id = GetInt64(userFields, 1).ToString();
        var name = GetString(userFields, 3);
        return (id, name);
    }

    private Models.DanmakuEvent ParseChatMessage(byte[] data)
    {
        var fields = ReadFields(data);
        var userFields = ReadSubMessage(fields, 2);
        var (userId, userName) = GetUserInfo(userFields);
        var content = GetString(fields, 3);

        if (string.IsNullOrEmpty(userName))
            userName = GetDisplayText(ReadSubMessage(fields, 1));

        return new Models.DanmakuEvent
        {
            UserId = userId,
            UserName = userName,
            Content = content
        };
    }

    private Models.GiftEvent ParseGiftMessage(byte[] data)
    {
        var fields = ReadFields(data);
        var userFields = ReadSubMessage(fields, 7);
        var (userId, userName) = GetUserInfo(userFields);

        // field 15: GiftStruct（礼物详情），name 在 sub-field 16
        var giftFields = ReadSubMessage(fields, 15);
        var giftName = GetString(giftFields, 16);
        if (string.IsNullOrEmpty(giftName)) giftName = "礼物";

        var groupCount = GetInt64(fields, 4, 1);
        var repeatCount = GetInt64(fields, 5, 1);
        var fanTicket = GetInt64(fields, 3);

        return new Models.GiftEvent
        {
            UserId = userId,
            UserName = userName,
            GiftName = giftName,
            GiftCount = (int)(groupCount > 0 ? groupCount : repeatCount),
            GiftValue = fanTicket
        };
    }

    private Models.SystemEvent ParseSocialMessage(byte[] data)
    {
        var fields = ReadFields(data);

        // Common 在 field 1
        var commonFields = ReadSubMessage(fields, 1);
        // User 在 field 2
        var userFields = ReadSubMessage(fields, 2);
        var (_, userName) = GetUserInfo(userFields);

        // field 4: action (1=关注, 3=分享)
        var action = GetInt64(fields, 4);
        var actionText = action switch
        {
            1 => "关注了主播",
            3 => "分享了直播间",
            _ => "的社交消息"
        };

        // 从 Common.describe.text 或 Common.displayText 提取文本
        // 过滤掉二进制数据和过长的字符串
        var displayText = GetDisplayText(commonFields);
        displayText = SanitizeDisplayText(displayText);

        return new Models.SystemEvent
        {
            Message = string.IsNullOrEmpty(displayText) ? $"{userName}{actionText}" : displayText
        };
    }

    private static bool IsReadableText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        // 必须全部是可打印字符：ASCII 可见字符、中文、全角字符
        foreach (var c in text)
        {
            if (!((c >= ' ' && c < 0x7F) ||       // ASCII 可见
                  (c >= 0x4E00 && c <= 0x9FFF) || // CJK 统一汉字
                  (c >= 0x3000 && c <= 0x303F) || // CJK 标点
                  (c >= 0xFF00 && c <= 0xFFEF) || // 全角字符
                  (c >= 0x2000 && c <= 0x206F) || // 通用标点
                  c == '\r' || c == '\n' || c == '\t'))
                return false;
        }
        return true;
    }

    private static string SanitizeDisplayText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // 移除不可打印字符，只保留可读文本
        var cleaned = new string(text.Where(c =>
            (c >= ' ' && c < 0x7F) ||
            (c >= 0x4E00 && c <= 0x9FFF) ||
            (c >= 0x3000 && c <= 0x303F) ||
            (c >= 0xFF00 && c <= 0xFFEF)).ToArray());
        // 如果清理后太短，可能是误解析
        if (cleaned.Length < 2) return string.Empty;
        // 限制长度
        if (cleaned.Length > 100) cleaned = cleaned[..100] + "...";
        return cleaned;
    }

    private Models.MemberEvent ParseMemberMessage(byte[] data)
    {
        var fields = ReadFields(data);
        var userFields = ReadSubMessage(fields, 2);
        var (userId, userName) = GetUserInfo(userFields);

        return new Models.MemberEvent
        {
            UserId = userId,
            UserName = userName
        };
    }

    private Models.LikeEvent ParseLikeMessage(byte[] data)
    {
        var fields = ReadFields(data);
        var userFields = ReadSubMessage(fields, 2);
        var (userId, userName) = GetUserInfo(userFields);
        // field 2: count（本次点赞数），field 3: total（累计点赞数）
        var count = GetInt64(fields, 2, 1);
        var total = GetInt64(fields, 3, 0);

        return new Models.LikeEvent
        {
            UserId = userId,
            UserName = userName,
            Count = (int)count,
            TotalLiked = (int)total
        };
    }

    private Models.SystemEvent ParseRoomMessage(byte[] data)
    {
        var fields = ReadFields(data);
        var commonFields = ReadSubMessage(fields, 1);
        var text = GetDisplayText(commonFields);

        return new Models.SystemEvent
        {
            Message = string.IsNullOrEmpty(text) ? "系统消息" : text
        };
    }

    private Models.LikeEvent ParseChatLikeMessage(byte[] data)
    {
        var fields = ReadFields(data);
        var userFields = ReadSubMessage(fields, 2);
        var (userId, userName) = GetUserInfo(userFields);
        var count = GetInt64(fields, 3, 1);

        return new Models.LikeEvent
        {
            UserId = userId,
            UserName = userName,
            Count = (int)count
        };
    }

    private Models.LiveEvent ParseLightGiftMessageDebug(byte[] data)
    {
        try
        {
            return ParseLightGiftMessage(data);
        }
        catch (Exception ex)
        {
            return new Models.DebugEvent
            {
                Method = "LightGift_ERR",
                PayloadSize = data.Length,
                Error = ex.Message
            };
        }
    }

    private Models.GiftEvent ParseLightGiftMessage(byte[] data)
    {
        var fields = ReadFields(data);

        // 遍历所有字段，提取用户名和礼物名称
        var userName = "";
        var giftName = "";
        var userId = "";

        foreach (var f in fields)
        {
            if (f.WireType == 2 && f.Bytes != null)
            {
                // 检查是否是 User 子消息（包含 id 和 nickname）
                try
                {
                    var subFields = ReadFields(f.Bytes);
                    var id = GetInt64(subFields, 1);
                    var name = GetString(subFields, 3);
                    if (id > 0 && !string.IsNullOrEmpty(name) && IsReadableText(name))
                    {
                        userId = id.ToString();
                        userName = name;
                    }
                }
                catch { }

                // 检查字符串字段是否是礼物名称（必须是可读文本）
                var str = f.AsString;
                if (string.IsNullOrEmpty(giftName) && IsReadableText(str) &&
                    str.Length > 0 && str.Length < 30 &&
                    !str.StartsWith("Webcast") && !str.StartsWith("http") &&
                    !str.Contains("webcast") && !str.Contains("image") &&
                    !str.Contains("png") && str != userName)
                {
                    giftName = str;
                }
            }
        }

        if (string.IsNullOrEmpty(giftName)) giftName = "礼物";
        if (string.IsNullOrEmpty(userName)) userName = "观众";

        var groupCount = GetInt64(fields, 4, 1);
        var repeatCount = GetInt64(fields, 5, 1);

        return new Models.GiftEvent
        {
            UserId = userId,
            UserName = userName,
            GiftName = giftName,
            GiftCount = (int)(groupCount > 0 ? groupCount : repeatCount),
            GiftValue = 0
        };
    }

    private List<Models.LiveEvent> ParseGiftSortMessage(byte[] data)
    {
        var events = new List<Models.LiveEvent>();
        var fields = ReadFields(data);

        // 策略1: 尝试用 GiftMessage 的字段结构直接解析
        // GiftMessage: User=field7, GiftStruct=field15(name=field16), repeatCount=field5, groupCount=field4
        // WebcastGiftSortMessage 可能与 GiftMessage 共享相同结构
        var directResult = TryParseAsGiftMessage(fields, data);
        if (directResult != null)
        {
            events.Add(directResult);
            return events;
        }

        // 策略2: 尝试将每个顶层子消息作为 GiftMessage 解析
        // (GiftSortMessage 可能是一个 repeated GiftMessage 容器)
        int giftEventCount = 0;
        for (int listField = 1; listField <= 10; listField++)
        {
            var subMsgs = ReadSubMessages(fields, listField);
            if (subMsgs.Count == 0) continue;

            foreach (var subFields in subMsgs)
            {
                // 先尝试作为 GiftMessage 解析
                var subResult = TryParseAsGiftMessage(subFields, null);
                if (subResult != null)
                {
                    events.Add(subResult);
                    giftEventCount++;
                    continue;
                }

                // 启发式解析
                var (userId, userName, giftName, giftCount) = ExtractGiftFromFields(subFields);
                if (!string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(giftName))
                {
                    events.Add(new Models.GiftEvent
                    {
                        UserId = userId,
                        UserName = userName,
                        GiftName = giftName,
                        GiftCount = giftCount,
                        GiftValue = 0
                    });
                    giftEventCount++;
                }
            }
        }

        // 策略3: 深度搜索
        if (giftEventCount == 0)
        {
            DeepSearchGifts(fields, events, 0);
        }

        return events;
    }

    /// <summary>
    /// 尝试用 GiftMessage 的字段结构解析（User=field7, GiftStruct=field15）
    /// </summary>
    private static Models.GiftEvent? TryParseAsGiftMessage(List<ProtoField> fields, byte[]? rawData)
    {
        // 检查是否有 GiftMessage 的标志性字段组合
        var userFields = ReadSubMessage(fields, 7);
        var giftStructFields = ReadSubMessage(fields, 15);

        var userId = GetInt64(userFields, 1);
        var userName = GetString(userFields, 3);
        var giftName = GetString(giftStructFields, 16);
        var giftId = GetInt64(giftStructFields, 5);

        // 需要至少有用户名或礼物名才算有效
        bool hasUser = userId > 0 && !string.IsNullOrEmpty(userName);
        bool hasGift = giftId > 0 && !string.IsNullOrEmpty(giftName);

        if (!hasUser && !hasGift)
            return null;

        if (string.IsNullOrEmpty(giftName)) giftName = "礼物";
        if (string.IsNullOrEmpty(userName)) userName = "观众";

        var groupCount = GetInt64(fields, 4, 1);
        var repeatCount = GetInt64(fields, 5, 1);

        return new Models.GiftEvent
        {
            UserId = userId.ToString(),
            UserName = userName,
            GiftName = giftName,
            GiftCount = (int)(groupCount > 0 ? groupCount : repeatCount),
            GiftValue = GetInt64(fields, 3)
        };
    }

    private static (string userId, string userName, string giftName, int giftCount) ExtractGiftFromFields(List<ProtoField> giftFields)
    {
        var userName = "";
        var giftName = "";
        var userId = "";
        var giftCount = 1;

        // 遍历所有子字段
        foreach (var f in giftFields)
        {
            if (f.WireType == 2 && f.Bytes != null)
            {
                // 尝试解析为 User 子消息
                try
                {
                    var subFields = ReadFields(f.Bytes);
                    var id = GetInt64(subFields, 1);
                    var name = GetString(subFields, 3);
                    // 如果有 id+nickname 结构，是用户信息
                    if (id > 0 && !string.IsNullOrEmpty(name) && name.Length < 30)
                    {
                        userId = id.ToString();
                        userName = name;
                    }

                    // 检查是否是 GiftStruct（field 5=id, field 16=name）
                    var gsId = GetInt64(subFields, 5);
                    var gsName = GetString(subFields, 16);
                    if (gsId > 0 && !string.IsNullOrEmpty(gsName) && string.IsNullOrEmpty(giftName))
                    {
                        giftName = gsName;
                    }

                    // 递归检查更深层的嵌套（GiftSort 的子消息可能包含 gift 详情）
                    foreach (var sf in subFields)
                    {
                        if (sf.WireType == 2 && sf.Bytes != null)
                        {
                            try
                            {
                                var innerFields = ReadFields(sf.Bytes);
                                var innerId = GetInt64(innerFields, 1);
                                var innerName = GetString(innerFields, 3);
                                if (innerId > 0 && !string.IsNullOrEmpty(innerName) && innerName.Length < 30 && string.IsNullOrEmpty(userName))
                                {
                                    userId = innerId.ToString();
                                    userName = innerName;
                                }
                                // GiftStruct（field 5=id, field 16=name）
                                var igId = GetInt64(innerFields, 5);
                                var igName = GetString(innerFields, 16);
                                if (igId > 0 && !string.IsNullOrEmpty(igName) && string.IsNullOrEmpty(giftName))
                                {
                                    giftName = igName;
                                }
                                // 查找礼物名称（通常在字符串字段中）
                                var innerStr = GetString(innerFields, 2);
                                if (string.IsNullOrEmpty(giftName) && !string.IsNullOrEmpty(innerStr) &&
                                    innerStr.Length > 0 && innerStr.Length < 30 &&
                                    !innerStr.StartsWith("Webcast") && !innerStr.StartsWith("http"))
                                {
                                    giftName = innerStr;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // 直接检查字符串字段是否是礼物名称
                var str = f.AsString;
                if (string.IsNullOrEmpty(giftName) &&
                    str.Length > 0 && str.Length < 30 &&
                    !str.StartsWith("Webcast") && !str.StartsWith("http") &&
                    !str.Contains('\0') && !str.Contains("webcast") &&
                    !str.Contains("image") && !str.Contains("png") &&
                    str != userName)
                {
                    giftName = str;
                }
            }
            else if (f.WireType == 0)
            {
                // 数量字段
                if (f.FieldNumber >= 2 && f.FieldNumber <= 8 && f.AsInt64 > 0 && f.AsInt64 < 100000)
                {
                    giftCount = (int)f.AsInt64;
                }
            }
        }

        return (userId, userName, giftName, giftCount);
    }

    private void DeepSearchGifts(List<ProtoField> fields, List<Models.LiveEvent> events, int depth)
    {
        if (depth > 4) return;

        foreach (var f in fields)
        {
            if (f.WireType != 2 || f.Bytes == null) continue;
            try
            {
                var subFields = ReadFields(f.Bytes);

                // 检查是否有 User 结构（field 1=id, field 3=nickname）
                var id = GetInt64(subFields, 1);
                var name = GetString(subFields, 3);
                var hasUser = id > 10000 && !string.IsNullOrEmpty(name) && name.Length < 30;

                // 检查是否有 GiftStruct（field 5=id, field 16=name）
                var gsId = GetInt64(subFields, 5);
                var gsName = GetString(subFields, 16);
                var hasGiftStruct = gsId > 0 && !string.IsNullOrEmpty(gsName);

                if (hasUser)
                {
                    // 找到用户信息
                    var gfName = hasGiftStruct ? gsName : "";

                    // 如果没有 GiftStruct，尝试在同层找礼物名称
                    if (string.IsNullOrEmpty(gfName))
                    {
                        foreach (var sf in subFields)
                        {
                            if (sf.WireType == 2 && sf.Bytes != null && sf.FieldNumber != 3)
                            {
                                try
                                {
                                    var innerFields = ReadFields(sf.Bytes);
                                    var igName = GetString(innerFields, 16);
                                    if (!string.IsNullOrEmpty(igName))
                                    {
                                        gfName = igName;
                                        break;
                                    }
                                }
                                catch { }

                                var str = sf.AsString;
                                if (string.IsNullOrEmpty(gfName) && !string.IsNullOrEmpty(str) && str.Length > 0 && str.Length < 30 &&
                                    !str.StartsWith("Webcast") && !str.StartsWith("http") &&
                                    !str.Contains('\0') && str != name)
                                {
                                    gfName = str;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(gfName)) gfName = "礼物";
                    events.Add(new Models.GiftEvent
                    {
                        UserId = id.ToString(),
                        UserName = name,
                        GiftName = gfName,
                        GiftCount = 1,
                        GiftValue = 0
                    });
                }
                else if (hasGiftStruct)
                {
                    events.Add(new Models.GiftEvent
                    {
                        GiftName = gsName,
                        GiftCount = 1,
                        GiftValue = 0
                    });
                }
                else
                {
                    // 递归搜索更深层
                    DeepSearchGifts(subFields, events, depth + 1);
                }
            }
            catch { }
        }
    }

    private Models.DebugEvent ParseRoomRankMessage(byte[] data)
    {
        var fields = ReadFields(data);

        // 尝试提取排名数据中的礼物信息
        var rankInfo = new List<string>();
        foreach (var f in fields)
        {
            if (f.WireType == 2 && f.Bytes != null)
            {
                try
                {
                    var innerFields = ReadFields(f.Bytes);
                    // 查找用户信息和分数
                    var userFields = ReadSubMessage(innerFields, 2);
                    var (_, userName) = GetUserInfo(userFields);
                    var score = GetInt64(innerFields, 1);
                    var rank = GetInt64(innerFields, 3);

                    if (!string.IsNullOrEmpty(userName) && score > 0)
                    {
                        rankInfo.Add($"#{rank} {userName}={score}");
                    }
                }
                catch { }
            }
        }

        if (rankInfo.Count > 0)
        {
            return new Models.DebugEvent
            {
                Method = "RoomRank",
                PayloadSize = data.Length,
                Error = $"排名: {string.Join(", ", rankInfo.Take(5))}"
            };
        }

        // 如果没有解析到排名数据，转储字段结构
        var fieldInfo = string.Join(", ", fields.Take(10).Select(f =>
            f.WireType == 0 ? $"{f.FieldNumber}={f.AsInt64}" :
            f.WireType == 2 ? $"{f.FieldNumber}[{f.Bytes?.Length ?? 0}B]" :
            $"{f.FieldNumber}=wt{f.WireType}"));
        return new Models.DebugEvent { Method = "RoomRank", PayloadSize = data.Length, Error = fieldInfo };
    }

    private Models.RoomStatsEvent ParseRoomStatsMessage(byte[] data)
    {
        var fields = ReadFields(data);
        // 尝试提取在线人数（不同 field number）
        var total = GetInt64(fields, 2);
        if (total == 0) total = GetInt64(fields, 3);
        if (total == 0) total = GetInt64(fields, 4);

        return new Models.RoomStatsEvent
        {
            ViewerCount = (int)total
        };
    }

    private Models.DebugEvent ParseCommonDotMessage(byte[] data)
    {
        var fields = ReadFields(data);
        var fieldInfo = string.Join(", ", fields.Take(15).Select(f =>
            f.WireType == 0 ? $"{f.FieldNumber}={f.AsInt64}" :
            f.WireType == 2 ? $"{f.FieldNumber}=\"{f.AsString[..Math.Min(30, f.AsString.Length)]}\"" :
            $"{f.FieldNumber}=wt{f.WireType}"));
        return new Models.DebugEvent { Method = "CommonDot", PayloadSize = data.Length, Error = fieldInfo };
    }

    private Models.DebugEvent ParseRoomDataSyncMessage(byte[] data)
    {
        var fields = ReadFields(data);
        // 检查是否有嵌套的 gift 数据
        var giftHints = new List<string>();
        foreach (var f in fields.Where(f => f.WireType == 2 && f.Bytes != null))
        {
            var innerFields = ReadFields(f.Bytes!);
            foreach (var inner in innerFields)
            {
                if (inner.WireType == 2 && inner.Bytes != null)
                {
                    var str = inner.AsString;
                    if (str.Contains("gift", StringComparison.OrdinalIgnoreCase) ||
                        str.Contains("Gift", StringComparison.Ordinal))
                        giftHints.Add($"f{f.FieldNumber}.f{inner.FieldNumber}=\"{str[..Math.Min(30, str.Length)]}\"");
                }
            }
        }
        var hint = giftHints.Count > 0 ? $" GIFT线索: [{string.Join(", ", giftHints)}]" : "";
        var fieldInfo = string.Join(", ", fields.Take(10).Select(f =>
            f.WireType == 0 ? $"{f.FieldNumber}={f.AsInt64}" :
            f.WireType == 2 ? $"{f.FieldNumber}[{f.Bytes?.Length ?? 0}B]" :
            $"{f.FieldNumber}=wt{f.WireType}"));
        return new Models.DebugEvent { Method = "RoomDataSync", PayloadSize = data.Length, Error = $"{fieldInfo}{hint}" };
    }

    private Models.DebugEvent? ParseUnknownMessage(string method, byte[] data)
    {
        // 转储未知消息的字段结构，帮助调试
        var fields = ReadFields(data);
        var fieldInfo = string.Join(", ", fields.Take(10).Select(f =>
            f.WireType == 0 ? $"{f.FieldNumber}={f.AsInt64}" :
            f.WireType == 2 ? $"{f.FieldNumber}=str:{f.AsString[..Math.Min(30, f.AsString.Length)]}" :
            $"{f.FieldNumber}=wt{f.WireType}"));

        return new Models.DebugEvent
        {
            Method = method,
            PayloadSize = data.Length,
            Error = $"fields: {fieldInfo}"
        };
    }

    #endregion

    #region 工具方法

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
        if (output.Length > 10 * 1024 * 1024)
            throw new InvalidOperationException($"Gzip 解压后数据过大: {output.Length} bytes");
        return output.ToArray();
    }

    #endregion
}
