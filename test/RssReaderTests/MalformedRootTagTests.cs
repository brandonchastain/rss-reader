namespace RssReaderTests;
using RssApp.Serialization;
using RssApp.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

[TestClass]
public sealed class MalformedRootTagTests
{
    // Trimmed from science.nasa.gov/category/apod/feed/: no space between the last two
    // namespace declarations on the <rss> tag.
    private const string NasaApodFeed = """
        <?xml version="1.0" encoding="UTF-8"?><rss version="2.0"
        	xmlns:content="http://purl.org/rss/1.0/modules/content/"
        	xmlns:atom="http://www.w3.org/2005/Atom"
        	 xmlns:apod="https://science.nasa.gov/apod/"xmlns:media="http://search.yahoo.com/mrss/" >

        <channel>
        	<title>APOD &#8211; NASA Science</title>
        	<link>https://science.nasa.gov</link>
        	<item>
        		<title>APOD: 2026 October 31 – Night on a Spooky Planet</title>
        		<link>https://science.nasa.gov/image-article/apod-2026-october-31-night-on-a-spooky-planet/</link>
        		<guid>https://science.nasa.gov/image-article/apod-2026-october-31-night-on-a-spooky-planet/</guid>
        		<pubDate>Sat, 31 Oct 2026 04:05:00 +0000</pubDate>
        		<description><![CDATA[A spooky planet.]]></description>
        	</item>
        </channel>
        </rss>
        """;

    [TestMethod]
    public void Feed_With_Missing_Space_Between_Root_Attributes_Parses()
    {
        var serializer = new RssDeserializer(new NullLogger<RssDeserializer>());
        var items = serializer.FromString(NasaApodFeed, new RssUser("test", -99)).ToList();

        Assert.AreEqual(1, items.Count);
        Assert.AreEqual("https://science.nasa.gov/image-article/apod-2026-october-31-night-on-a-spooky-planet/", items[0].Href);
        Assert.AreEqual("APOD – NASA Science", RssDeserializer.TryGetFeedTitle(NasaApodFeed));
    }

    [TestMethod]
    public void Repair_Inserts_Space_Only_Where_Missing()
    {
        Assert.AreEqual(
            """<rss a="1" b='2' c="3">""",
            RssDeserializer.RepairRootAttributes("""<rss a="1"b='2'c="3">"""));
    }

    [TestMethod]
    public void Well_Formed_Feed_Is_Returned_Unchanged()
    {
        const string feed = """<?xml version="1.0"?><rss version="2.0" xmlns:a="x"><channel><item><title t="a"b="c"</title></item></channel></rss>""";

        // Only the root tag is repaired; text further in is left exactly as it was.
        Assert.AreSame(feed, RssDeserializer.RepairRootAttributes(feed));
    }

    [TestMethod]
    public void Quote_Inside_Another_Quote_Style_Is_Not_Split()
    {
        const string tag = """<feed xmlns="http://www.w3.org/2005/Atom" title='say "hi"there'>""";
        Assert.AreSame(tag, RssDeserializer.RepairRootAttributes(tag));
    }
}
