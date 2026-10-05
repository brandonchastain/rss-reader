using RssApp.Contracts.FeedTypes;

namespace RssApp.Serialization;

/// <summary>
/// Picks the best article image from a feed item's Media RSS tags.
///
/// Publishers often list the same image several times at different sizes
/// (the Guardian emits 140, 460 and 700 pixel renditions, smallest first), so
/// taking the first &lt;media:content&gt; produced a thumbnail-sized image that
/// then had to be upscaled in the reader. This picks the largest rendition
/// instead, skipping anything declared as video or audio.
///
/// Order of preference:
///   1. the largest image-like &lt;media:content&gt; (direct or inside &lt;media:group&gt;)
///   2. the largest &lt;media:thumbnail&gt; (direct or inside &lt;media:group&gt;)
///   3. an image &lt;enclosure&gt;
/// </summary>
public static class MediaImagePicker
{
    public static string PickBest(
        IEnumerable<MediaContent> contents,
        IEnumerable<MediaContent> thumbnails,
        IEnumerable<MediaGroup> groups,
        RssEnclosure enclosure)
    {
        var groupList = groups ?? Enumerable.Empty<MediaGroup>();
        var allContents = (contents ?? Enumerable.Empty<MediaContent>())
            .Concat(groupList.SelectMany(g => g.Contents ?? Enumerable.Empty<MediaContent>()));
        var allThumbnails = (thumbnails ?? Enumerable.Empty<MediaContent>())
            .Concat(groupList.SelectMany(g => g.Thumbnails ?? Enumerable.Empty<MediaContent>()));

        return Largest(allContents.Where(IsImage))
            ?? Largest(allThumbnails)
            ?? (enclosure?.Type?.StartsWith("image", StringComparison.OrdinalIgnoreCase) == true
                    ? enclosure.Url
                    : null);
    }

    /// <summary>
    /// Largest by declared pixel area, falling back to width alone when no height
    /// is given. Unsized elements rank below any sized element but still win when
    /// nothing is sized; ties keep document order.
    /// </summary>
    private static string Largest(IEnumerable<MediaContent> candidates)
    {
        MediaContent best = null;
        long bestScore = -1;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate?.Url))
            {
                continue;
            }

            var score = SizeScore(candidate);
            if (best == null || score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best?.Url;
    }

    private static long SizeScore(MediaContent media)
    {
        var width = ParseSize(media.Width);
        var height = ParseSize(media.Height);
        if (width > 0 && height > 0) return width * height;
        if (width > 0) return width * width;
        if (height > 0) return height * height;
        return 0;
    }

    private static long ParseSize(string value) =>
        long.TryParse(value?.Trim(), out var n) && n > 0 ? n : 0;

    /// <summary>
    /// A &lt;media:content&gt; is usable as an article image unless it says otherwise.
    /// Most publishers omit both "type" and "medium" on image renditions, so the
    /// absence of a hint counts as an image; an explicit non-image hint does not.
    /// </summary>
    private static bool IsImage(MediaContent media)
    {
        if (!string.IsNullOrWhiteSpace(media.Medium))
        {
            return media.Medium.Trim().Equals("image", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(media.Type))
        {
            return media.Type.Trim().StartsWith("image", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }
}
