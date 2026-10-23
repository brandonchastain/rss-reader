using RssApp.Contracts;

namespace WasmApp.Services;

public record AdminUser(int Id, string Username, int FeedCount, int PostCount);

public interface IAdminClient
{
    Task<SystemStatsSnapshot> GetCurrentStatsAsync();
    Task<List<SystemStatsSnapshot>> GetStatsHistoryAsync(int days = 30);
    /// <summary>Takes a fresh snapshot now (admin only) and returns it.</summary>
    Task<SystemStatsSnapshot> RefreshStatsAsync();
    Task<List<AdminUser>> GetUsersAsync();
}
