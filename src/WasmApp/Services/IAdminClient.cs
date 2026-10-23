using RssApp.Contracts;

namespace WasmApp.Services;

public record AdminUser(int Id, string Username, int FeedCount, int PostCount);

public interface IAdminClient
{
    Task<SystemStatsSnapshot> GetCurrentStatsAsync();
    /// <summary>History for a range key: 1h, 1d, 7d, 30d or 1y.</summary>
    Task<List<SystemStatsSnapshot>> GetStatsHistoryAsync(string range = "30d");
    /// <summary>Takes a fresh snapshot now (admin only) and returns it.</summary>
    Task<SystemStatsSnapshot> RefreshStatsAsync();
    Task<List<AdminUser>> GetUsersAsync();
}
