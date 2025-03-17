
using System.Xml.Serialization;

namespace RssApp.Contracts;
    
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
    public  string PublishDate { get; set; }
    
    [XmlElement("link")]
    public  RssLink Link { get; set; }

    [XmlElement("comments")]
    public RssLink CommentsLink { get; set; }

    [XmlElement("description")]
    public string Description { get; set; }
}

[XmlRoot("link")]
public class RssLink
{
    [XmlText]
    public  string Href { get; set; }
}