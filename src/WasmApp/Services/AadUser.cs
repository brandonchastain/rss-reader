namespace WasmApp.Services;

public class AadUser
{
    public ClientPrincipal ClientPrincipal { get; set; }
}

public class ClientPrincipal
{
    public string IdentityProvider { get; set; }

    // The account's database id. Also the browser cache's partition key.
    public string UserId { get; set; }

    // This is a list of roles the user has, e.g., "anonymous", "authenticated"
    public IEnumerable<string> UserRoles { get; set; }
}

public class UserAuthenticationState
{
     public ClientPrincipal ClientPrincipal { get; set; }
}
