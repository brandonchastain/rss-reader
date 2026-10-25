// Offers a reload when a newer build has been deployed.
//
// Blazor WebAssembly loads the app once per page load, so a tab that stays open
// across a deploy keeps running the old build. blazor.boot.json is served
// no-cache and changes with every build, so its ETag identifies the running one:
// the first fetch is the baseline, and a different ETag later means a deploy.
// Checked when the tab returns to the foreground and every ten minutes while
// visible; background tabs make no requests.
(function () {
    var URL = '_framework/blazor.boot.json';
    var INTERVAL_MS = 10 * 60 * 1000;
    var baseline = null;
    var shown = false;

    function currentTag() {
        return fetch(URL, { method: 'HEAD', cache: 'no-cache' })
            .then(function (r) { return r.ok ? r.headers.get('ETag') : null; })
            .catch(function () { return null; });
    }

    function showBanner() {
        if (shown) return;
        shown = true;
        var bar = document.createElement('div');
        bar.className = 'update-banner';
        bar.setAttribute('role', 'status');
        bar.innerHTML = '<span>A new version is available.</span>';
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.textContent = 'Reload';
        btn.addEventListener('click', function () { location.reload(); });
        bar.appendChild(btn);
        document.body.appendChild(bar);
    }

    function check() {
        if (shown || document.hidden) return;
        currentTag().then(function (tag) {
            if (!tag) return;
            if (baseline === null) baseline = tag;
            else if (tag !== baseline) showBanner();
        });
    }

    check();
    setInterval(check, INTERVAL_MS);
    document.addEventListener('visibilitychange', check);
})();
