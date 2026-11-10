namespace Fixture17;

public abstract class ApiBase
{
    protected string Envelope(string body) => "{" + body + "}";
}

[Route("api/status")]
public class StatusController : ApiBase
{
    [HttpGet]
    public string Get()
    {
        return Envelope("ok");
    }
}
