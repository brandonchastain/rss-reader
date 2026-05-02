using System.Net;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using WasmApp.Services;

namespace SerializerTests;

// Tests must run serially because AuthenticationHeaderHandler uses a static redirect guard
[DoNotParallelize]
[TestClass]
public sealed class AuthenticationHeaderHandlerTests
{
    private MockNavigationManager _navManager = null!;

    [TestInitialize]
    public void Setup()
    {
        _navManager = new MockNavigationManager("https://localhost/timeline");

        // Reset the static redirect guard between tests
        var field = typeof(AuthenticationHeaderHandler)
            .GetField("_isRedirecting", BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, 0);
    }

    [TestMethod]
    public async Task SendAsync_401_AnonymousUser_NavigatesHome()
    {
        var handler = CreateHandler(HttpStatusCode.Unauthorized, isAuthenticated: false);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };

        await client.GetAsync("/api/something");

        Assert.IsNotNull(_navManager.NavigatedTo, "Should navigate on 401");
        StringAssert.StartsWith(_navManager.NavigatedTo, "/?returnUrl=",
            "Anonymous user should be sent home, not to AAD");
        Assert.IsFalse(_navManager.NavigatedTo!.Contains("/.auth/login/aad"),
            "Should NOT redirect anonymous user to AAD login");
        Assert.IsTrue(_navManager.ForceLoad, "Should use forceLoad=true");
    }

    [TestMethod]
    public async Task SendAsync_401_AuthenticatedUser_NavigatesToAadForSilentReauth()
    {
        var handler = CreateHandler(HttpStatusCode.Unauthorized, isAuthenticated: true);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };

        await client.GetAsync("/api/something");

        Assert.IsNotNull(_navManager.NavigatedTo, "Should navigate on 401");
        StringAssert.StartsWith(_navManager.NavigatedTo, "/.auth/login/aad?post_login_redirect_uri=",
            "Session-expired authenticated user should be silently re-authed via AAD");
        Assert.IsTrue(_navManager.ForceLoad, "Should use forceLoad=true");
    }

    [TestMethod]
    public async Task SendAsync_401_ReturnUrlContainsCurrentUri()
    {
        var handler = CreateHandler(HttpStatusCode.Unauthorized, isAuthenticated: false);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };

        await client.GetAsync("/api/something");

        // The returnUrl should be the URL-encoded current page URI
        var expectedEncoded = Uri.EscapeDataString("https://localhost/timeline");
        Assert.IsTrue(_navManager.NavigatedTo!.Contains(expectedEncoded),
            $"returnUrl should contain the current page URI. Got: {_navManager.NavigatedTo}");
    }

    [TestMethod]
    public async Task SendAsync_200_DoesNotNavigate()
    {
        var handler = CreateHandler(HttpStatusCode.OK, isAuthenticated: false);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };

        await client.GetAsync("/api/something");

        Assert.IsNull(_navManager.NavigatedTo, "Should NOT navigate on a successful response");
    }

    [TestMethod]
    public async Task SendAsync_401_DoubleRedirectGuard_NavigatesOnlyOnce()
    {
        // Two separate handler instances share the static _isRedirecting guard
        var handler1 = CreateHandler(HttpStatusCode.Unauthorized, isAuthenticated: false);
        var handler2 = CreateHandler(HttpStatusCode.Unauthorized, isAuthenticated: false);
        var client1 = new HttpClient(handler1) { BaseAddress = new Uri("https://localhost/") };
        var client2 = new HttpClient(handler2) { BaseAddress = new Uri("https://localhost/") };

        await client1.GetAsync("/api/first");
        var firstNav = _navManager.NavigatedTo;

        // Reset capture so we can detect if a second navigation occurs
        _navManager.NavigatedTo = null;
        await client2.GetAsync("/api/second");

        Assert.IsNotNull(firstNav, "First 401 should trigger navigation");
        Assert.IsNull(_navManager.NavigatedTo, "Second 401 should be suppressed by the redirect guard");
    }

    // --- Helpers ---

    private AuthenticationHeaderHandler CreateHandler(HttpStatusCode responseStatus, bool isAuthenticated)
    {
        var handler = new AuthenticationHeaderHandler(
            new FakeAuthStateProvider(isAuthenticated),
            _navManager)
        {
            InnerHandler = new FakeHttpMessageHandler(responseStatus)
        };
        return handler;
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public FakeHttpMessageHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_status));
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        private readonly bool _isAuthenticated;
        public FakeAuthStateProvider(bool isAuthenticated) => _isAuthenticated = isAuthenticated;

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = _isAuthenticated
                ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "testuser")], "Test")
                : new ClaimsIdentity();
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    private sealed class MockNavigationManager : NavigationManager
    {
        public string? NavigatedTo { get; set; }
        public bool ForceLoad { get; private set; }

        public MockNavigationManager(string uri)
        {
            Initialize("https://localhost/", uri);
        }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            NavigatedTo = uri;
            ForceLoad = options.ForceLoad;
        }
    }
}
