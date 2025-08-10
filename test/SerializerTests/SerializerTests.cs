namespace SerializerTests;
using RssApp.Serialization;
using RssApp.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

[TestClass]
public sealed class SerializerTests
{
    [TestMethod]
    public void Deserialized_Feed_Should_Not_Be_Null()
    {
        foreach (var file in Directory.GetFiles("feeds"))
        {
            var content = File.ReadAllText(file);
            var serializer = new RssDeserializer(new NullLogger<RssDeserializer>());
            var feed = serializer.FromString(content, new RssUser("test", -99));
            Assert.IsNotNull(feed);
        }
    }
}
