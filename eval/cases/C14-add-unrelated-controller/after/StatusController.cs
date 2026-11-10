namespace Change14;

[Route("api/status")]
public class StatusController
{
    [HttpGet]
    public string Get() => "ok";
}
