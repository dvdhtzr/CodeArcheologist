namespace Fixture14;

[Route("api/fib")]
public class FibController
{
    [HttpGet("{n}")]
    public int Get(int n)
    {
        return Fib(n);
    }

    private int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);
}
