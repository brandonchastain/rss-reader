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

    public int FeedId { get; set; }
    public string Href { get; set; }
    public int UserId { get; set; }
    public ICollection<string> Tags { get; set; }

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