namespace RssApp.Contracts;

public class NewsFeedItem
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
}