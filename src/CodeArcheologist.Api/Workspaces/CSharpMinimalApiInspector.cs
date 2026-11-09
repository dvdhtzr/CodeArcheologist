using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeArcheologist.Api.Workspaces;

public sealed record CSharpMinimalApiCandidate(
    string HttpVerb,
    string WrittenMemberName,
    string WrittenReceiver,
    string? WrittenRouteLiteral,
    string RouteStatus,
    string HandlerSyntax,
    string FilePath,
    string FileSha256,
    int Line,
    int Column,
    int SpanStart,
    int SpanLength);

public sealed record CSharpMinimalApiReport(
    Guid WorkspaceId,
    Guid RepositoryId,
    Guid SnapshotId,
    string FingerprintSha256,
    string AnalyzerVersion,
    int CSharpFileCount,
    int CandidateCount,
    int NonliteralRouteCount,
    IReadOnlyList<CSharpMinimalApiCandidate> Candidates,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Detects written MapGet/MapPost/etc member invocations in SHA-256-verified
/// saved C# source. This is a syntax candidate inventory, not a verified
/// ASP.NET Minimal API call or runtime route table.
/// </summary>
public sealed class CSharpMinimalApiInspector(SnapshotCaptureService capture)
{
    public const string Version = "roslyn-minimal-api-candidates-v1";
    private const int MaxCSharpFiles = 250;
    private const long MaxCSharpBytes = 8L * 1024 * 1024;
    private const int MaxCandidates = 3000;
    private const int MaxRouteLength = 256;
    private const int MaxReceiverLength = 160;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly CSharpParseOptions Options =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    public CSharpMinimalApiReport Inspect(
        SnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        var manifest = capture.ReadManifest(snapshot);
        var files = manifest.Files
            .Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        if (files.Length > MaxCSharpFiles ||
            files.Sum(file => file.SizeBytes) > MaxCSharpBytes)
            throw new SnapshotCaptureException(
                "Minimal API candidate inspection is limited to 250 C# files / 8 MiB.", 413);

        var candidates = new List<CSharpMinimalApiCandidate>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source;
            try
            {
                source = StrictUtf8.GetString(
                    capture.ReadVerifiedSnapshotFile(snapshot, file));
            }
            catch (DecoderFallbackException)
            {
                throw new SnapshotCaptureException(
                    "Saved C# source is not valid UTF-8: " + file.Path, 422);
            }

            var tree = CSharpSyntaxTree.ParseText(
                source, options: Options, path: file.Path,
                cancellationToken: cancellationToken);
            foreach (var invocation in tree.GetRoot(cancellationToken)
                .DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                    member.Name is not IdentifierNameSyntax name)
                    continue;

                var verb = HttpVerb(name.Identifier.ValueText);
                if (verb is null) continue;

                if (candidates.Count >= MaxCandidates)
                    throw new SnapshotCaptureException(
                        "Minimal API mapping candidates exceeded the 3000-site limit.", 413);

                var (route, routeStatus) = ReadRoute(invocation.ArgumentList);
                var handlerSyntax = ReadHandlerSyntax(invocation.ArgumentList);
                var receiver = member.Expression.ToString().Replace('\r', ' ')
                    .Replace('\n', ' ').Trim();
                if (receiver.Length > MaxReceiverLength)
                    receiver = receiver[..MaxReceiverLength] + "...";

                var location = tree.GetLineSpan(
                    invocation.Span, cancellationToken);
                candidates.Add(new CSharpMinimalApiCandidate(
                    verb,
                    name.Identifier.ValueText,
                    receiver,
                    route,
                    routeStatus,
                    handlerSyntax,
                    file.Path,
                    file.Sha256,
                    location.StartLinePosition.Line + 1,
                    location.StartLinePosition.Character + 1,
                    invocation.SpanStart,
                    invocation.Span.Length));
            }
        }

        return new CSharpMinimalApiReport(
            snapshot.WorkspaceId,
            snapshot.RepositoryId,
            snapshot.Id,
            snapshot.FingerprintSha256,
            Version,
            files.Length,
            candidates.Count,
            candidates.Count(candidate =>
                candidate.RouteStatus != "written-literal"),
            candidates,
            [
                "Recognizes only simple member-access spellings MapGet/MapPost/MapPut/MapPatch/MapDelete. Other mapping APIs such as MapMethods, MapFallback and MapGroup are not analyzed.",
                "Same-named unrelated methods produce candidates; no ASP.NET extension-method binding, builder type or runtime registration was verified.",
                "Only a single positional first-argument string literal up to 256 characters is displayed; constants, interpolation, named routes and missing arguments remain unresolved.",
                "Route groups, controller prefixes, authorization, middleware, handler method symbols and dynamic branch conditions are not resolved. Written route strings are not effective URLs.",
                "Handler syntax classifies lambda, method-group-like or other expressions but never claims the handler's actual target or behavior.",
                "Candidate mapping annotations are joined to existing observed invocation nodes by exact saved file path, SHA-256 and source span.",
                "No imported source, builds, MSBuild evaluation, restore, metadata references, source generators or analyzers were executed.",
                "Default C# parsing lacks project-specific conditional symbols; inactive branches and generated mapping code may be missing."
            ]);
    }

    private static string? HttpVerb(string member) => member switch
    {
        "MapGet" => "GET",
        "MapPost" => "POST",
        "MapPut" => "PUT",
        "MapPatch" => "PATCH",
        "MapDelete" => "DELETE",
        _ => null
    };

    private static (string? Literal, string Status) ReadRoute(
        ArgumentListSyntax arguments)
    {
        if (arguments.Arguments.Count == 0)
            return (null, "missing-argument");

        var first = arguments.Arguments[0];
        if (first.NameColon is not null ||
            first.RefKindKeyword != default)
            return (null, "nonliteral-or-unsupported");

        if (first.Expression is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.StringLiteralExpression) &&
            literal.Token.ValueText.Length <= MaxRouteLength)
            return (literal.Token.ValueText, "written-literal");

        return (null, "nonliteral-or-unsupported");
    }

    private static string ReadHandlerSyntax(ArgumentListSyntax arguments)
    {
        if (arguments.Arguments.Count < 2) return "missing-or-unknown";
        return arguments.Arguments[1].Expression switch
        {
            SimpleLambdaExpressionSyntax or
                ParenthesizedLambdaExpressionSyntax => "lambda-expression",
            AnonymousMethodExpressionSyntax => "anonymous-method",
            IdentifierNameSyntax or
                MemberAccessExpressionSyntax => "method-group-like-expression",
            _ => "other-unresolved-expression"
        };
    }
}
