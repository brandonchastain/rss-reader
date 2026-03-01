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
window.scrollToTopInstant = function(element) {
    if (element) {
        element.scrollIntoView({ behavior: 'instant', block: 'start' });
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

// Functions to save, get, and clear the last post ID in localStorage
window.rssApp = {
    setLastPostId: function(postId, isFilterUnread, isFilterSaved, filterTags, setDateTime) {
        localStorage.setItem('rssApp.lastPostId', postId);
        localStorage.setItem('rssApp.lastPage', document.title);
        localStorage.setItem('rssApp.isFilterUnread', isFilterUnread);
        localStorage.setItem('rssApp.isFilterSaved', isFilterSaved);
        localStorage.setItem('rssApp.filterTags', filterTags);
        localStorage.setItem('rssApp.lastSet', setDateTime);
    },
    getLastPostId: function() {
        return localStorage.getItem('rssApp.lastPostId');
    },
    getIsFilterUnread: function() {
        return localStorage.getItem('rssApp.isFilterUnread') === 'true';
    },
    getIsFilterSaved: function() {
        return localStorage.getItem('rssApp.isFilterSaved') === 'true';
    },
    getFilterTags: function() {
        return localStorage.getItem('rssApp.filterTags') || '';
    },
    getLastSet: function() {
        return localStorage.getItem('rssApp.lastSet');
    },
    clearData: function() {
        localStorage.removeItem('rssApp.lastPostId');
        localStorage.removeItem('rssApp.isFilterUnread');
        localStorage.removeItem('rssApp.isFilterSaved');
        localStorage.removeItem('rssApp.filterTags');
        localStorage.removeItem('rssApp.lastSet');
    },
    scrollToLastPost: function() {
        const lastPostId = this.getLastPostId();
        const isSamePage = document.title === localStorage.getItem('rssApp.lastPage');
        if (!lastPostId || !isSamePage) {
            return;
        }

        const maxAttempts = 100;
        const scrollStep = 10000; // px
        const delay = 100; // ms
        let attempts = 0;

        function tryScroll() {
            const postElement = document.querySelector(`a[href="${lastPostId}"]`);
            if (postElement) {
                postElement.scrollIntoView({
                    behavior: 'instant', // or 'instant' or 'smooth'
                    block: 'center',
                    inline: 'nearest'
                });
                postElement.parentElement.click();
                return;
            }
            if (attempts < maxAttempts) {
                window.scrollBy(0, scrollStep);
                attempts++;
                setTimeout(tryScroll, delay);
            }
        }
        tryScroll();
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