
using System.Xml.Serialization;

namespace RssApp.Contracts.FeedTypes;
    
[XmlRoot("rss")]
public class RssDocument
{
    [XmlElement("channel")]
    public RssChannel Feed { get; set; }
}

[XmlRoot("channel")]
public class RssChannel
{
    [XmlElement("title")]
    public string Title { get; set; }

    /// <summary>
    /// The channel's site URL. Feeds that publish site-relative item links (e.g.
    /// retronauts.com) rely on this as the base to resolve them against. Namespace-less
    /// so it binds &lt;link&gt; and not the &lt;atom:link rel="self"&gt; alongside it.
    /// </summary>
    [XmlElement("link")]
    public string Link { get; set; }

    /// <summary>
    /// The &lt;atom:link&gt; elements many RSS feeds carry; the rel="self" href is a
    /// second-choice base when the channel has no plain &lt;link&gt;.
    /// </summary>
    [XmlElement("link", Namespace = "http://www.w3.org/2005/Atom")]
    public List<AtomLink> AtomLinks { get; set; }

    [XmlElement("item")]
    public List<RssItem> Entries { get; set; }
}

[XmlRoot("item")]
public class RssItem
{
    [XmlElement("guid")]
    public string Id { get; set; }
    [XmlElement("title")]
    public  string Title { get; set; }

    [XmlElement("pubDate")]
    public string PublishDate { get; set; }
    
    [XmlElement("link")]
    public  RssLink Link { get; set; }

    [XmlElement("comments")]
    public RssLink CommentsLink { get; set; }

    [XmlElement("description")]
    public string Description { get; set; }

    [XmlElement("content", Namespace = MediaContent.MediaRssNamespace)]
    public List<MediaContent> MediaContents { get; set; }

    [XmlElement("thumbnail", Namespace = MediaContent.MediaRssNamespace)]
    public List<MediaContent> MediaThumbnails { get; set; }

    /// <summary>
    /// Some publishers (YouTube, many WordPress plugins) wrap their media tags in a
    /// &lt;media:group&gt; instead of putting them directly on the item.
    /// </summary>
    [XmlElement("group", Namespace = MediaContent.MediaRssNamespace)]
    public List<MediaGroup> MediaGroups { get; set; }

    [XmlElement("enclosure")]
    public RssEnclosure Enclosure { get; set; }
}

[XmlRoot("enclosure")]
public class RssEnclosure
{
    [XmlAttribute("url")]
    public string Url { get; set; }

    [XmlAttribute("type")]
    public string Type { get; set; }
}

/// <summary>
/// A &lt;media:content&gt; or &lt;media:thumbnail&gt; element. Feeds commonly list
/// several renditions of the same image at different widths; the deserializer
/// uses the size attributes to pick the largest one (see MediaImagePicker).
/// </summary>
[XmlType(Namespace = MediaRssNamespace)]
[XmlRoot("content")]
public class MediaContent
{
    public const string MediaRssNamespace = "http://search.yahoo.com/mrss/";

    [XmlAttribute("url")]
    public string Url { get; set; }

    [XmlAttribute("width")]
    public string Width { get; set; }

    [XmlAttribute("height")]
    public string Height { get; set; }

    /// <summary>MIME type, e.g. "image/jpeg" or "video/mp4". Often omitted.</summary>
    [XmlAttribute("type")]
    public string Type { get; set; }

    /// <summary>Media RSS "medium" hint: image, video, audio, document, executable.</summary>
    [XmlAttribute("medium")]
    public string Medium { get; set; }
}

[XmlType(Namespace = MediaContent.MediaRssNamespace)]
[XmlRoot("group")]
public class MediaGroup
{
    [XmlElement("content", Namespace = MediaContent.MediaRssNamespace)]
    public List<MediaContent> Contents { get; set; }

    [XmlElement("thumbnail", Namespace = MediaContent.MediaRssNamespace)]
    public List<MediaContent> Thumbnails { get; set; }
}

[XmlRoot("link")]
public class RssLink
{
    [XmlText]
    public  string Href { get; set; }
}