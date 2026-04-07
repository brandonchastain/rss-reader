
using RssApp.Contracts;

namespace RssApp.Data;

public interface IUserRepository
{
    RssUser AddUser(string username, int? id = null);
    RssUser GetUserByName(string username);
    RssUser GetUserById(int userId);
    RssUser GetUserByAadId(string aadUserId);
    void SetAadUserId(int userId, string aadUserId);
    void DeleteUser(int userId);
    IEnumerable<RssUser> GetAllUsers();
}