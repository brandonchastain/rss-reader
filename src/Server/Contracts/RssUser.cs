namespace RssApp.Contracts;


public class RssUser
{
    public RssUser(string username, int id, bool isAdmin = false)
    {
        this.Username = username;
        this.Id = id;
        this.IsAdmin = isAdmin;
    }
    
    public string Username { get; set; }

    public int Id { get; set; }

    public bool IsAdmin { get; set; }
}