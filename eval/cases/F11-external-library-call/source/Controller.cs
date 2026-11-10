namespace Fixture11;

[Route("api/files")]
public class FilesController
{
    [HttpGet("{name}")]
    public string Read(string name)
    {
        return System.IO.Path.GetFileName(name);
    }
}
