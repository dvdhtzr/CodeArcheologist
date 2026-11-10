namespace Change05;

[Route("api/orders")]
public partial class OrdersController
{
    [HttpGet("{id}")]
    public string Get(int id)
    {
        return Format(id);
    }
}
