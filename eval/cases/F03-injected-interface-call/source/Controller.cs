namespace Fixture03;

public interface IPricing
{
    decimal Quote(string sku);
}

[Route("api/prices")]
public class PricesController
{
    private readonly IPricing _pricing;
    public PricesController(IPricing pricing) { _pricing = pricing; }

    [HttpGet("{sku}")]
    public decimal Get(string sku)
    {
        return _pricing.Quote(sku);
    }
}
