using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeArcheologist.Api.Investigations;

namespace CodeArcheologist.EvaluationLab;

/// <summary>
/// Observed identity of the actual executable and local model used by a PR4 run.
/// Read-only probes; never calls the model, changes its options or touches curated Git.
/// The run metadata and its seal preserve the observation, not an authentication claim.
/// </summary>
public sealed record R3Pr4ExecutionIdentity(
    string ModelTag, string ModelDigest, string OllamaVersion,
    string GitCommit, string DotnetRuntime, string OperatingSystem,
    string ProcessArchitecture, Guid LabAssemblyMvid, Guid ApiAssemblyMvid)
{
    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Hex40 = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

    public bool Valid =>
        ModelTag == GateC3IndependentRunner.ModelId &&
        Hex64.IsMatch(ModelDigest) && Hex40.IsMatch(GitCommit) &&
        !string.IsNullOrWhiteSpace(OllamaVersion) &&
        !string.IsNullOrWhiteSpace(DotnetRuntime) &&
        !string.IsNullOrWhiteSpace(OperatingSystem) &&
        !string.IsNullOrWhiteSpace(ProcessArchitecture) &&
        LabAssemblyMvid != Guid.Empty && ApiAssemblyMvid != Guid.Empty;

    public static async Task<R3Pr4ExecutionIdentity> CaptureAsync(
        string repositoryRoot, bool requireClean, CancellationToken ct = default)
    {
        // Git operations apply ONLY to the trusted Code Archeologist code checkout.
        // The 15 curated source repositories are neither built nor interrogated here.
        var commit = (await GitAsync(repositoryRoot, ct, "rev-parse", "HEAD")).Trim();
        if (requireClean &&
            !string.IsNullOrWhiteSpace(await GitAsync(repositoryRoot, ct, "status", "--porcelain")))
            throw new InvalidOperationException(
                "Scored PR4 requires a clean Code Archeologist checkout; commit alone does not identify dirty code.");

        using var http = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromSeconds(12)
        };
        using var version = JsonDocument.Parse(await http.GetStringAsync("api/version", ct));
        using var tags = JsonDocument.Parse(await http.GetStringAsync("api/tags", ct));
        var model = tags.RootElement.GetProperty("models").EnumerateArray()
            .Where(x => x.GetProperty("name").GetString() == GateC3IndependentRunner.ModelId)
            .ToArray();
        if (model.Length != 1)
            throw new InvalidOperationException(
                "Ollama does not expose exactly one installed " + GateC3IndependentRunner.ModelId + " model.");
        var identity = new R3Pr4ExecutionIdentity(
            GateC3IndependentRunner.ModelId,
            (model[0].GetProperty("digest").GetString() ?? "").ToLowerInvariant(),
            version.RootElement.GetProperty("version").GetString() ?? "",
            commit.ToLowerInvariant(),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            typeof(R3Pr4ExecutionIdentity).Assembly.ManifestModule.ModuleVersionId,
            typeof(GateC3IndependentRunner).Assembly.ManifestModule.ModuleVersionId);
        if (!identity.Valid)
            throw new InvalidOperationException("Incomplete PR4 local model/executable identity.");
        return identity;
    }

    private static async Task<string> GitAsync(string root, CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Cannot inspect trusted repository Git revision.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("PR4 Git identity probe failed: " + error.Trim());
        return output;
    }
}
