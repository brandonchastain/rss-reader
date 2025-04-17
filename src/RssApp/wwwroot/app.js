/// Hit the endpoint for Azure App Service authentication.
/// This must be sent from the client side (javascript) to get the authentication cookie.
async function getUsername() {
    const response = await fetch('/.auth/me');
    const json = await response.json();

    var username = null;
    if (json.length > 0 && json[0].user_id) {
        username = json[0].user_id;
        console.log(username);
    }

    return username;
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