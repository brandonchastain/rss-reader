namespace SerializerTests;

using Microsoft.Extensions.Logging.Abstractions;
using RssApp.Contracts;
using RssApp.Serialization;
using RssReader.Server.Services;

/// <summary>
/// Which image a feed item ends up with. Covers the Media RSS picker (largest
/// rendition wins, video is skipped, media:group is searched) and the content
/// scrape fallback (largest srcset candidate, tracking pixels skipped).
/// </summary>
[TestClass]
public sealed class ThumbnailSelectionTests
{
    private static readonly RssUser User = new RssUser("test", -99);

    private static NewsFeedItem Single(string xml)
    {
        var items = new RssDeserializer(new NullLogger<RssDeserializer>())
            .FromString(xml, User, "https://example.com/feed")
            .ToList();
        Assert.AreEqual(1, items.Count, "expected exactly one item");
        return items[0];
    }

    private static string Rss(string itemBody) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:media="http://search.yahoo.com/mrss/">
          <channel>
            <title>t</title>
            <link>https://example.com/</link>
            <item>
              <title>a</title>
              <link>https://example.com/a</link>
              {itemBody}
            </item>
          </channel>
        </rss>
        """;

    private static string Atom(string entryBody) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom" xmlns:media="http://search.yahoo.com/mrss/">
          <title>t</title>
          <link rel="alternate" href="https://example.com/"/>
          <entry>
            <id>tag:a</id>
            <title>a</title>
            <link rel="alternate" href="https://example.com/a"/>
            {entryBody}
          </entry>
        </feed>
        """;

    [TestMethod]
    public void Rss_Picks_The_Largest_MediaContent_Rendition()
    {
        // The Guardian lists the same picture smallest-first; the 700px one must win.
        var item = Single(Rss("""
            <media:content width="140" url="https://img.example.com/pic.jpg?width=140"/>
            <media:content width="700" url="https://img.example.com/pic.jpg?width=700"/>
            <media:content width="460" url="https://img.example.com/pic.jpg?width=460"/>
            """));

        Assert.AreEqual("https://img.example.com/pic.jpg?width=700", item.ThumbnailUrl);
    }

    [TestMethod]
    public void Rss_Ranks_By_Area_When_Both_Dimensions_Are_Given()
    {
        var item = Single(Rss("""
            <media:content width="800" height="100" url="https://img.example.com/banner.jpg"/>
            <media:content width="600" height="600" url="https://img.example.com/square.jpg"/>
            """));

        Assert.AreEqual("https://img.example.com/square.jpg", item.ThumbnailUrl);
    }

    [TestMethod]
    public void Rss_Skips_Video_MediaContent_And_Uses_The_Thumbnail()
    {
        var item = Single(Rss("""
            <media:content type="video/mp4" width="1920" url="https://cdn.example.com/clip.mp4"/>
            <media:content medium="audio" url="https://cdn.example.com/clip.mp3"/>
            <media:thumbnail width="640" url="https://cdn.example.com/poster.jpg"/>
            """));

        Assert.AreEqual("https://cdn.example.com/poster.jpg", item.ThumbnailUrl);
    }

    [TestMethod]
    public void Rss_Searches_MediaGroup_For_Renditions()
    {
        var item = Single(Rss("""
            <media:group>
              <media:content width="300" url="https://img.example.com/s.jpg"/>
              <media:content width="1200" url="https://img.example.com/l.jpg"/>
            </media:group>
            """));

        Assert.AreEqual("https://img.example.com/l.jpg", item.ThumbnailUrl);
    }

    [TestMethod]
    public void Rss_Uses_An_Unsized_MediaContent_When_That_Is_All_There_Is()
    {
        var item = Single(Rss("""
            <media:content url="https://img.example.com/only.jpg"/>
            """));

        Assert.AreEqual("https://img.example.com/only.jpg", item.ThumbnailUrl);
    }

    [TestMethod]
    public void Rss_Prefers_A_Sized_Rendition_Over_An_Unsized_One()
    {
        var item = Single(Rss("""
            <media:content url="https://img.example.com/unknown.jpg"/>
            <media:content width="320" url="https://img.example.com/known.jpg"/>
            """));

        Assert.AreEqual("https://img.example.com/known.jpg", item.ThumbnailUrl);
    }

    [TestMethod]
    public void Rss_Falls_Back_To_An_Image_Enclosure()
    {
        var item = Single(Rss("""
            <enclosure url="https://img.example.com/enc.png" type="image/png" length="1"/>
            """));

        Assert.AreEqual("https://img.example.com/enc.png", item.ThumbnailUrl);
    }

    [TestMethod]
    public void Rss_Relative_Media_Url_Is_Resolved_Against_The_Site()
    {
        var item = Single(Rss("""
            <media:thumbnail url="/images/pic.jpg"/>
            """));

        Assert.AreEqual("https://example.com/images/pic.jpg", item.ThumbnailUrl);
    }

    [TestMethod]
    public void Atom_Picks_The_Largest_Thumbnail_From_MediaGroup()
    {
        // YouTube-style entry: everything lives under media:group.
        var item = Single(Atom("""
            <media:group>
              <media:title>a</media:title>
              <media:thumbnail url="https://i.example.com/hq.jpg" width="480" height="360"/>
              <media:thumbnail url="https://i.example.com/default.jpg" width="120" height="90"/>
            </media:group>
            """));

        Assert.AreEqual("https://i.example.com/hq.jpg", item.ThumbnailUrl);
    }

    [TestMethod]
    public void Atom_Without_Media_Tags_Still_Has_No_Thumbnail()
    {
        var item = Single(Atom("""
            <content type="html">&lt;p&gt;no pictures here&lt;/p&gt;</content>
            """));

        Assert.IsNull(item.ThumbnailUrl);
        Assert.IsNull(new ThumbnailResolver().Resolve(item));
    }

    [TestMethod]
    public void Resolver_Keeps_A_Media_Image_Over_The_Content_Scrape()
    {
        var item = new NewsFeedItem
        {
            ThumbnailUrl = "https://img.example.com/media.jpg",
            Content = "<p><img src=\"https://img.example.com/inline.jpg\"></p>",
        };

        Assert.AreEqual("https://img.example.com/media.jpg", new ThumbnailResolver().Resolve(item));
    }

    [TestMethod]
    public void Resolver_Picks_The_Largest_Srcset_Candidate()
    {
        var item = new NewsFeedItem
        {
            Content = "<p><img src=\"https://img.example.com/w300.jpg\" " +
                      "srcset=\"https://img.example.com/w300.jpg 300w, https://img.example.com/w1200.jpg 1200w, https://img.example.com/w600.jpg 600w\"></p>",
        };

        Assert.AreEqual("https://img.example.com/w1200.jpg", new ThumbnailResolver().Resolve(item));
    }

    [TestMethod]
    public void Resolver_Picks_The_Highest_Density_When_Srcset_Uses_X_Descriptors()
    {
        var item = new NewsFeedItem
        {
            Content = "<img src=\"https://img.example.com/1x.jpg\" srcset=\"https://img.example.com/1x.jpg 1x, https://img.example.com/2x.jpg 2x\">",
        };

        Assert.AreEqual("https://img.example.com/2x.jpg", new ThumbnailResolver().Resolve(item));
    }

    [TestMethod]
    public void Resolver_Skips_One_Pixel_Tracking_Images()
    {
        var item = new NewsFeedItem
        {
            Content = "<img src=\"https://feeds.example.com/~r/track\" width=\"1\" height=\"1\">" +
                      "<p>text</p><img src=\"https://img.example.com/real.jpg\" width=\"800\">",
        };

        Assert.AreEqual("https://img.example.com/real.jpg", new ThumbnailResolver().Resolve(item));
    }

    [TestMethod]
    public void Resolver_Falls_Back_To_Lazy_Data_Src()
    {
        var item = new NewsFeedItem
        {
            Content = "<img data-src=\"https://img.example.com/lazy.jpg\" src=\"data:image/gif;base64,R0lGOD\">",
        };

        Assert.AreEqual("https://img.example.com/lazy.jpg", new ThumbnailResolver().Resolve(item));
    }
}
