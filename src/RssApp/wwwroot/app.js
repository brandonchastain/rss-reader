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