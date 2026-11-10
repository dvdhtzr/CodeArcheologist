namespace Fixture13;

[Route("api/notes")]
public class NotesController
{
    [HttpGet]
    public string List() => Load();

    [HttpPost]
    public string Create(string text)
    {
        return Save(text);
    }

    private string Load() => "[]";
    private string Save(string text) => text;
}
