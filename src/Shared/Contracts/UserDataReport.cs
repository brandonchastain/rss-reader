namespace RssApp.Contracts;

public class UserDataReport
{
    public string Username { get; set; }
    public int FeedCount { get; set; }
    public int TotalItemCount { get; set; }
    public List<FeedSummary> Feeds { get; set; } = new();
}

public class FeedSummary
{
    public string Url { get; set; }
    public List<string> Tags { get; set; } = new();
    public int ItemCount { get; set; }
}
