namespace Change01;

[Route("api/orders")]
public class OrdersController
{
    [HttpGet("{id}")]
    public string Get(int id)
    {
        Audit(id);
        return Format(id);
    }

    private string Format(int id) => "order-" + id;

    private void Audit(int id) { }
}
