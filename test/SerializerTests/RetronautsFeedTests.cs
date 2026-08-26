namespace SerializerTests;
using RssApp.Serialization;
using RssApp.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

[TestClass]
public sealed class RetronautsFeedTests
{
    // A frozen snapshot of https://retronauts.com/feed: RSS 2.0 with relative <link>s,
    // CDATA titles/descriptions, and media:thumbnail. Mix of podcast episodes and
    // "Retro Re-release Roundup" / Kickstarter articles — all must parse.
    [TestMethod]
    public void Retronauts_Feed_Parses_All_Items()
    {
        var content = File.ReadAllText("feeds/retronauts.xml");
        var serializer = new RssDeserializer(new NullLogger<RssDeserializer>());
        var items = serializer.FromString(content, new RssUser("test", -99)).ToList();

        Assert.AreEqual(11, items.Count, "Every item in the snapshot should parse");
        Assert.IsTrue(items.Any(i => i.Title.Contains("Roundup")),
            "Non-episode roundup items should parse alongside episodes");
    }

    // Retronauts publishes every <link>/<guid> site-relative ("/article/2546/..."),
    // so stored verbatim they resolved against the reader's own origin and 404'd on
    // click. They must come out absolute, resolved against the channel's <link>.
    [TestMethod]
    public void Retronauts_Relative_Links_Are_Made_Absolute()
    {
        var content = File.ReadAllText("feeds/retronauts.xml");
        var serializer = new RssDeserializer(new NullLogger<RssDeserializer>());
        var items = serializer.FromString(content, new RssUser("test", -99)).ToList();

        CollectionAssert.AreEqual(
            new string[0],
            items.Where(i => !i.Href.StartsWith("https://retronauts.com/")).Select(i => i.Href).ToArray(),
            "Every item link should resolve against the channel <link>");
        Assert.IsTrue(
            items.Any(i => i.Href == "https://retronauts.com/article/2521/retronauts-episode-776-the-golden-age-of-pinball"),
            "A known relative link should resolve to its full article URL");
    }

    private const string RelativeLinksNoChannelLink = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0">
          <channel>
            <title>No Channel Link</title>
            <item>
              <title>Relative</title>
              <link>/posts/hello</link>
            </item>
          </channel>
        </rss>
        """;

    // When the feed declares no base of its own, the URL it was fetched from stands
    // in — otherwise these items would keep the same broken relative hrefs.
    [TestMethod]
    public void Feed_Url_Is_The_Fallback_Base()
    {
        var serializer = new RssDeserializer(new NullLogger<RssDeserializer>());
        var items = serializer
            .FromString(RelativeLinksNoChannelLink, new RssUser("test", -99), "https://example.com/feed/rss.xml")
            .ToList();

        Assert.AreEqual("https://example.com/posts/hello", items.Single().Href);
    }

    // No base anywhere: leave the link alone rather than inventing an origin.
    [TestMethod]
    public void Relative_Link_Survives_When_No_Base_Is_Available()
    {
        var serializer = new RssDeserializer(new NullLogger<RssDeserializer>());
        var items = serializer.FromString(RelativeLinksNoChannelLink, new RssUser("test", -99)).ToList();

        Assert.AreEqual("/posts/hello", items.Single().Href);
    }

    private const string SchemeEdgeCaseFeed = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0">
          <channel>
            <title>Scheme Edge Cases</title>
            <link>https://example.com/blog/</link>
            <item><title>Protocol relative</title><link>//cdn.example.org/a</link></item>
            <item><title>Dot relative</title><link>./nested/post</link></item>
            <item><title>Unix looking</title><link>/posts/hello</link></item>
          </channel>
        </rss>
        """;

    // Guards the Linux/Windows split that broke CI: what counts as "already absolute"
    // must be decided by the presence of a URI scheme, not by Uri.TryCreate with
    // UriKind.Absolute -- on Unix that parses "/posts/hello" as an absolute file://
    // URI, so site-relative links were silently left unresolved there.
    [TestMethod]
    public void Only_A_Uri_Scheme_Counts_As_Already_Absolute()
    {
        var serializer = new RssDeserializer(new NullLogger<RssDeserializer>());
        var hrefs = serializer.FromString(SchemeEdgeCaseFeed, new RssUser("test", -99))
            .Select(i => i.Href)
            .ToList();

        // Protocol-relative has no scheme, so the base supplies one.
        Assert.AreEqual("https://cdn.example.org/a", hrefs[0]);
        Assert.AreEqual("https://example.com/blog/nested/post", hrefs[1]);
        Assert.AreEqual("https://example.com/posts/hello", hrefs[2]);
    }

    private const string AbsoluteLinkFeed = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0">
          <channel>
            <title>Absolute</title>
            <link>https://example.com/</link>
            <item>
              <title>Elsewhere</title>
              <link>https://other.example.org/a/post?x=1&amp;y=2</link>
            </item>
          </channel>
        </rss>
        """;

    // Absolute links must pass through byte-for-byte — resolving them against a base
    // would silently rewrite cross-domain links (and change item identity, which is
    // keyed on FeedUrl + Href).
    [TestMethod]
    public void Absolute_Links_Are_Left_Untouched()
    {
        var serializer = new RssDeserializer(new NullLogger<RssDeserializer>());
        var items = serializer
            .FromString(AbsoluteLinkFeed, new RssUser("test", -99), "https://example.com/rss")
            .ToList();

        Assert.AreEqual("https://other.example.org/a/post?x=1&y=2", items.Single().Href);
    }

    // Regression: a single malformed item (here, one missing a <link>) must not throw
    // out the whole feed. Before the fix, x.Link.Href threw NullReferenceException
    // during the lazy projection, which the FeedRefresher's ToList() surfaced as a
    // failed fetch — so the entire feed "disappeared" until the next clean snapshot.
    private const string TwoGoodOneBadLink = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0">
          <channel>
            <title>Retronauts</title>
            <item>
              <guid>/article/1/good-a</guid>
              <title><![CDATA[Good A]]></title>
              <link>/article/1/good-a</link>
              <pubDate>Mon, 22 Jun 2026 16:00:00 -0700</pubDate>
              <description><![CDATA[a]]></description>
            </item>
            <item>
              <guid>/article/2/no-link</guid>
              <title><![CDATA[No Link Item]]></title>
              <pubDate>Thu, 18 Jun 2026 10:49:00 -0700</pubDate>
              <description><![CDATA[b]]></description>
            </item>
            <item>
              <guid>/article/3/good-b</guid>
              <title><![CDATA[Good B]]></title>
              <link>/article/3/good-b</link>
              <pubDate>Mon, 15 Jun 2026 16:00:00 -0700</pubDate>
              <description><![CDATA[c]]></description>
            </item>
          </channel>
        </rss>
        """;

    [TestMethod]
    public void One_Bad_Item_Does_Not_Discard_Whole_Feed()
    {
        var serializer = new RssDeserializer(new NullLogger<RssDeserializer>());
        var items = serializer.FromString(TwoGoodOneBadLink, new RssUser("test", -99)).ToList();

        // Both well-formed items survive; the link-less item falls back to its guid.
        Assert.IsTrue(items.Count >= 2,
            $"Good items should survive a single bad item; got {items.Count}");
        Assert.IsTrue(items.Any(i => i.Href == "/article/1/good-a"));
        Assert.IsTrue(items.Any(i => i.Href == "/article/3/good-b"));
    }
}
