using System.Threading.Tasks;
using RssApp.Contracts;

namespace WasmApp.Services;

public interface IUserClient : IDisposable
{
    Task<string> GetUsernameAsync();
    Task<RssUser> GetFeedUserAsync();
    Task<(RssUser, bool)> RegisterUserAsync(string username);
}