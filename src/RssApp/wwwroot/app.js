
async function getUsername() {
    const response = await fetch('/.auth/me');  
    console.log("sending request...");
    // convert to JSON  
    const json = await response.json();  
    // ensure clientPrincipal and userDetails exist  
    if(json.length > 0 && json[0].user_id) {  
        // return userDetails (the username)  
        console.log(json[0].user_id);
        return json[0].user_id;  
    } else {  
        // return null if anonymous
        return null;  
    }
}