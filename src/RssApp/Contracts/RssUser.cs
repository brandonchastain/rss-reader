namespace RssApp.Contracts;

using RssApp.Contracts.FeedTypes;


public class RssUser
{
    public RssUser(string username, int id)
    {
        this.Username = username;
        this.Id = id;
    }
    public string Username { get; set; }

    public int Id { get; set; }

}