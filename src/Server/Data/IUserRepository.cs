using RssApp.Contracts;

namespace RssApp.Data;

public interface IUserRepository
{
    RssUser AddUser(string username, int? id = null);
    RssUser GetUserByName(string username);
    RssUser GetUserById(int userId);

    /// <summary>The user a login identity (UserIdentities row) belongs to, or null.</summary>
    RssUser FindByIdentity(string provider, string subject);

    /// <summary>
    /// Attaches a login identity to a user. A no-op when the identity is already
    /// linked (to this or any other user): an identity is never moved.
    /// </summary>
    void LinkIdentity(int userId, string provider, string subject);

    void UpdateUsername(int userId, string newUsername);
    void DeleteUser(int userId);
    IEnumerable<RssUser> GetAllUsers();
}
