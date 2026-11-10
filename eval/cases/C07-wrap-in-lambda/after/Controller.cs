namespace Change07;

[Route("api/orders")]
public class OrdersController
{
    [HttpGet("{id}")]
    public string Get(int id)
    {
        System.Func<string> render = () => Format(id);
        return render();
    }

    private string Format(int id) => "order-" + id;
}
