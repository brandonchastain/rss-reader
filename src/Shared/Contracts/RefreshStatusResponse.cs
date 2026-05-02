namespace RssApp.Contracts
{
    public class RefreshStatusResponse
    {
        public bool HasNewItems { get; set; }
        public bool IsRefreshing { get; set; }
        public int PendingFeeds { get; set; }
    }
}
