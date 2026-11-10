namespace Fixture19;

[Route("api/slug")]
public class SlugController
{
    [HttpGet("{title}")]
    public string Get(string title)
    {
        return Slugs.Make(title);
    }
}
