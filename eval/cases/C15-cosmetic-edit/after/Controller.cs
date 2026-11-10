namespace Change15;

[Route("api/orders")]
public class OrdersController
{
    [HttpGet("{id}")]
    public string Get(int id)
    {
        // Format the order identifier.

        return Format(id);
    }

    private string Format(int id) => "order-" + id;
}
