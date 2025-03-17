
using System.Xml.Serialization;

namespace RssApp.Contracts;
    
[XmlRoot("feed", Namespace = "http://www.w3.org/2005/Atom")]
public class AtomFeed
{
    [XmlElement("title")]
    public string Title { get; set; }

    [XmlElement("entry")]
    public List<AtomEntry> Entries { get; set; }
}

[XmlRoot("entry")]
public class AtomEntry
{
    [XmlElement("id")]
    public string Id { get; set; }

    [XmlElement("title")]
    public  string Title { get; set; }

    [XmlElement("published")]
    public  string PublishDate { get; set; }
    
    [XmlElement("link")]
    public  List<AtomLink> Links { get; set; }

    public AtomLink AltLink => this.Links?.FirstOrDefault(l => l.Rel == "alternate");

    [XmlElement("summary")]
    public string Summary { get; set; }

    [XmlElement("content")]
    public string Content { get; set; }
}

[XmlRoot("link")]
public class AtomLink
{
    [XmlAttribute("rel")]
    public string Rel { get; set; }

    [XmlAttribute("href")]
    public  string Href { get; set; }
}