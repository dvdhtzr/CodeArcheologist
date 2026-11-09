using CodeArcheologist.Api.Workspaces;

internal static class GateBFixtureRun
{
    /// <summary>
    /// Isolated synthetic fixture for exercising source syntax diversity.
    /// Not a substitute for real-world repository tests. Uses the normal
    /// capture and verification services but never persists in user app data.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "--gate-b-fixture" ||
            !int.TryParse(args[1], out var attempts) || attempts is < 1 or > 10)
        {
            Console.Error.WriteLine("Usage: --gate-b-fixture <attempts 1..10>");
            return 2;
        }

        var priorDataDir = Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR");
        var temp = Path.Combine(Path.GetTempPath(),
            "code-archeologist-gate-b-fixture-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(temp, "source");
        var data = Path.Combine(temp, "data");
        // Avoid deleting any pre-existing folder even in the unlikely event
        // of an identifier collision.
        Directory.CreateDirectory(temp);
        try
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(data);
            // This source intentionally does not reference any external
            // libraries and is NEVER compiled or executed by Code Archeologist.
            // Recognition is limited to the written HTTP attribute syntax.
            await File.WriteAllTextAsync(Path.Combine(source, "Controller.cs"),
                """
                namespace GateBFixture;
                [Route("api/items")]
                public class ItemsController
                {
                    public void Helper() { }
                    [HttpGet("details/{id}")]
                    public int Details(int id)
                    {
                        Helper();
                        return id;
                    }
                    [HttpPost("create")]
                    public void Create() { Helper(); }
                }
                """);

            // This override is local to the disposable verification process.
            // It neither modifies the normal app's settings nor its database.
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", data);
            var store = new WorkspaceStore();
            store.Initialize();
            var workspace = store.Create("Gate B fixture", Guid.NewGuid());
            if (workspace.Conflict)
                throw new InvalidOperationException("Fixture workspace could not be created.");
            var location = RepositoryPathPolicy.Validate(source, store.DatabasePath);
            if (location.Location is null)
                throw new InvalidOperationException("Fixture registration rejected: " +
                    location.Error);
            var repository = store.RegisterRepository(workspace.Workspace.Id,
                location.Location, Guid.NewGuid());
            if (repository.Status != RepositoryRegistrationStatus.Created ||
                repository.Repository is null)
                throw new InvalidOperationException("Fixture repository registration failed.");
            var capture = new SnapshotCaptureService(store);
            var outcome = await capture.CaptureAsync(workspace.Workspace.Id,
                repository.Repository.Id, Guid.NewGuid(), CancellationToken.None);
            if (!outcome.Created)
                throw new InvalidOperationException("Fixture capture was not fresh.");

            Console.WriteLine("Disposable attribute-based fixture captured and verified.");
            Console.WriteLine("No existing application workspace or snapshot is used.");
            return await GateBLocalRun.RunAsync(
                ["--gate-b-run", attempts.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                 "http-endpoint-attribute"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", priorDataDir);
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                Console.Error.WriteLine(
                    "WARNING: temporary fixture cleanup failed; check system temp directory.");
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    "WARNING: temporary fixture cleanup denied; check system temp directory.");
            }
        }
    }
}
