namespace Fixture04;

[Route("api/report")]
public class ReportController
{
    [HttpGet]
    public string Get()
    {
        return Render(42);
    }

    private string Render(int value) => "int:" + value;
    private string Render(string value) => "text:" + value;
}
