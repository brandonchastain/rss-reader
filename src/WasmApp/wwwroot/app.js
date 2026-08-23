const synth = window.speechSynthesis;
var speaking = false;
var activeSpeechCallback = null;
var activeUtterance = null;

function clearSpeech(utterance) {
  if (utterance && utterance !== activeUtterance) return; // stale end event from a cancelled utterance
  speaking = false;
  activeUtterance = null;
  if (activeSpeechCallback) {
    activeSpeechCallback.invokeMethodAsync('OnSpeechStopped');
    activeSpeechCallback = null;
  }
}

// dotNetRef: DotNetObjectReference — receives OnSpeechStopped callback when speech ends/is cancelled
window.speakThisText = function(text, dotNetRef) {
  if (speaking) {
    // Cancel current speech and notify the active component
    const prev = activeSpeechCallback;
    activeSpeechCallback = null;
    activeUtterance = null;
    speaking = false;
    synth.cancel();
    if (prev) prev.invokeMethodAsync('OnSpeechStopped');
    // If same component clicked stop (no new text), just stop
    if (!text) return;
  }

  const utterThis = new SpeechSynthesisUtterance(text);
  utterThis.addEventListener("end", () => clearSpeech(utterThis));
  utterThis.addEventListener("error", () => clearSpeech(utterThis));
  synth.speak(utterThis);
  speaking = true;
  activeUtterance = utterThis;
  activeSpeechCallback = dotNetRef;
}

window.stopSpeaking = function() {
  if (speaking) {
    const prev = activeSpeechCallback;
    activeSpeechCallback = null;
    activeUtterance = null;
    speaking = false;
    synth.cancel();
    if (prev) prev.invokeMethodAsync('OnSpeechStopped');
  }
}

// True when the tab is in the foreground. The timeline's "new posts" poll
// checks this before each tick so background tabs make no network calls.
window.isDocumentVisible = function() {
  return !document.hidden;
}

// Function to download a file
window.downloadFile = function (filename, contentType, content) {
    // Create a Blob with the file content
    const blob = new Blob([content], { type: contentType });

    // Create a link element
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = filename;

    // Add to the DOM and trigger the download
    document.body.appendChild(a);
    a.click();

    // Clean up
    document.body.removeChild(a);
    URL.revokeObjectURL(a.href);
};

window.scrollToElement = function(element) {
    if (element) {
        const rect = element.getBoundingClientRect();
        if (rect.top < 0) {
            element.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }
    }
};

// Snap the element to the top of the viewport instantly (used before collapsing a post
// so the collapse happens below the fold, keeping the infinite-scroll sentinel out of view).
// Only scrolls when the header's top edge has scrolled above the viewport top (rect.top < 0).
window.scrollToTopInstant = function(element) {
    if (element) {
        const rect = element.getBoundingClientRect();
        if (rect.top < 0) {
            element.scrollIntoView({ behavior: 'instant', block: 'start' });
        }
    }
};

window.Observer = {
    observer: null,
    Initialize: function (component, observerTargetId) {
        let _debounceTimer = null;
        let _isVisible = false;
        this.observer = new IntersectionObserver(entries => {
                _isVisible = entries[0].isIntersecting;
                if (_isVisible) {
                    // Debounce: only fire if the sentinel is still visible after 250ms.
                    // This prevents a collapsing post from briefly exposing the sentinel
                    // and triggering a spurious page load.
                    clearTimeout(_debounceTimer);
                    _debounceTimer = setTimeout(() => {
                        if (_isVisible) {
                            component.invokeMethodAsync('OnIntersection');
                        }
                    }, 250);
                } else {
                    clearTimeout(_debounceTimer);
                }
            },
            {
                root: null,
                rootMargin: '0px',
                threshold: [0]
            });

        let element = document.getElementById(observerTargetId);
        if (element == null) throw new Error("The observable target was not found");
        this.observer.observe(element);
    }
};

window.rssApp = {
    // Theme preference: persisted to localStorage so it survives reloads and
    // sessions. The initial theme is applied pre-boot by the inline script in
    // index.html; these helpers let Blazor read/toggle it at runtime.
    theme: {
        get: function () {
            return document.documentElement.getAttribute('data-theme') || 'dark';
        },
        set: function (theme) {
            var value = theme === 'light' ? 'light' : 'dark';
            document.documentElement.setAttribute('data-theme', value);
            try { localStorage.setItem('rssApp.theme', value); } catch (e) {}
            return value;
        },
        toggle: function () {
            var next = window.rssApp.theme.get() === 'light' ? 'dark' : 'light';
            return window.rssApp.theme.set(next);
        }
    },
    _loadedItemCount: 0,
    setLoadedItemCount: function(count) {
        window.rssApp._loadedItemCount = count;
    },
    saveScrollStateAndNavigate: function(postId, targetHref, markReadUrl) {
        // Use Blazor-provided count (works with Virtualize) with DOM fallback
        var itemCount = window.rssApp._loadedItemCount || document.querySelectorAll('[data-post-id]').length;
        var pageEstimate = Math.ceil(itemCount / 20);
        sessionStorage.setItem('rssApp.scrollAnchorPostId', postId);
        sessionStorage.setItem('rssApp.scrollAnchorPage', pageEstimate.toString());
        sessionStorage.setItem('rssApp.scrollAnchorPath', window.location.pathname);
        // Fire mark-as-read with keepalive so it completes even after navigation
        if (markReadUrl) {
            fetch(markReadUrl, { method: 'GET', keepalive: true }).catch(function() {});
        }
        window.location.href = targetHref;
    },
    saveScrollStateForLink: function(postId, markReadUrl) {
        // Synchronous variant: writes scroll anchor + fires fire-and-forget
        // markAsRead. Called from a capture-phase document click listener so
        // it runs before any Blazor handler / re-render.
        if (postId) {
            var itemCount = window.rssApp._loadedItemCount || document.querySelectorAll('[data-post-id]').length;
            var pageEstimate = Math.ceil(itemCount / 20);
            sessionStorage.setItem('rssApp.scrollAnchorPostId', postId);
            sessionStorage.setItem('rssApp.scrollAnchorPage', pageEstimate.toString());
            sessionStorage.setItem('rssApp.scrollAnchorPath', window.location.pathname);
        }
        if (markReadUrl) {
            // sendBeacon is specifically designed to fire reliably during
            // page unload (cross-origin navigation). It's the most robust
            // way to ensure the markAsRead request reaches the server.
            // The endpoint accepts both GET and POST.
            var sent = false;
            try {
                if (navigator.sendBeacon) {
                    sent = navigator.sendBeacon(markReadUrl);
                }
            } catch (e) { sent = false; }
            if (!sent) {
                // Fallback for older browsers or when sendBeacon refuses
                // (e.g., quota exceeded). keepalive lets the request outlive
                // the document on best-effort basis.
                try {
                    fetch(markReadUrl, { method: 'POST', keepalive: true, credentials: 'same-origin' }).catch(function() {});
                } catch (e) {}
            }
        }
    },
    getScrollState: function() {
        var postId = sessionStorage.getItem('rssApp.scrollAnchorPostId');
        if (!postId) return null;
        var page = parseInt(sessionStorage.getItem('rssApp.scrollAnchorPage') || '0');
        var path = sessionStorage.getItem('rssApp.scrollAnchorPath') || '';
        return { postId: postId, page: page, path: path };
    },
    clearScrollState: function() {
        sessionStorage.removeItem('rssApp.scrollAnchorPostId');
        sessionStorage.removeItem('rssApp.scrollAnchorPage');
        sessionStorage.removeItem('rssApp.scrollAnchorPath');
    },
    scrollToEstimatedIndex: function(index, itemSize) {
        var estimatedPosition = index * itemSize;
        window.scrollTo(0, estimatedPosition);
    },
    scrollToPost: function(postId) {
        var el = document.querySelector('[data-post-id="' + postId + '"]');
        if (el) {
            el.scrollIntoView({ behavior: 'instant', block: 'center' });
            return true;
        }
        return false;
    },

    // Cold-start cache. The API runs scale-to-zero, so waking the container costs
    // ~20s of platform time no request can avoid. This keeps the last-seen
    // timeline, the user record, the tag list, and recently-read post bodies in
    // localStorage so the app can paint from them immediately and reconcile with
    // the server in the background.
    //
    // localStorage (not IndexedDB) is deliberate: reads are synchronous, so
    // hydration paints before anything awaits, and measured content is ~500 bytes
    // per post -- a 50-post page of bodies is ~25KB. The caps below exist only so
    // one pathological post (the largest in the live DB is 2.7MB) can't fill the
    // ~5MB origin budget.
    cache: {
        VERSION: 'v1',
        MAX_ITEM_BYTES: 64 * 1024,
        MAX_CONTENT_BYTES: 2 * 1024 * 1024,

        _key: function (user, slot) {
            return 'rssApp.cache.' + window.rssApp.cache.VERSION + '.' + user + '.' + slot;
        },
        _get: function (user, slot) {
            if (!user) return null;
            try { return localStorage.getItem(window.rssApp.cache._key(user, slot)); }
            catch (e) { return null; }
        },
        _set: function (user, slot, value) {
            if (!user) return false;
            try {
                localStorage.setItem(window.rssApp.cache._key(user, slot), value);
                return true;
            } catch (e) {
                // Quota exceeded (or storage disabled). Drop the content slot --
                // the largest and most regenerable -- and retry once.
                try {
                    localStorage.removeItem(window.rssApp.cache._key(user, 'content'));
                    localStorage.setItem(window.rssApp.cache._key(user, slot), value);
                    return true;
                } catch (e2) {
                    return false;
                }
            }
        },

        getTimeline: function (user) { return window.rssApp.cache._get(user, 'timeline'); },
        setTimeline: function (user, json) { return window.rssApp.cache._set(user, 'timeline', json); },
        getUser: function (user) { return window.rssApp.cache._get(user, 'user'); },
        setUser: function (user, json) { return window.rssApp.cache._set(user, 'user', json); },
        getTags: function (user) { return window.rssApp.cache._get(user, 'tags'); },
        setTags: function (user, json) { return window.rssApp.cache._set(user, 'tags', json); },

        // Post bodies, newest-write-first so trimming drops the least recently
        // written. Stored as an array rather than an object because integer-like
        // object keys are ordered numerically by the JS engine, which would
        // silently destroy recency order.
        getContent: function (user, itemId) {
            var raw = window.rssApp.cache._get(user, 'content');
            if (!raw) return null;
            try {
                var entries = JSON.parse(raw);
                for (var i = 0; i < entries.length; i++) {
                    if (entries[i].id === itemId) return entries[i].c;
                }
            } catch (e) { }
            return null;
        },

        // mapJson: { "<itemId>": "<base64 content>" }. Merges into the existing
        // entries, newest first, dropping oversized items and trimming to budget.
        mergeContent: function (user, mapJson) {
            if (!user) return false;
            var incoming;
            try { incoming = JSON.parse(mapJson); } catch (e) { return false; }

            var existing = [];
            var raw = window.rssApp.cache._get(user, 'content');
            if (raw) { try { existing = JSON.parse(raw) || []; } catch (e) { existing = []; } }

            var seen = {};
            var merged = [];
            Object.keys(incoming).forEach(function (id) {
                var c = incoming[id];
                if (typeof c !== 'string' || c.length > window.rssApp.cache.MAX_ITEM_BYTES) return;
                seen[id] = true;
                merged.push({ id: id, c: c });
            });
            existing.forEach(function (e) {
                if (e && e.id && !seen[e.id]) { seen[e.id] = true; merged.push(e); }
            });

            var total = 0;
            var trimmed = [];
            for (var i = 0; i < merged.length; i++) {
                total += merged[i].c.length;
                if (total > window.rssApp.cache.MAX_CONTENT_BYTES) break;
                trimmed.push(merged[i]);
            }

            return window.rssApp.cache._set(user, 'content', JSON.stringify(trimmed));
        },

        // Drops cached slots. With a user, drops that user's slots (sign-out,
        // account delete, cleared posts); without one, drops every cache key.
        clear: function (user) {
            var prefix = user
                ? 'rssApp.cache.' + window.rssApp.cache.VERSION + '.' + user + '.'
                : 'rssApp.cache.';
            window.rssApp.cache._removeMatching(function (k) { return k.indexOf(prefix) === 0; });
        },

        // Sweeps slots that belong to a different user or an older cache version,
        // so a shared browser or a shape change can't serve someone else's posts.
        prune: function (user) {
            if (!user) return;
            var mine = 'rssApp.cache.' + window.rssApp.cache.VERSION + '.' + user + '.';
            window.rssApp.cache._removeMatching(function (k) {
                return k.indexOf('rssApp.cache.') === 0 && k.indexOf(mine) !== 0;
            });
        },

        _removeMatching: function (predicate) {
            try {
                var doomed = [];
                for (var i = 0; i < localStorage.length; i++) {
                    var k = localStorage.key(i);
                    if (k && predicate(k)) doomed.push(k);
                }
                doomed.forEach(function (k) { localStorage.removeItem(k); });
            } catch (e) { }
        }
    }
};

document.addEventListener('DOMContentLoaded', () => {
    const contentContainer = document.getElementById('focus-post-content-container');
    if (contentContainer) {
        contentContainer.addEventListener('click', (e) => {
            // Only expand if not already expanded and not clicking a link
            if (!e.target.closest('a') && !contentContainer.classList.contains('expanded')) {
                contentContainer.classList.add('expanded');
            }
        });
    }
});

// Capture-phase delegated handler: saves scroll anchor + fires keepalive
// mark-as-read whenever the user activates an article-link <a>. Runs before
// Blazor's bubble-phase @onclick handlers, so a Blazor re-render cannot
// cancel the JS interop. Works for left-click, ctrl-click, and keyboard
// activation (Enter on focused link fires click). Middle-click fires
// auxclick (not click) in modern browsers, so listen on both.
function articleLinkActivated(e) {
    var link = e.target.closest('a[data-article-link]');
    if (!link) return;
    var postId = link.getAttribute('data-post-id');
    var markReadUrl = link.getAttribute('data-mark-read-url');
    window.rssApp.saveScrollStateForLink(postId, markReadUrl);
}
document.addEventListener('click', articleLinkActivated, true);
document.addEventListener('auxclick', articleLinkActivated, true);