using RssApp.Contracts;

namespace WasmApp.Services;

public interface IAdminClient
{
    Task<SystemStatsSnapshot> GetCurrentStatsAsync();
    Task<List<SystemStatsSnapshot>> GetStatsHistoryAsync();
}
