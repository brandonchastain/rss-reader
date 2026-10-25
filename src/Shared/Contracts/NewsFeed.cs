namespace RssApp.Contracts;

public class NewsFeed : IEquatable<NewsFeed>
{
    public NewsFeed()
    {
        this.FeedId = -1;
        this.Href = string.Empty;
        this.UserId = -1;
        this.Tags = new List<string>();
    }
    
    public NewsFeed(string href, int userId)
    : this(-1, href, userId)
    {
        // for testing only
    }

    public NewsFeed(int id, string href, int userId)
    {
        this.FeedId = id;
        this.Href = href;
        this.UserId = userId;
        this.Tags = new List<string>();
    }

    /// <summary>Consecutive failed fetches at which a feed is reported as failing.</summary>
    public const int FailingThreshold = 3;

    public int FeedId { get; set; }
    public string Href { get; set; }
    /// <summary>The feed's own title, learned on fetch; null until the first successful fetch.</summary>
    public string Title { get; set; }
    public int UserId { get; set; }
    public ICollection<string> Tags { get; set; }

    // Fetch health, read-only for clients: filled from the shared feed row so a
    // dead feed (retired URL, blocked, returns a web page) is visible instead of
    // silently producing no posts. Ignored when a client sends them back.
    public int ConsecutiveFailures { get; set; }
    public string LastError { get; set; }
    public DateTimeOffset? LastFetchedUtc { get; set; }
    public bool IsFailing => this.ConsecutiveFailures >= FailingThreshold;

    public bool Equals(NewsFeed other)
    {
        if (other == null)
        {
            return false;
        }

        return string.Equals(this.Href, other.Href, StringComparison.OrdinalIgnoreCase)
            && this.UserId == other.UserId;
    }

    public override int GetHashCode()
    {
        return this.Href?.GetHashCode() ?? 0
            ^ this.UserId.GetHashCode();
    }
}