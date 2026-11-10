namespace Fixture06;

[Route("api/math")]
public class MathController
{
    [HttpGet("square/{n}")]
    public int Square(int n)
    {
        return Multiply(n, n);

        static int Multiply(int a, int b) => a * b;
    }
}
