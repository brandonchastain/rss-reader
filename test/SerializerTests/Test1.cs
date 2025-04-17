namespace SerializerTests;
using RssApp.Serialization;
using Microsoft.Extensions.Logging;
using RssApp.Contracts;

[TestClass]
public sealed class Tests
{
    [TestMethod]
    public void TestSerializeBroken()
    {
        var content = File.ReadAllText("brokenFeed.xml");
        var serializer = new RssDeserializer(new Logger<RssDeserializer>(new LoggerFactory()));
        var feed = serializer.FromString(content, new RssUser("test", -99));
        Assert.IsNotNull(feed);
    }
}
