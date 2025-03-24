namespace RssApp.Contracts;

public class RssUser
{
    public RssUser(string username, int id)
    {
        this.Username = username;
        this.Id = id;
    }
    public string Username { get; set; }

    public int Id { get; set; }

    public static RssUser ReadFromCsv(string csv)
    {
        var parts = csv.Split(',');
        if (parts.Length != 2)
        {
            throw new ArgumentException("Invalid CSV format");
        }

        var user = new RssUser(parts[0], int.Parse(parts[1]));
        return user;
    }

}