namespace RssReaderTests;

using RssReader.Shared.Extensions;

[TestClass]
public sealed class RootDomainTests
{
    [DataTestMethod]
    [DataRow("https://www.theverge.com/news/1", "theverge.com")]
    [DataRow("https://feeds.arstechnica.com/arstechnica/index", "arstechnica.com")]
    [DataRow("https://xkcd.com/rss.xml", "xkcd.com")]
    [DataRow("https://www.bbc.co.uk/news/articles/x", "bbc.co.uk")]
    [DataRow("https://feeds.bbci.co.uk/news/rss.xml", "bbci.co.uk")]
    [DataRow("https://www.abc.net.au/news/feed", "abc.net.au")]
    [DataRow("https://www.businessexaminer.ca/feed", "businessexaminer.ca")]
    [DataRow("https://hunch.net/?feed=rss2", "hunch.net")]
    public void GetRootDomain_KeepsTheSiteName(string url, string expected)
    {
        Assert.AreEqual(expected, url.GetRootDomain());
    }

    [TestMethod]
    public void GetRootDomain_EmptyIsEmpty()
    {
        Assert.AreEqual(string.Empty, "".GetRootDomain());
    }
}
