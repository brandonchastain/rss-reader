#nullable enable
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

    public string? AadUserId { get; set; }

    public bool IsAdmin { get; set; }
}