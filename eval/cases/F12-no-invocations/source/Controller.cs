namespace Fixture12;

[Route("api/constant")]
public class ConstantController
{
    [HttpGet]
    public int Get()
    {
        return 7;
    }
}
