namespace Change03;

[Route("api/orders")]
public class OrdersController
{
    [HttpGet("{id}")]
    public string Get(int id)
    {
        return Render(id);
    }

    private string Render(int id) => "order-" + id;
}
