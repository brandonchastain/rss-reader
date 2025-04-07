namespace RssApp.Contracts;

public class NewsFeedItem : IEquatable<NewsFeedItem>
{

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
    public string PublishDate { get; set; }
    public string Id { get; set; }
    public string Content { get; set; }
    public bool IsRead { get; set; }
    public string ThumbnailUrl { get; set; }

    // ignore below for serialization
    public bool IsPaywalled { get; set; }

    public bool IsBeingPreviewed { get; set; }

    public ICollection<string> FeedTags { get; set; } = new List<string>();
    
    public DateTime? ParsedDate {
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

    public string GetThumbnailUrl()
    {
        if (!string.IsNullOrEmpty(this.ThumbnailUrl))
        {
            return this.ThumbnailUrl;
        }

        var favicon = "/placeholder.jpg";
        if (string.IsNullOrEmpty(this.Content))
        {
            return favicon;
        }

        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(this.Content);
        var img = doc.DocumentNode.SelectSingleNode("//img");
        if (img == null)
        {
            return favicon;
        }

        var src = img.GetAttributeValue("src", null);
        if (string.IsNullOrEmpty(src))
        {
            return favicon;
        }

        return src;
    }

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