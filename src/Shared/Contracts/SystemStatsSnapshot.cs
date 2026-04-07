namespace RssApp.Contracts;

public class SystemStatsSnapshot
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int UserCount { get; set; }
    public int FeedCount { get; set; }
    public int ItemCount { get; set; }
    public long DbSizeBytes { get; set; }
}
