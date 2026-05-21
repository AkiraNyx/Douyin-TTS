namespace DouyinTTS.Core.Live.Models;

public class RoomInfo
{
    public long RoomId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int ViewerCount { get; set; }
    public bool IsLive { get; set; }
}
