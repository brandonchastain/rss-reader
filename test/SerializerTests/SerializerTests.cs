namespace SerializerTests;
using RssApp.Serialization;
using RssApp.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

[TestClass]
public sealed class SerializerTests
{
    [TestMethod]
    public void Deserialized_Feed_Thumbnails_Should_Not_Be_Null()
    {
        foreach (var file in Directory.GetFiles("feeds").Where(f => f.Contains("vox.xml")))
        {
            var content = File.ReadAllText(file);
            var serializer = new RssDeserializer(new NullLogger<RssDeserializer>());
            var feed = serializer.FromString(content, new RssUser("test", -99));
            Assert.IsNotNull(feed);

            foreach (var item in feed)
            {
                string thumbUrl = item.GetThumbnailUrl();
                Assert.IsNotNull(thumbUrl);
            }
        }
    }

    [TestMethod]
    public void OpmlExport_Should_Include_All_Tags()
    {
        // Arrange
        var feeds = new List<NewsFeed>
        {
            new NewsFeed("https://example.com/feed1", 1)
            {
                Tags = new List<string> { "tech", "news", "programming" }
            },
            new NewsFeed("https://example.com/feed2", 1)
            {
                Tags = new List<string> { "science", "research" }
            },
            new NewsFeed("https://example.com/feed3", 1)
            {
                Tags = new List<string>() // No tags
            }
        };

        // Act
        var opmlContent = OpmlSerializer.GenerateOpmlContent(feeds);

        // Assert
        Assert.IsTrue(opmlContent.Contains("category=\"tech,news,programming\""), 
            "OPML should contain all tags for feed1");
        Assert.IsTrue(opmlContent.Contains("category=\"science,research\""), 
            "OPML should contain all tags for feed2");
        Assert.IsTrue(opmlContent.Contains("https://example.com/feed3"), 
            "Feed3 should be included even without tags");
    }

    [TestMethod]
    public void OpmlImport_Should_Parse_All_Tags()
    {
        // Arrange
        var opmlContent = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<opml version=""2.0"">
  <head>
    <title>RSS Feed Export</title>
  </head>
  <body>
    <outline type=""rss"" xmlUrl=""https://example.com/feed1"" category=""tech,news,programming"" />
    <outline type=""rss"" xmlUrl=""https://example.com/feed2"" category=""science,research"" />
    <outline type=""rss"" xmlUrl=""https://example.com/feed3"" />
  </body>
</opml>";

        // Act
        var feeds = OpmlSerializer.ParseOpmlContent(opmlContent, 1).ToList();

        // Assert
        Assert.AreEqual(3, feeds.Count, "Should import all 3 feeds");
        
        var feed1 = feeds.FirstOrDefault(f => f.Href == "https://example.com/feed1");
        Assert.IsNotNull(feed1, "Feed1 should be imported");
        Assert.AreEqual(3, feed1.Tags.Count, "Feed1 should have 3 tags");
        CollectionAssert.AreEquivalent(new[] { "tech", "news", "programming" }, feed1.Tags.ToArray(), 
            "Feed1 should have correct tags");

        var feed2 = feeds.FirstOrDefault(f => f.Href == "https://example.com/feed2");
        Assert.IsNotNull(feed2, "Feed2 should be imported");
        Assert.AreEqual(2, feed2.Tags.Count, "Feed2 should have 2 tags");
        CollectionAssert.AreEquivalent(new[] { "science", "research" }, feed2.Tags.ToArray(), 
            "Feed2 should have correct tags");

        var feed3 = feeds.FirstOrDefault(f => f.Href == "https://example.com/feed3");
        Assert.IsNotNull(feed3, "Feed3 should be imported");
        Assert.AreEqual(0, feed3.Tags.Count, "Feed3 should have no tags");
    }

    [TestMethod]
    public void OpmlImport_Should_Handle_Tags_With_Spaces()
    {
        // Arrange
        var opmlContent = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<opml version=""2.0"">
  <head>
    <title>RSS Feed Export</title>
  </head>
  <body>
    <outline type=""rss"" xmlUrl=""https://example.com/feed1"" category=""tech, news , programming"" />
  </body>
</opml>";

        // Act
        var feeds = OpmlSerializer.ParseOpmlContent(opmlContent, 1).ToList();

        // Assert
        Assert.AreEqual(1, feeds.Count);
        var feed = feeds[0];
        Assert.AreEqual(3, feed.Tags.Count, "Should have 3 tags");
        CollectionAssert.AreEquivalent(new[] { "tech", "news", "programming" }, feed.Tags.ToArray(), 
            "Tags should be trimmed correctly");
    }

    [TestMethod]
    public void OpmlRoundTrip_Should_Preserve_All_Tags()
    {
        // Arrange
        var originalFeeds = new List<NewsFeed>
        {
            new NewsFeed("https://example.com/feed1", 1)
            {
                Tags = new List<string> { "tech", "news", "programming", "dotnet" }
            },
            new NewsFeed("https://example.com/feed2", 1)
            {
                Tags = new List<string> { "science" }
            }
        };

        // Act - Export and then Import
        var opmlContent = OpmlSerializer.GenerateOpmlContent(originalFeeds);
        var importedFeeds = OpmlSerializer.ParseOpmlContent(opmlContent, 1).ToList();

        // Assert
        Assert.AreEqual(originalFeeds.Count, importedFeeds.Count, "Should import same number of feeds");
        
        foreach (var originalFeed in originalFeeds)
        {
            var importedFeed = importedFeeds.FirstOrDefault(f => f.Href == originalFeed.Href);
            Assert.IsNotNull(importedFeed, $"Feed {originalFeed.Href} should be imported");
            Assert.AreEqual(originalFeed.Tags.Count, importedFeed.Tags.Count, 
                $"Feed {originalFeed.Href} should have same number of tags");
            CollectionAssert.AreEquivalent(originalFeed.Tags.ToArray(), importedFeed.Tags.ToArray(), 
                $"Feed {originalFeed.Href} should have same tags after round-trip");
        }
    }

    [TestMethod]
    public void OpmlExport_Generated_Should_Be_Valid_XML()
    {
        // Arrange
        var feeds = new List<NewsFeed>
        {
            new NewsFeed("https://example.com/feed1", 1)
            {
                Tags = new List<string> { "tech", "news", "programming" }
            }
        };

        // Act
        var opmlContent = OpmlSerializer.GenerateOpmlContent(feeds);

        // Assert - Should be valid XML that can be parsed
        var reimported = OpmlSerializer.ParseOpmlContent(opmlContent, 1).ToList();
        Assert.AreEqual(1, reimported.Count);
        Assert.AreEqual("https://example.com/feed1", reimported[0].Href);
        Assert.AreEqual(3, reimported[0].Tags.Count);
    }
}
