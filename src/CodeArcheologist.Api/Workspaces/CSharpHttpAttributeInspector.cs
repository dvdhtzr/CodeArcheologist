using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeArcheologist.Api.Workspaces;

public sealed record CSharpHttpAttributeCandidate(
    string HttpVerb,
    string WrittenAttribute,
    string MethodName,
    string FilePath,
    string FileSha256,
    int Line,
    int Column,
    int AttributeSpanStart,
    int AttributeSpanLength,
    int MethodSpanStart,
    int MethodSpanLength,
    string? AttributeRouteLiteral,
    string AttributeRouteStatus,
    string? MethodRouteLiteral,
    string MethodRouteStatus,
    string? TypeRouteLiteral,
    string TypeRouteStatus);

public sealed record CSharpHttpAttributeReport(
    Guid WorkspaceId,
    Guid RepositoryId,
    Guid SnapshotId,
    string FingerprintSha256,
    string AnalyzerVersion,
    int CSharpFileCount,
    int CandidateCount,
    int NonliteralAttributeRouteCount,
    IReadOnlyList<CSharpHttpAttributeCandidate> Candidates,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Extracts conventional ASP.NET HTTP attribute *spellings* from saved,
/// SHA-256-verified C# syntax. Never binds actual attribute types or claims
/// effective routes. No imported code, projects, analyzers or generators run.
/// </summary>
public sealed class CSharpHttpAttributeInspector(SnapshotCaptureService capture)
{
    public const string Version = "roslyn-http-attribute-candidates-v1";
    private const int MaxCSharpFiles = 250;
    private const long MaxCSharpBytes = 8L * 1024 * 1024;
    private const int MaxCandidates = 3000;
    private const int MaxRouteLength = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly CSharpParseOptions Options =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    public CSharpHttpAttributeReport Inspect(
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
                "HTTP attribute inspection is limited to 250 C# files / 8 MiB.", 413);

        var candidates = new List<CSharpHttpAttributeCandidate>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = capture.ReadVerifiedSnapshotFile(snapshot, file);
            string source;
            try
            {
                source = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new SnapshotCaptureException(
                    "Saved C# file has invalid UTF-8: " + file.Path, 422);
            }

            var tree = CSharpSyntaxTree.ParseText(
                source, options: Options, path: file.Path,
                cancellationToken: cancellationToken);
            var root = tree.GetRoot(cancellationToken);
            foreach (var method in root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var type = method.Parent as BaseTypeDeclarationSyntax;
                var (typeRoute, typeRouteStatus) = type is null
                    ? (null, "absent")
                    : ReadRoute(type.AttributeLists);
                var (methodRoute, methodRouteStatus) =
                    ReadRoute(method.AttributeLists);

                foreach (var attribute in method.AttributeLists
                    .SelectMany(list => list.Attributes))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var verb = HttpVerb(attribute);
                    if (verb is null) continue;

                    if (candidates.Count >= MaxCandidates)
                        throw new SnapshotCaptureException(
                            "HTTP attribute candidates exceeded the 3000-occurrence limit.", 413);

                    var (attributeRoute, attributeRouteStatus) =
                        RouteArgument(attribute);
                    var location = tree.GetLineSpan(
                        attribute.Span, cancellationToken);

                    candidates.Add(new CSharpHttpAttributeCandidate(
                        verb,
                        attribute.Name.ToString(),
                        method.Identifier.ValueText,
                        file.Path,
                        file.Sha256,
                        location.StartLinePosition.Line + 1,
                        location.StartLinePosition.Character + 1,
                        attribute.SpanStart,
                        attribute.Span.Length,
                        method.SpanStart,
                        method.Span.Length,
                        attributeRoute,
                        attributeRouteStatus,
                        methodRoute,
                        methodRouteStatus,
                        typeRoute,
                        typeRouteStatus));
                }
            }
        }

        return new CSharpHttpAttributeReport(
            snapshot.WorkspaceId,
            snapshot.RepositoryId,
            snapshot.Id,
            snapshot.FingerprintSha256,
            Version,
            files.Length,
            candidates.Count,
            candidates.Count(c => c.AttributeRouteStatus == "nonliteral-or-unsupported"),
            candidates,
            new[]
            {
                "HTTP annotations are conventional *written attribute names*, not Roslyn-bound ASP.NET symbols or verified registered endpoints.",
                "Only simple, unqualified HttpGet/HttpPost/HttpPut/HttpPatch/HttpDelete attribute names (with optional Attribute suffix) are included.",
                "Controller/type and method Route arguments are shown separately, never combined or expanded into an effective URL.",
                "Only a single positional ordinary string literal is captured; nameof, constants, interpolation, aliases, qualified names and named arguments are unsupported.",
                "Absent or nonliteral routes, token placeholders such as [controller], route constraints and conventions remain unresolved.",
                "Minimal API MapGet/MapPost, MapGroup, controllers registered at runtime, convention routing, external clients and effective HTTP behavior are not analyzed.",
                "The default C# parse options have no project-specific preprocessor symbols; conditional code may be omitted.",
                "No imported project evaluation, MSBuild, source execution, restore, analyzers, generators or metadata references occur.",
                "All source locations and hashes refer only to verified saved snapshot files."
            });
    }

    private static string? HttpVerb(AttributeSyntax attribute) =>
        attribute.Name is IdentifierNameSyntax id
            ? id.Identifier.ValueText switch
            {
                "HttpGet" or "HttpGetAttribute" => "GET",
                "HttpPost" or "HttpPostAttribute" => "POST",
                "HttpPut" or "HttpPutAttribute" => "PUT",
                "HttpPatch" or "HttpPatchAttribute" => "PATCH",
                "HttpDelete" or "HttpDeleteAttribute" => "DELETE",
                _ => null
            }
            : null;

    private static (string? Literal, string Status) ReadRoute(
        SyntaxList<AttributeListSyntax> attributeLists)
    {
        var routes = attributeLists
            .SelectMany(list => list.Attributes)
            .Where(attribute => attribute.Name is IdentifierNameSyntax id &&
                id.Identifier.ValueText is "Route" or "RouteAttribute")
            .ToArray();
        return routes.Length switch
        {
            0 => (null, "absent"),
            1 => RouteArgument(routes[0]),
            _ => (null, "multiple-unresolved")
        };
    }

    private static (string? Literal, string Status) RouteArgument(
        AttributeSyntax attribute)
    {
        var arguments = attribute.ArgumentList?.Arguments;
        if (arguments is null || arguments.Value.Count == 0)
            return (null, "omitted");

        // Named args might be Name, Order, etc. Only exactly one literal,
        // positional route string is safe to report as *written* text.
        var positional = arguments.Value.Where(
            item => item.NameEquals is null && item.NameColon is null).ToArray();
        if (positional.Length != 1)
            return (null, "nonliteral-or-unsupported");

        if (positional[0].Expression is LiteralExpressionSyntax value &&
            value.IsKind(SyntaxKind.StringLiteralExpression) &&
            value.Token.ValueText.Length <= MaxRouteLength)
            return (value.Token.ValueText, "written-literal");

        return (null, "nonliteral-or-unsupported");
    }
}
