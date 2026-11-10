namespace Change11;

public interface IClock { string Now(); }
public sealed class SystemClock : IClock { public string Now() => "now"; }

[Route("api/time")]
public class TimeController
{
    private readonly IClock _clock;
    public TimeController(IClock clock) { _clock = clock; }

    [HttpGet]
    public string Get() => _clock.Now();
}
