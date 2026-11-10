namespace Change08;

public static class Routes { public const string ById = "{id}"; }

[Route("api/orders")]
public class OrdersController
{
    [HttpGet(Routes.ById)]
    public string Get(int id)
    {
        return Format(id);
    }

    private string Format(int id) => "order-" + id;
}
