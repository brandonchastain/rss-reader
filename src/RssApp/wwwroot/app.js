
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