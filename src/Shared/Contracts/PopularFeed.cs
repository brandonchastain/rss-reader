namespace RssApp.Contracts;

/// <summary>A feed many users subscribe to, offered on the Feeds page for discovery.</summary>
public class PopularFeed
{
    public string Href { get; set; } = string.Empty;
    public int SubscriberCount { get; set; }
}
