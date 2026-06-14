namespace RssApp.Contracts
{
    public class RefreshStatusResponse
    {
        public bool HasNewItems { get; set; }
        public bool IsRefreshing { get; set; }
        public int PendingFeeds { get; set; }

        // Number of new posts inserted by the most recent refresh. Lets the
        // client show "N new posts" instead of a bare "new posts available".
        public int NewItemCount { get; set; }
    }
}
