namespace Fixture18;

[Route("api/slow")]
public class SlowController
{
    [HttpGet]
    public async System.Threading.Tasks.Task<string> Get()
    {
        return await FetchAsync();
    }

    private async System.Threading.Tasks.Task<string> FetchAsync()
    {
        await System.Threading.Tasks.Task.Yield();
        return "done";
    }
}
