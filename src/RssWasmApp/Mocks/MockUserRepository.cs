using RssApp.Data;
using RssApp.Contracts;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RssWasmApp.Mocks
{
    public class MockUserRepository : IUserRepository
    {
        public Task<RssUser> GetUserByIdAsync(string userId)
        {
            int idInt = int.TryParse(userId, out var id) ? id : 1; // Default to 1 if parsing fails
            return Task.FromResult(new RssUser("MockUser", idInt));
        }

        public RssUser AddUser(string username, int? id = null) => new RssUser(username, id ?? 1);
        public RssUser? GetUserByName(string username) => new RssUser(username, 1);
        public RssUser? GetUserById(int userId) => new RssUser("MockUser", userId);
        public IEnumerable<RssUser> GetAllUsers() => new List<RssUser> { new RssUser("MockUser", 1) };
    }
}
