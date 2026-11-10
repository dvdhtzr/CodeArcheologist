namespace Change06;

public interface IFormatter { string Format(int id); }

[Route("api/orders")]
public class OrdersController
{
    private readonly IFormatter _formatter;
    public OrdersController(IFormatter formatter) { _formatter = formatter; }

    [HttpGet("{id}")]
    public string Get(int id)
    {
        return _formatter.Format(id);
    }
}
