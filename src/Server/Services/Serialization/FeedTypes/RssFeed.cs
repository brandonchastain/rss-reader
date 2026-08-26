
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

    [XmlElement("content", Namespace = "http://search.yahoo.com/mrss/")]
    public List<MediaContent> MediaContents { get; set; }

    [XmlElement("thumbnail", Namespace = "http://search.yahoo.com/mrss/")]
    public List<MediaContent> MediaThumbnails { get; set; }

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

[XmlType(Namespace = "http://search.yahoo.com/mrss/")]
[XmlRoot("content")]
public class MediaContent
{
    [XmlAttribute("url")]
    public string Url { get; set; }
    
    [XmlAttribute("width")]
    public string Width { get; set; }
}

[XmlRoot("link")]
public class RssLink
{
    [XmlText]
    public  string Href { get; set; }
}