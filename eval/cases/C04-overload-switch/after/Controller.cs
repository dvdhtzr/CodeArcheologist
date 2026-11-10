namespace Change04;

[Route("api/orders")]
public class OrdersController
{
    [HttpGet("{id}")]
    public string Get(int id)
    {
        return Render(id.ToString());
    }

    private string Render(int value) => "int:" + value;
    private string Render(string value) => "text:" + value;
}
