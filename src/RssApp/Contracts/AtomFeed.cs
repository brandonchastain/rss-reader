
using System.Xml;
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
    public AtomContent Content { get; set; }
}

public class AtomContent
{
    [XmlAttribute("type")]
    public string Type { get; set; }
    
    [XmlText]
    public string Text { get; set; }
    
    [XmlAnyElement]
    public XmlElement[] Elements { get; set; }
    
    // Convert the content to string regardless of how it was stored
    public override string ToString()
    {
        // If we have direct text content (including CDATA), use that
        if (!string.IsNullOrEmpty(Text))
            return Text;
            
        // If we have XML elements, convert them to string
        if (Elements != null && Elements.Length > 0)
            return string.Join("", Elements.Select(e => e.OuterXml));
            
        return string.Empty;
    }
}

[XmlRoot("link")]
public class AtomLink
{
    [XmlAttribute("rel")]
    public string Rel { get; set; }

    [XmlAttribute("href")]
    public  string Href { get; set; }
}