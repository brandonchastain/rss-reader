using System.Xml.Serialization;

namespace RssApp.Contracts;

[XmlRoot("RDF", Namespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#")]
public class RdfFeed
{
    [XmlElement("channel", Namespace = "http://purl.org/rss/1.0/")]
    public RdfChannel Channel { get; set; }

    [XmlElement("item", Namespace = "http://purl.org/rss/1.0/")]
    public List<RdfItem> Items { get; set; }
}

public class RdfChannel
{
    [XmlElement("title", Namespace = "http://purl.org/rss/1.0/")]
    public string Title { get; set; }

    [XmlElement("link", Namespace = "http://purl.org/rss/1.0/")]
    public string Link { get; set; }

    [XmlElement("description", Namespace = "http://purl.org/rss/1.0/")]
    public string Description { get; set; }
}

public class RdfItem
{
    [XmlElement("title", Namespace = "http://purl.org/rss/1.0/")]
    public string Title { get; set; }

    [XmlElement("link", Namespace = "http://purl.org/rss/1.0/")]
    public RdfLink Link { get; set; }

    [XmlElement("description", Namespace = "http://purl.org/rss/1.0/")]
    public string Description { get; set; }

    [XmlElement("date", Namespace = "http://purl.org/dc/elements/1.1/")]
    public string PublishDate { get; set; }

    [XmlElement("comments", Namespace = "http://purl.org/rss/1.0/")]
    public RdfLink CommentsLink { get; set; }

    [XmlElement("guid", Namespace = "http://purl.org/rss/1.0/")]
    public string Id { get; set; }
}

public class RdfLink
{
    [XmlText]
    public string Href { get; set; }
}
