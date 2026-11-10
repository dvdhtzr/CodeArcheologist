namespace Fixture07;

public static class Routes
{
    public const string Users = "api/users";
}

[Route(Routes.Users)]
public class UsersController
{
    [HttpGet(Routes.Users + "/{id}")]
    public string Get(string id)
    {
        return Lookup(id);
    }

    private string Lookup(string id) => id;
}
