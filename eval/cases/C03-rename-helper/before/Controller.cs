namespace Change03;

[Route("api/orders")]
public class OrdersController
{
    [HttpGet("{id}")]
    public string Get(int id)
    {
        return Format(id);
    }

    private string Format(int id) => "order-" + id;
}
