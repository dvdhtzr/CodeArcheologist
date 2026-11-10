namespace Fixture08;

public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/version", Handlers.Version);
    }
}

public static class Handlers
{
    public static string Version() => "1.0";
}
