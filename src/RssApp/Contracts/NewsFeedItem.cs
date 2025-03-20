namespace RssApp.Contracts;

public class NewsFeedItem : IEquatable<NewsFeedItem>
{

    public NewsFeedItem(string id, string title, string href, string commentsHref, string publishDate, string content)
    {
        this.Id = id;
        this.Title = title;
        this.Href = href;
        this.CommentsHref = commentsHref;
        this.PublishDate = publishDate;
        this.Content = content;
    }

    public string FeedUrl { get; set; }
    public string Title { get; set; }
    public string Href { get; set; }
    public string CommentsHref { get; set; }
    public string PublishDate { get; set; }
    public string Id { get; set; }
    public string Content { get; set; }
    public DateTime? ParsedDate {
        get
        {
            if (DateTime.TryParse(PublishDate, out DateTime parsed))
            {
                return parsed;
            }

            return null;
        }
    }

    public bool Equals(NewsFeedItem? other)
    {
        if (other == null)
        {
            return false;
        }

        return string.Equals(this.FeedUrl, other.FeedUrl, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(this.Href, other.Href, StringComparison.OrdinalIgnoreCase);
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