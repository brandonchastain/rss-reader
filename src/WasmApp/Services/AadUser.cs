namespace WasmApp.Services;

public class AadUser
{
    public ClientPrincipal ClientPrincipal { get; set; }
}

public class ClientPrincipal
{
    public string IdentityProvider { get; set; }
    public string UserId { get; set; }
    public string UserDetails { get; set; }

    // This is a list of roles the user has, e.g., "anonymous", "authenticated"
    public IEnumerable<string> UserRoles { get; set; }
}

public class UserAuthenticationState
{
     public ClientPrincipal ClientPrincipal { get; set; }
}