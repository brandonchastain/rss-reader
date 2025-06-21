using RssApp.ComponentServices;
using RssApp.Contracts;
using System;
using System.Threading.Tasks;

namespace RssWasmApp.Mocks
{
    public class MockFeedRefresher : IFeedRefresher
    {
        public Task AddFeedAsync(NewsFeed feed) => Task.CompletedTask;
        public Task RefreshAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
