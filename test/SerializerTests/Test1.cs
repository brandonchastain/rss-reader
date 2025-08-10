namespace SerializerTests;
using RssApp.Serialization;
using RssApp.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

[TestClass]
public sealed class Tests
{
    [TestMethod]
    public void TestSerializeBroken()
    {
        var content = File.ReadAllText("brokenFeed.xml");
        var serializer = new RssDeserializer(new NullLogger<RssDeserializer>());
        var feed = serializer.FromString(content, new RssUser("test", -99));
        Assert.IsNotNull(feed);
    }
}
