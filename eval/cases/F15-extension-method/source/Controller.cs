namespace Fixture15;

public static class TextExtensions
{
    public static string Shout(this string value) => value.ToUpperInvariant();
}

[Route("api/echo")]
public class EchoController
{
    [HttpGet("{text}")]
    public string Get(string text)
    {
        return text.Shout();
    }
}
