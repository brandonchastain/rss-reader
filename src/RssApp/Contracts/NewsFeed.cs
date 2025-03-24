namespace RssApp.Contracts;

public class NewsFeed : IEquatable<NewsFeed>
{

    public NewsFeed(string url, int userId, bool isPaywalled = false)
    {
        this.FeedUrl = url;
        this.UserId = userId;
        this.IsPaywalled = isPaywalled;
    }

    public string FeedUrl { get; set; }
    public bool IsPaywalled { get; set; }
    public int UserId { get; set; }

    public static async Task WriteCsvHeaderAsync(StreamWriter writer)
    {
        await writer.WriteLineAsync($"{nameof(FeedUrl)},{nameof(IsPaywalled)},{nameof(UserId)}");
    }

    public async Task WriteCsvAsync(StreamWriter writer)
    {
        await writer.WriteAsync($"{this.FeedUrl},");
        await writer.WriteAsync($"{this.IsPaywalled},");
        await writer.WriteAsync($"{this.UserId}" + Environment.NewLine);
    }

    public static NewsFeed ReadFromCsv(string csvLine)
    {
        var values = csvLine.Split(',');
        if (values.Length != 3)
        {
            Console.WriteLine(csvLine);
            throw new ArgumentException("Invalid CSV line");
        }

        return new NewsFeed(values[0], int.Parse(values[2]), bool.Parse(values[1]));
    }

    public bool Equals(NewsFeed other)
    {
        if (other == null)
        {
            return false;
        }

        return string.Equals(this.FeedUrl, other.FeedUrl, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return this.FeedUrl?.GetHashCode() ?? 0;
    }
}