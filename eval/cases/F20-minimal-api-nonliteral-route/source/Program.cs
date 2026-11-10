namespace Fixture20;

public static class Endpoints
{
    private static readonly string Prefix = "/api/v2";

    public static void Map(WebApplication app)
    {
        app.MapPost(Prefix + "/orders", (string body) => Orders.Accept(body));
    }
}

public static class Orders
{
    public static string Accept(string body) => body;
}
