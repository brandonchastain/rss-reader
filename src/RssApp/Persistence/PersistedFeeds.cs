
namespace RssApp.Persistence;

public class PersistedFeeds
{
    private static readonly string FileName = "feeds.csv";
    private List<string> feeds;
    public PersistedFeeds()
    {
        if (!File.Exists(FileName))
        {
            using (File.Create(FileName))
            {

            }
        }

        this.feeds = new List<string>();
        var contents = File.ReadAllText(FileName);
        foreach (string feed in contents.Split(","))
        {
            this.feeds.Add(feed);
        }
    }

    public IEnumerable<string> GetFeeds()
    {
        return this.feeds;
    }

    public void AddFeed(string url)
    {
        this.feeds.Add(url);
        this.SaveFeed();
    }

    public void DeleteFeed(string url)
    {
        this.feeds.Remove(url);
        this.SaveFeed();
    }

    private void SaveFeed()
    {
        File.WriteAllText(FileName, string.Join(",", this.feeds));
    }
}