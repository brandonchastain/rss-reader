using System.Threading.Tasks;
using RssApp.Contracts;

namespace WasmApp.Services;

public interface IUserClient
{
    Task<string> GetUsernameAsync();
    Task<(RssUser, bool)> GetFeedUserAsync();
    Task<(RssUser, bool)> RegisterUserAsync(string username);
}