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
    // Reading-view font and text size, persisted like the theme and applied
    // as data-reader-font and --reader-scale on <html>. The pre-boot script in
    // index.html applies the saved choice before first paint; keep the two in
    // step.
    reader: {
        fonts: ['mono', 'serif', 'sans'],
        get: function () {
            var font = 'mono', size = 100;
            try {
                var saved = JSON.parse(localStorage.getItem('rssApp.reader') || '{}');
                if (window.rssApp.reader.fonts.indexOf(saved.font) >= 0) font = saved.font;
                if (saved.size >= 80 && saved.size <= 160) size = saved.size;
            } catch (e) {}
            return { font: font, size: size };
        },
        set: function (font, size) {
            window.rssApp.reader.apply(font, size);
            try { localStorage.setItem('rssApp.reader', JSON.stringify({ font: font, size: size })); } catch (e) {}
        },
        apply: function (font, size) {
            var root = document.documentElement;
            root.setAttribute('data-reader-font', font);
            root.style.setProperty('--reader-scale', String(size / 100));
        }
    },
    _loadedItemCount: 0,
    setLoadedItemCount: function(count) {
        window.rssApp._loadedItemCount = count;
    },
    saveScrollStateAndNavigate: function(postId, targetHref, markReadUrl) {
        // Use the Blazor-provided count: with Virtualize the DOM only holds the
        // rows near the viewport, so the DOM fallback undercounts.
        var itemCount = window.rssApp._loadedItemCount || document.querySelectorAll('[data-post-id]').length;
        var pageEstimate = Math.ceil(itemCount / 20);
        sessionStorage.setItem('rssApp.scrollAnchorPostId', postId);
        sessionStorage.setItem('rssApp.scrollAnchorPage', pageEstimate.toString());
        sessionStorage.setItem('rssApp.scrollAnchorPath', window.location.pathname);
        window.rssApp.markReadBeforeNavigate(postId, markReadUrl);
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
        window.rssApp.markReadBeforeNavigate(postId, markReadUrl);
    },

    // Marks a post read in a way that survives the page unloading a moment
    // later for an outbound link. Everything here is synchronous except the
    // request itself, and it runs in the capture phase of the click, before
    // Blazor's own handler and before the browser starts navigating.
    //
    //  1. The cached timeline slots and the write-behind outbox are patched
    //     first, in localStorage. If the request below never lands (the tab
    //     unloads first, the API is asleep, read-only during a deploy), the
    //     outbox replays it on the load after Back -- the same path an
    //     offline mark-read already takes.
    //  2. The row is flipped to read in the DOM so a back/forward-cache
    //     snapshot of this page already shows the post read.
    //  3. The request goes out as fetch keepalive with credentials: the API
    //     is a different origin and the session cookie lives there, which is
    //     why the old sendBeacon to a frontend-origin path never authenticated.
    //     POST rather than GET so the browser's HTTP cache can never answer it.
    markReadBeforeNavigate: function(postId, markReadUrl) {
        var cache = window.rssApp.cache;
        var user = cache._activeUser;
        var ts = Date.now();
        if (postId && user) {
            cache.patchItem(user, postId, true, null);
            cache.enqueueOutbox(user, postId, 'read', true, ts);
        }
        if (postId) {
            var row = document.querySelector('[data-post-id="' + postId + '"].row');
            if (row) {
                row.classList.remove('post-unread');
                row.classList.add('post-read');
            }
        }
        if (!markReadUrl) return;
        try {
            fetch(markReadUrl, { method: 'POST', keepalive: true, credentials: 'include' })
                .then(function(response) {
                    // Only reached if this page is still alive; otherwise the
                    // outbox entry stays and replays harmlessly (idempotent).
                    if (response.ok && postId && user) {
                        cache.removeOutbox(user, postId, 'read', ts);
                    }
                })
                .catch(function() {});
        } catch (e) {}
    },

    // A page restored from the back/forward cache never re-ran its load, so
    // let the timeline component replay the outbox and re-apply it to the
    // rows it is showing.
    _pageRestoredRef: null,
    onPageRestored: function(dotNetRef) {
        window.rssApp._pageRestoredRef = dotNetRef;
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
    scrollToPost: function(postId) {
        var el = document.querySelector('[data-post-id="' + postId + '"]');
        if (el) {
            el.scrollIntoView({ behavior: 'instant', block: 'center' });
            return true;
        }
        return false;
    },

    // Windowing for the timeline (PostTable). Keeps the DOM bounded without ever
    // moving the content under the reader:
    //
    //  - Trim: once the page has been idle briefly and at least a page of rows
    //    sits more than a screen above the viewport, measure those rows and hand
    //    the count and per-row heights to .NET, which drops them from the DOM and
    //    grows the spacer by exactly their sum. Net height above the viewport is
    //    unchanged, so the scroll position needs no correction.
    //  - Restore: when the spacer comes within 1.5 screens of the viewport, .NET
    //    puts rows back a page at a time and shrinks the spacer by their recorded
    //    heights, again net zero.
    //
    // A row's measured height runs from the start of its entry (its day
    // separator if it has one, else the row) to the start of the next entry, so
    // an expanded post body or a hidden (filtered) row is accounted for exactly.
    listWindow: {
        _state: null,

        init: function(dotNetRef) {
            var lw = window.rssApp.listWindow;
            lw.dispose();

            var table = document.getElementById('post-table');
            if (!table) return;

            // Trim keeps more above the viewport than restore reaches for, so a
            // trim never triggers the restore that would undo it.
            var SCREENS_ABOVE = 2;    // rows kept rendered above the viewport
            var MIN_TRIM = 20;        // don't trim fewer rows than this at once
            var IDLE_MS = 200;        // reader must be still this long before a trim
            var RESTORE_MARGIN = 1;   // screens of lead time for a restore

            var idleTimer = null;
            var busy = false;
            var observed = null;

            function spacer() { return document.getElementById('post-table-top-spacer'); }

            // Top edge of entry i, given the row elements. A hidden row has no
            // box of its own, so its entry starts where the next one does.
            function entryStarts(rows, count) {
                var starts = new Array(count + 1);
                starts[count] = startOf(rows[count], null);
                for (var i = count - 1; i >= 0; i--) {
                    starts[i] = startOf(rows[i], starts[i + 1]);
                }
                return starts;
            }
            function startOf(row, nextStart) {
                var prev = row.previousElementSibling;
                if (prev && prev.classList.contains('day-separator')) return prev.getBoundingClientRect().top;
                if (row.hidden && nextStart !== null) return nextStart;
                return row.getBoundingClientRect().top;
            }

            async function maybeTrim() {
                if (busy) return;
                var rows = table.querySelectorAll('[data-post-id].row');
                var limit = -window.innerHeight * SCREENS_ABOVE;
                var k = 0;
                while (k < rows.length - 1 && (rows[k].hidden || rows[k].getBoundingClientRect().bottom < limit)) k++;
                if (k < MIN_TRIM) return;

                var starts = entryStarts(rows, k);
                var heights = [];
                for (var i = 0; i < k; i++) heights.push(Math.max(0, starts[i + 1] - starts[i]));

                busy = true;
                try { await dotNetRef.invokeMethodAsync('TrimTop', k, heights); }
                catch (e) { }
                finally { busy = false; }
            }

            async function restore() {
                if (busy) return;
                busy = true;
                try {
                    for (var guard = 0; guard < 50; guard++) {
                        var sp = spacer();
                        if (!sp) break;
                        if (sp.getBoundingClientRect().bottom < -window.innerHeight * RESTORE_MARGIN) break;
                        var remaining = await dotNetRef.invokeMethodAsync('RestoreTop');
                        if (remaining <= 0) break;
                        await new Promise(function(res) { requestAnimationFrame(res); });
                    }
                } catch (e) { }
                finally { busy = false; }
            }

            var io = new IntersectionObserver(function(entries) {
                for (var i = 0; i < entries.length; i++) {
                    if (entries[i].isIntersecting) { restore(); return; }
                }
            }, { root: null, rootMargin: (RESTORE_MARGIN * 100) + '% 0px 0px 0px', threshold: 0 });

            // The spacer element is recreated when the list branch re-renders
            // (filter switch, reload), so re-point the observer when it changes.
            function watchSpacer() {
                var sp = spacer();
                if (sp === observed) return;
                if (observed) io.unobserve(observed);
                observed = sp;
                if (sp) io.observe(sp);
            }

            function onScroll() {
                watchSpacer();
                clearTimeout(idleTimer);
                idleTimer = setTimeout(maybeTrim, IDLE_MS);
            }

            // Recorded heights are only valid at the width they were measured
            // at; a rotation or window resize invalidates them all.
            var lastWidth = window.innerWidth;
            var resizeTimer = null;
            function onResize() {
                if (window.innerWidth === lastWidth) return;
                lastWidth = window.innerWidth;
                clearTimeout(resizeTimer);
                resizeTimer = setTimeout(async function() {
                    if (busy) return;
                    busy = true;
                    try { await dotNetRef.invokeMethodAsync('RestoreAll'); }
                    catch (e) { }
                    finally { busy = false; }
                }, 150);
            }

            watchSpacer();
            window.addEventListener('scroll', onScroll, { passive: true });
            window.addEventListener('resize', onResize);

            lw._state = {
                dispose: function() {
                    clearTimeout(idleTimer);
                    clearTimeout(resizeTimer);
                    io.disconnect();
                    window.removeEventListener('scroll', onScroll);
                    window.removeEventListener('resize', onResize);
                }
            };
        },

        dispose: function() {
            var lw = window.rssApp.listWindow;
            if (lw._state) { lw._state.dispose(); lw._state = null; }
        }
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

        // One timeline slot per filter view -- 'all', 'unread', 'tag-tech' and so
        // on -- so switching filters hydrates from the last time that view was
        // open rather than waiting on the API. Slots are bounded by an LRU index:
        // a reader with twenty tags would otherwise accumulate a slot per tag.
        MAX_TIMELINE_SLOTS: 6,

        _timelineSlot: function (sig) { return 'timeline.' + (sig || 'all'); },

        getTimeline: function (user, sig) {
            var v = window.rssApp.cache._get(user, window.rssApp.cache._timelineSlot(sig));
            if (v) window.rssApp.cache._touchSlot(user, sig);
            return v;
        },
        setTimeline: function (user, sig, json) {
            var ok = window.rssApp.cache._set(user, window.rssApp.cache._timelineSlot(sig), json);
            if (ok) window.rssApp.cache._touchSlot(user, sig);
            return ok;
        },

        // Recency order of timeline slots, most recent first. Evicts the tail
        // past MAX_TIMELINE_SLOTS, removing the slot itself as well as the entry.
        _touchSlot: function (user, sig) {
            sig = sig || 'all';
            var order = window.rssApp.cache._slotOrder(user);
            order = order.filter(function (s) { return s !== sig; });
            order.unshift(sig);

            var evicted = order.slice(window.rssApp.cache.MAX_TIMELINE_SLOTS);
            order = order.slice(0, window.rssApp.cache.MAX_TIMELINE_SLOTS);
            evicted.forEach(function (s) {
                try { localStorage.removeItem(window.rssApp.cache._key(user, window.rssApp.cache._timelineSlot(s))); }
                catch (e) { }
            });

            window.rssApp.cache._set(user, 'timeline.index', JSON.stringify(order));
        },

        _slotOrder: function (user) {
            var raw = window.rssApp.cache._get(user, 'timeline.index');
            if (!raw) return [];
            try { return JSON.parse(raw) || []; } catch (e) { return []; }
        },

        // Applies a read/saved change to every cached slot holding the item. A
        // post can appear in several views at once, and leaving stale copies
        // behind is what would make an 'unread' slot resurface posts already
        // read -- the reason filtered views were excluded from the cache before.
        patchItem: function (user, itemId, isRead, isSaved) {
            if (!user || !itemId) return;
            window.rssApp.cache._slotOrder(user).forEach(function (sig) {
                var slot = window.rssApp.cache._timelineSlot(sig);
                var raw = window.rssApp.cache._get(user, slot);
                if (!raw) return;
                try {
                    var items = JSON.parse(raw);
                    var touched = false;
                    for (var i = 0; i < items.length; i++) {
                        if (items[i].Id !== itemId) continue;
                        if (isRead !== null) { items[i].IsRead = isRead; touched = true; }
                        if (isSaved !== null) { items[i].IsSaved = isSaved; touched = true; }
                    }
                    if (touched) window.rssApp.cache._set(user, slot, JSON.stringify(items));
                } catch (e) { }
            });
        },

        getUser: function (user) { return window.rssApp.cache._get(user, 'user'); },
        setUser: function (user, json) { return window.rssApp.cache._set(user, 'user', json); },
        getTags: function (user) { return window.rssApp.cache._get(user, 'tags'); },
        setTags: function (user, json) { return window.rssApp.cache._set(user, 'tags', json); },

        // Write-behind outbox: read/saved changes the server hasn't accepted yet.
        getOutbox: function (user) { return window.rssApp.cache._get(user, 'outbox'); },
        setOutbox: function (user, json) { return window.rssApp.cache._set(user, 'outbox', json); },

        // The user whose slots the synchronous link handler patches; set from
        // PostCache.Username so the two sides always agree on the partition.
        _activeUser: null,
        setActiveUser: function (user) { window.rssApp.cache._activeUser = user || null; },

        _outboxEntries: function (user) {
            var raw = window.rssApp.cache.getOutbox(user);
            if (!raw) return [];
            try { return JSON.parse(raw) || []; } catch (e) { return []; }
        },

        // Mirrors PendingWrites.Collapse: one entry per (item, kind), holding
        // the latest value. Property names are PascalCase because PostCache
        // deserializes this JSON into the PendingWrite record.
        enqueueOutbox: function (user, itemId, kind, value, ts) {
            if (!user || !itemId) return;
            var entries = window.rssApp.cache._outboxEntries(user).filter(function (e) {
                return !(e && e.ItemId === itemId && e.Kind === kind);
            });
            entries.push({ ItemId: itemId, Kind: kind, Value: value, Ts: ts });
            window.rssApp.cache.setOutbox(user, JSON.stringify(entries));
        },

        // Drops the entry enqueued at `ts` once the server accepted it. A later
        // write for the same item (different Ts) is left alone.
        removeOutbox: function (user, itemId, kind, ts) {
            if (!user || !itemId) return;
            var entries = window.rssApp.cache._outboxEntries(user);
            var kept = entries.filter(function (e) {
                return !(e && e.ItemId === itemId && e.Kind === kind && e.Ts === ts);
            });
            if (kept.length !== entries.length) {
                window.rssApp.cache.setOutbox(user, JSON.stringify(kept));
            }
        },

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

// The post headline is a real link to the original so that ctrl/cmd/shift-
// click and middle-click still open it, but a plain click opens the in-app
// reader instead: cancel the navigation here and let the Blazor handler on the
// link expand the post. The opening-the-original cases are marked read the
// same way as any other article link.
function headlineActivated(e) {
    var link = e.target.closest('a.post-headline');
    if (!link) return;
    var opensOriginal = e.type === 'auxclick'
        ? e.button === 1
        : e.ctrlKey || e.metaKey || e.shiftKey || e.altKey;
    if (opensOriginal) {
        window.rssApp.saveScrollStateForLink(link.getAttribute('data-post-id'), link.getAttribute('data-mark-read-url'));
    } else if (e.type === 'click') {
        e.preventDefault();
    }
}
document.addEventListener('click', headlineActivated, true);
document.addEventListener('auxclick', headlineActivated, true);

// Keyboard shortcuts for post lists (vim style). They act on a "current"
// post and work by clicking the row's own controls, so every path through
// Blazor is the same one a mouse takes. The current row is marked with a
// data-current attribute rather than a class: Blazor rewrites the row's class
// whenever its read state changes, and the list window re-creates rows it
// restores, so current() puts the mark back on whichever element holds the id.
//   j / k        next / previous post
//   l, o, Enter  open in the reader
//   h            close the reader
//   O            open the original
//   m            toggle read
//   s            toggle saved
//   ?            show these
window.rssApp.keys = {
    _currentId: null,

    rows: function () {
        return Array.prototype.filter.call(
            document.querySelectorAll('#post-table .row[data-post-id]'),
            function (row) { return !row.hidden; });
    },

    current: function () {
        var id = window.rssApp.keys._currentId;
        if (!id) return null;
        var row = document.querySelector('#post-table .row[data-post-id="' + id + '"]');
        if (!row || row.hidden) return null;
        row.setAttribute('data-current', '');
        return row;
    },

    select: function (row, block) {
        var keys = window.rssApp.keys;
        var previous = keys.current();
        if (previous) previous.removeAttribute('data-current');
        keys._currentId = row.getAttribute('data-post-id');
        row.setAttribute('data-current', '');
        row.scrollIntoView({ behavior: 'instant', block: block || 'nearest' });
    },

    // The first row whose top is inside the viewport, below the sticky header.
    firstInView: function (rows) {
        for (var i = 0; i < rows.length; i++) {
            if (rows[i].getBoundingClientRect().bottom > 56) return rows[i];
        }
        return rows[0] || null;
    },

    move: function (step) {
        var keys = window.rssApp.keys;
        var rows = keys.rows();
        if (rows.length === 0) return;
        var current = keys.current();
        if (!current) {
            keys.select(keys.firstInView(rows));
            return;
        }
        var index = rows.indexOf(current) + step;
        if (index >= 0 && index < rows.length) keys.select(rows[index]);
    },

    isOpen: function (row) {
        var next = row.nextElementSibling;
        return !!next && next.classList.contains('postContent');
    },

    setOpen: function (open) {
        var keys = window.rssApp.keys;
        var row = keys.current();
        if (!row) {
            var rows = keys.rows();
            if (rows.length === 0) return;
            row = keys.firstInView(rows);
            keys.select(row);
        }
        if (keys.isOpen(row) === open) return;
        if (open) row.scrollIntoView({ behavior: 'instant', block: 'start' });
        row.click();
    },

    clickInCurrent: function (selector) {
        var row = window.rssApp.keys.current();
        var target = row && row.querySelector(selector);
        if (target) target.click();
    },

    help: function () {
        var dialog = document.getElementById('kb-help');
        if (!dialog) return;
        if (dialog.open) dialog.close(); else dialog.showModal();
    },

    onKeyDown: function (e) {
        if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) return;
        var target = e.target;
        if (target && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName))) return;
        if (!document.getElementById('post-table')) return;

        var keys = window.rssApp.keys;
        var dialog = document.getElementById('kb-help');
        if (dialog && dialog.open) {
            if (e.key === '?') { dialog.close(); e.preventDefault(); }
            return;
        }

        switch (e.key) {
            case 'j': keys.move(1); break;
            case 'k': keys.move(-1); break;
            case 'l':
            case 'o': keys.setOpen(true); break;
            case 'Enter':
                // Leave Enter on a focused link or button to the browser.
                if (target && target.closest && target.closest('a, button')) return;
                keys.setOpen(true);
                break;
            case 'h': keys.setOpen(false); break;
            case 'O': keys.clickInCurrent('a.post-external'); break;
            case 'm': keys.clickInCurrent('[data-action="toggle-read"]'); break;
            case 's': keys.clickInCurrent('[data-action="toggle-save"]'); break;
            case '?': keys.help(); break;
            default: return;
        }
        e.preventDefault();
    }
};
document.addEventListener('keydown', window.rssApp.keys.onKeyDown);

// Back/forward-cache restore: the page comes back exactly as it was frozen,
// without reloading, so nothing would otherwise replay the outbox or refresh
// the read flags on the rows.
window.addEventListener('pageshow', function(e) {
    if (!e.persisted) return;
    var ref = window.rssApp._pageRestoredRef;
    if (!ref) return;
    try { ref.invokeMethodAsync('OnPageRestoredAsync').catch(function() {}); } catch (err) {}
});