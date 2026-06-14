using RssReader.Shared.Extensions;

namespace RssApp.Contracts;

public class NewsFeedItem : IEquatable<NewsFeedItem>
{
    public NewsFeedItem()
    {
        this.Id = string.Empty;
        this.FeedUrl = string.Empty;
        this.UserId = -1;
        this.Title = string.Empty;
        this.Href = string.Empty;
        this.CommentsHref = string.Empty;
        this.PublishDate = string.Empty;
        this.Content = null;
        this.ThumbnailUrl = null;
    }

    public NewsFeedItem(string id, int userId, string title, string href, string commentsHref, string publishDate, string content, string thumbnailUrl)
    {
        this.Id = id;
        this.UserId = userId;
        this.Title = title;
        this.Href = href;
        this.CommentsHref = commentsHref;
        this.PublishDate = publishDate;
        this.Content = content;
        this.ThumbnailUrl = thumbnailUrl;
    }

    public string FeedUrl { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; }
    public string Href { get; set; }
    public string CommentsHref { get; set; }
    public long PublishDateOrder { get; set; }
    public string PublishDate { get; set; }
    public string Id { get; set; }
    public string Content { get; set; }
    public bool IsRead { get; set; }
    public string ThumbnailUrl { get; set; }
    public bool IsSaved { get; set; }
    public bool IsBeingPreviewed { get; set; }
    public ICollection<string> FeedTags { get; set; } = new List<string>();

    public DateTime? ParsedDate
    {
        get
        {
            if (DateTime.TryParse(PublishDate, out DateTime parsed))
            {
                parsed = TimeZoneInfo.ConvertTimeToUtc(parsed);
                parsed = TimeZoneInfo.ConvertTimeFromUtc(parsed, TimeZoneInfo.Local);
                return parsed;
            }

            return null;
        }
    }

    public bool Equals(NewsFeedItem other)
    {
        if (other == null)
        {
            return false;
        }

        return string.Equals(this.FeedUrl, other.FeedUrl, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(this.Href, other.Href, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The resolved article image, or null when the item has none. Resolution
    /// (media tags + content &lt;img&gt; scrape) happens once, server-side, in
    /// ThumbnailResolver; the value is persisted in ThumbnailUrl. The client
    /// falls back to a per-domain favicon and then a placeholder when this is null.
    /// </summary>
    public string GetThumbnailUrl() => this.ThumbnailUrl;

    public override int GetHashCode()
    {
        return (this.FeedUrl?.GetHashCode() ?? 0) * 7
                + this.Href.GetHashCode() * 11;
    }

    public override string ToString()
    {
        return $"{this.Title} ({this.PublishDate})";
    }
}