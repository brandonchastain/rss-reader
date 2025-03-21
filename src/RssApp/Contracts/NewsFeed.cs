namespace RssApp.Contracts;

public class NewsFeed
{

    public NewsFeed(string url, bool isPaywalled = false)
    {
        this.FeedUrl = url;
        this.IsPaywalled = isPaywalled;
    }

    public string FeedUrl { get; set; }
    public bool IsPaywalled { get; set; }

    public static async Task WriteCsvHeaderAsync(StreamWriter writer)
    {
        await writer.WriteLineAsync(
            $"{nameof(FeedUrl)},{nameof(IsPaywalled)}");
    }

    public async Task WriteCsvAsync(StreamWriter writer)
    {
        await writer.WriteAsync($"{this.FeedUrl},");
        await writer.WriteAsync($"{this.IsPaywalled}" + Environment.NewLine);
    }

    public static NewsFeed ReadFromCsv(string csvLine)
    {
        var values = csvLine.Split(',');
        if (values.Length != 2)
        {
            Console.WriteLine(csvLine);
            throw new ArgumentException("Invalid CSV line");
        }

        return new NewsFeed(values[0], bool.Parse(values[1]));
    }
}