using System.Text.RegularExpressions;
using HtmlAgilityPack;
using RssApp.Contracts;

namespace RssReader.Server.Services;

/// <summary>
/// Resolves the best "article image" for a feed item, in one place:
///   1. A media URL captured during deserialization
///      (the largest media:content / media:thumbnail / image enclosure,
///      chosen by MediaImagePicker).
///   2. Otherwise the first real &lt;img&gt; in the item's HTML content, at the
///      largest size its srcset offers. One-pixel tracking images (FeedBurner,
///      WordPress stats) are skipped so they never become the thumbnail.
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

        return ExtractBestImage(item.Content);
    }

    private static string ExtractBestImage(string content)
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
                if (IsTrackingPixel(img))
                {
                    continue;
                }

                var src = BestSrc(img);
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

    /// <summary>
    /// The largest candidate the &lt;img&gt; advertises: the biggest width ("w")
    /// descriptor in srcset, else the biggest density ("x"), else the plain src,
    /// else a lazy-loading data-src.
    /// </summary>
    private static string BestSrc(HtmlNode img)
    {
        var fromSrcset = LargestSrcsetCandidate(img.GetAttributeValue("srcset", null))
            ?? LargestSrcsetCandidate(img.GetAttributeValue("data-srcset", null));
        if (IsHttpUrl(fromSrcset))
        {
            return fromSrcset;
        }

        var src = img.GetAttributeValue("src", null);
        if (IsHttpUrl(src))
        {
            return src;
        }

        return img.GetAttributeValue("data-src", null);
    }

    // "url 300w, url 600w" or "url 1x, url 2x". A URL never contains whitespace,
    // so each candidate is a non-space run followed by its descriptor.
    private static readonly Regex SrcsetCandidate = new(@"(\S+)\s+(\d+(?:\.\d+)?)([wx])", RegexOptions.Compiled);

    private static string LargestSrcsetCandidate(string srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset))
        {
            return null;
        }

        string best = null;
        double bestWidth = -1;
        double bestDensity = -1;
        foreach (Match match in SrcsetCandidate.Matches(srcset))
        {
            var url = match.Groups[1].Value.TrimEnd(',');
            if (!double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var size))
            {
                continue;
            }

            // Width descriptors always beat density descriptors: they describe
            // actual pixels, which is what matters for a hero image.
            if (match.Groups[3].Value == "w")
            {
                if (size > bestWidth)
                {
                    bestWidth = size;
                    best = url;
                }
            }
            else if (bestWidth < 0 && size > bestDensity)
            {
                bestDensity = size;
                best = url;
            }
        }

        return best;
    }

    private static bool IsTrackingPixel(HtmlNode img)
    {
        var width = img.GetAttributeValue("width", null);
        var height = img.GetAttributeValue("height", null);
        return IsOnePixel(width) || IsOnePixel(height);
    }

    private static bool IsOnePixel(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && int.TryParse(value.Trim().TrimEnd('p', 'x'), out var n)
        && n <= 1;

    private static bool IsHttpUrl(string url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
}
