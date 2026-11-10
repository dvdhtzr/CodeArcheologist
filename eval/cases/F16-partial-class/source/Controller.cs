namespace Fixture16;

[Route("api/split")]
public partial class SplitController
{
    [HttpGet]
    public string Get()
    {
        return Helper();
    }
}
