using RssApp.Contracts;

namespace WasmApp.Services;

public record AdminUser(int Id, string Username);

public interface IAdminClient
{
    Task<SystemStatsSnapshot> GetCurrentStatsAsync();
    Task<List<SystemStatsSnapshot>> GetStatsHistoryAsync();
    Task<List<AdminUser>> GetUsersAsync();
}
