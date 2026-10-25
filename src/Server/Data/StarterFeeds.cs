namespace RssApp.Data;

/// <summary>
/// A short introductory set every new account is subscribed to, so the first
/// timeline is not empty. Keep it small; the Feeds page offers the rest.
/// </summary>
public static class StarterFeeds
{
    public static readonly IReadOnlyList<string> Urls = new[]
    {
        "https://hnrss.org/frontpage",
        "https://www.theverge.com/rss/index.xml",
        "https://feeds.arstechnica.com/arstechnica/index",
        "https://feeds.bbci.co.uk/news/world/rss.xml",
        "https://xkcd.com/rss.xml",
    };
}
