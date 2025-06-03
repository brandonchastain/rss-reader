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

window.Observer = {
    observer: null,
    Initialize: function (component, observerTargetId) {
        this.observer = new IntersectionObserver(e => {
            // Check here
            if (e[0].isIntersecting) {
                component.invokeMethodAsync('OnIntersection');
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
        if (!lastPostId) return;

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