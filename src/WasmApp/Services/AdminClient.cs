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

    public async Task<SystemStatsSnapshot> RefreshStatsAsync()
    {
        var response = await _apiClient.PostAsync("api/admin/stats/refresh", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SystemStatsSnapshot>();
    }

    public async Task<List<SystemStatsSnapshot>> GetStatsHistoryAsync(int days = 30)
    {
        return await _apiClient.GetFromJsonAsync<List<SystemStatsSnapshot>>($"api/admin/stats/history?days={days}")
               ?? new List<SystemStatsSnapshot>();
    }

    public async Task<List<AdminUser>> GetUsersAsync()
    {
        return await _apiClient.GetFromJsonAsync<List<AdminUser>>("api/admin/users")
               ?? new List<AdminUser>();
    }
}
