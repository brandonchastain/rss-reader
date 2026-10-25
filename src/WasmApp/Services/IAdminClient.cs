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
    /// <summary>The table behind a stat card: users, feeds, posts or db.</summary>
    Task<AdminTable> GetDetailAsync(string section);
    /// <summary>Deletes a user, feed or post by id (admin only).</summary>
    Task DeleteAsync(string section, long id);
}
