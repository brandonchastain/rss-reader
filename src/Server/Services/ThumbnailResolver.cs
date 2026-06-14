using HtmlAgilityPack;
using RssApp.Contracts;

namespace RssReader.Server.Services;

/// <summary>
/// Resolves the best "article image" for a feed item, in one place:
///   1. A media URL captured during deserialization
///      (media:content / media:thumbnail / image enclosure).
///   2. Otherwise the first absolute &lt;img&gt; found in the item's HTML content.
///
/// Returns null when the item has no usable image — the client then falls back
/// to a per-domain favicon (served by GET /api/feed/icon) and finally a static
/// placeholder. This consolidates logic that previously lived in three places:
/// NewsFeedItem.GetThumbnailUrl (run on both server and client), the RSS
/// deserializer, and the SQLite item repository's write path.
/// </summary>
public class ThumbnailResolver
{
    public string Resolve(NewsFeedItem item)
    {
        if (IsHttpUrl(item.ThumbnailUrl))
        {
            return item.ThumbnailUrl;
        }

        return ExtractFirstImage(item.Content);
    }

    private static string ExtractFirstImage(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(content);
            var imgs = doc.DocumentNode.SelectNodes("//img");
            if (imgs == null)
            {
                return null;
            }

            foreach (var img in imgs)
            {
                var src = img.GetAttributeValue("src", null);
                if (IsHttpUrl(src))
                {
                    return src;
                }
            }
        }
        catch
        {
            // Malformed HTML — treat as no image.
        }

        return null;
    }

    private static bool IsHttpUrl(string url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
}
