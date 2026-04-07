using RssApp.Contracts;

namespace RssApp.Data;

public interface ISystemStatsRepository
{
    void RecordSnapshot(SystemStatsSnapshot snapshot);
    SystemStatsSnapshot GetLatestSnapshot();
    IEnumerable<SystemStatsSnapshot> GetHistory(int days = 30);
    void CleanupOlderThan(int days = 30);
}
