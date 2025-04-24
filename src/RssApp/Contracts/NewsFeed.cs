namespace RssApp.Contracts;

using RssApp.Contracts.FeedTypes;

public class NewsFeed : IEquatable<NewsFeed>
{
    public NewsFeed(string url, int userId)
    : this(-1, url, userId, false)
    {
        // for testing only
    }

    public NewsFeed(int id, string url, int userId, bool isPaywalled = false)
    {
        this.FeedId = id;
        this.FeedUrl = url;
        this.UserId = userId;
        this.Tags = new List<string>();
        this.IsPaywalled = isPaywalled;
    }

    public int FeedId { get; set; }
    public string FeedUrl { get; set; }
    public bool IsPaywalled { get; set; }
    public int UserId { get; set; }
    public ICollection<string> Tags { get; set; }

    public bool Equals(NewsFeed other)
    {
        if (other == null)
        {
            return false;
        }

        return string.Equals(this.FeedUrl, other.FeedUrl, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return this.FeedUrl?.GetHashCode() ?? 0;
    }
}