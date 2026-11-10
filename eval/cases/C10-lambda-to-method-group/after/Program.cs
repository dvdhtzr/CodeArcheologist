namespace Change10;

public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", HealthReport.Create);
    }
}

public static class HealthReport
{
    public static string Create() => "ok";
}
