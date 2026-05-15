namespace DouyinTTS.Core.Live.Models;

public class RoomInfo
{
    public long RoomId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public int ViewerCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public string WebSocketUrl { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public bool IsLive { get; set; }
}
