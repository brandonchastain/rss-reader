using System.Threading.Tasks;
using RssApp.Contracts;

namespace WasmApp.Services;

public interface IUserClient : IDisposable
{
    /// <summary>The signed-in account's id from /auth/me, or null when signed out.</summary>
    Task<string> GetAccountIdAsync();

    /// <summary>The signed-in account. Sign-in creates it, so this only reads.</summary>
    Task<RssUser> GetFeedUserAsync();

    Task<UserDataReport> GetDataReportAsync();
    Task DeleteAccountAsync();
}
