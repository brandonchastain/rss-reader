using System.Net.Http.Json;
using RssApp.Contracts;

namespace WasmApp.Services;

public class AdminClient : IAdminClient
{
    private readonly HttpClient _apiClient;

    public AdminClient(IHttpClientFactory httpClientFactory)
    {
        _apiClient = httpClientFactory.CreateClient("ApiClient");
    }

    public async Task<SystemStatsSnapshot> GetCurrentStatsAsync()
    {
        return await _apiClient.GetFromJsonAsync<SystemStatsSnapshot>("api/admin/stats");
    }

    public async Task<List<SystemStatsSnapshot>> GetStatsHistoryAsync()
    {
        return await _apiClient.GetFromJsonAsync<List<SystemStatsSnapshot>>("api/admin/stats/history")
               ?? new List<SystemStatsSnapshot>();
    }
}
