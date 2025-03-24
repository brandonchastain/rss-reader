namespace RssApp.Contracts;

public class NewsFeedItem : IEquatable<NewsFeedItem>
{

    public NewsFeedItem(string id, int userId, string title, string href, string commentsHref, string publishDate, string content)
    {
        this.Id = id;
        this.UserId = userId;
        this.Title = title;
        this.Href = href;
        this.CommentsHref = commentsHref;
        this.PublishDate = publishDate;
        this.Content = content;
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

    // ignore for serialization
    public bool IsPaywalled { get; set; }

    public bool IsBeingPreviewed { get; set; }
    
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
        if (string.IsNullOrEmpty(this.Content))
        {
            return null;
        }

        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(this.Content);
        var img = doc.DocumentNode.SelectSingleNode("//img");
        if (img == null)
        {
            return null;
        }

        var src = img.GetAttributeValue("src", null);
        if (string.IsNullOrEmpty(src))
        {
            return null;
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

    public static async Task WriteCsvHeaderAsync(StreamWriter writer)
    {
        await writer.WriteLineAsync(
            $"{nameof(Id)},{nameof(IsRead)},{nameof(FeedUrl)},{nameof(Title)},{nameof(Href)},{nameof(CommentsHref)},{nameof(PublishDate)},{nameof(Content)},{nameof(UserId)}");
    }

    public async Task WriteCsvAsync(StreamWriter writer)
    {
        await writer.WriteAsync($"{this.Id},");
        await writer.WriteAsync($"{this.IsRead},");
        await writer.WriteAsync($"{this.FeedUrl},");
        await writer.WriteAsync($"{this.Title.Replace(",", "🙈")},");
        await writer.WriteAsync($"{this.Href},");
        await writer.WriteAsync($"{this.CommentsHref},");
        await writer.WriteAsync($"{this.PublishDate.Replace(",", "🙈")},");
        await writer.WriteAsync($"{this.Content.Replace(",", "🙈").ReplaceLineEndings("🫡")},");
        await writer.WriteAsync($"{this.UserId}" + Environment.NewLine);
    }

    public static NewsFeedItem ReadFromCsv(string csvLine)
    {
        var values = csvLine.Split(',');
        if (values.Length != 9)
        {
            Console.WriteLine(csvLine);
            throw new ArgumentException("Invalid CSV line");
        }

        return new NewsFeedItem(
            id: values[0],
            userId: int.Parse(values[8]),
            title: values[3].Replace("🙈", ","),
            href: values[4],
            commentsHref: values[5],
            publishDate: values[6].Replace("🙈", ","),
            content: values[7].Replace("🫡", Environment.NewLine).Replace("🙈", ","))
        {
            IsRead = bool.Parse(values[1]),
            FeedUrl = values[2]
        };
    }
}