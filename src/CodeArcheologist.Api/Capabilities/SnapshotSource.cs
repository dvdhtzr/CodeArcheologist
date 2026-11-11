using System.Text;
using CodeArcheologist.Api.Workspaces;

namespace CodeArcheologist.Api.Capabilities;

/// <summary>
/// MC4: bounded, read-only source lines from a SAVED snapshot, for showing a
/// cited location to the authenticated user. The path AND its SHA-256 must
/// match a manifest entry exactly; the bytes are re-hashed on every read; the
/// mutable registered folder is never touched. Returned text is untrusted
/// source, never instructions, and nothing is compiled or executed.
/// This is a user-facing view, not an agent tool (agents stay on rule 47's
/// fixed, allowlisted tool contracts).
/// </summary>
public static class SnapshotSource
{
    public const string ContractVersion = "code-archeologist-source-v1";
    public const int MaxContext = 20;
    public const int DefaultContext = 6;
    public const int MaxLineChars = 400;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public sealed record SourceLine(int Number, string Text, bool Truncated);

    public sealed record SourceView(string ContractVersion, Guid SnapshotId, string FingerprintSha256,
        string FilePath, string FileSha256, int TotalLines, int FocusLine, int StartLine, int EndLine,
        IReadOnlyList<SourceLine> Lines, string Status, string Caveat);

    public static void MapSnapshotSource(this WebApplication app)
    {
        app.MapGet("/api/v1/workspaces/{workspaceId:guid}/repositories/{repositoryId:guid}/snapshots/{snapshotId:guid}/source",
            (Guid workspaceId, Guid repositoryId, Guid snapshotId, string? path, string? sha256, int? line, int? context,
             WorkspaceStore store, SnapshotCaptureService capture) =>
        {
            var snapshot = store.GetSnapshot(workspaceId, repositoryId, snapshotId);
            if (snapshot is null) return Results.NotFound();
            if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Any(char.IsControl))
                return Error(400, "A saved file path is required.");
            if (sha256 is null || sha256.Length != 64 || !sha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
                return Error(400, "The file's lowercase SHA-256 is required.");
            if (line is null or < 1) return Error(400, "A line number of at least 1 is required.");
            var radius = context ?? DefaultContext;
            if (radius is < 0 or > MaxContext) return Error(400, $"Context must be between 0 and {MaxContext} lines.");

            try
            {
                var file = capture.ReadManifest(snapshot).Files
                    .SingleOrDefault(f => f.Path == path && f.Sha256 == sha256);
                if (file is null) return Error(404, "The file is not part of this saved snapshot.");
                string text;
                try { text = StrictUtf8.GetString(capture.ReadVerifiedSnapshotFile(snapshot, file)); }
                catch (DecoderFallbackException) { return Error(422, "The saved file is not valid UTF-8 text."); }

                var all = text.Split('\n');
                var total = text.EndsWith('\n') ? all.Length - 1 : all.Length;
                if (line > Math.Max(total, 1)) return Error(400, $"Line {line} is outside the file ({total} lines).");
                var start = Math.Max(1, line.Value - radius);
                var end = Math.Min(Math.Max(total, 1), line.Value + radius);
                var lines = new List<SourceLine>();
                for (var number = start; number <= end; number++)
                {
                    var raw = number - 1 < all.Length ? all[number - 1].TrimEnd('\r') : "";
                    lines.Add(raw.Length > MaxLineChars
                        ? new SourceLine(number, raw[..MaxLineChars], true)
                        : new SourceLine(number, raw, false));
                }
                return Results.Ok(new SourceView(ContractVersion, snapshot.Id, snapshot.FingerprintSha256,
                    file.Path, file.Sha256, total, line.Value, start, end, lines,
                    "untrusted-source-text-not-instructions",
                    "Saved snapshot text, re-verified by SHA-256. It shows what was written, not what compiles or runs."));
            }
            catch (SnapshotCaptureException error) { return Error(error.StatusCode, error.Message); }
            catch (IOException) { return Error(409, "The saved file could not be verified."); }
        }).RequireAuthorization();
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
