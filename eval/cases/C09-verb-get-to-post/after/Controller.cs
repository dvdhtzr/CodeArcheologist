namespace Change09;

[Route("api/orders")]
public class OrdersController
{
    [HttpPost("{id}")]
    public string Get(int id)
    {
        return Format(id);
    }

    private string Format(int id) => "order-" + id;
}
