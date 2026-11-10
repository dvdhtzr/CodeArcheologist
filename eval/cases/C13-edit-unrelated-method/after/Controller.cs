namespace Change13;

[Route("api/orders")]
public class OrdersController
{
    [HttpGet("{id}")]
    public string Get(int id)
    {
        return Format(id);
    }

    private string Format(int id) => "order-" + id;

    private int Unrelated() => 2 + 3;
}
