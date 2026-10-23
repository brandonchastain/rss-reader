#nullable enable
using System.Text.Json.Serialization;

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

    // Subject of the user's first Microsoft identity (legacy Static Web Apps
    // id or Entra object id). Kept for the JSON shape; the server reads
    // Identities instead.
    public string? AadUserId { get; set; }

    // First login identity in provider:subject form. Kept for the JSON shape.
    public string? ProviderUserId { get; set; }

    public bool IsAdmin { get; set; }

    // Every login identity linked to this user, in provider:subject form
    // (email-hash links as microsoft-email:hash). Server-side only.
    [JsonIgnore]
    public IReadOnlyList<string> Identities { get; set; } = Array.Empty<string>();
}
