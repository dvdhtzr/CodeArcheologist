namespace Fixture05;

[Route("api/items")]
public class ItemsController
{
    [HttpGet]
    public int Count()
    {
        System.Func<int> deferred = () => Expensive();
        return 1;
    }

    private int Expensive() => 99;
}
