using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeArcheologist.Api.Workspaces;

public sealed record CSharpInvocationCaller(
    string Kind,
    string Name,
    int SpanStart,
    int SpanLength);

public sealed record CSharpInvocationSite(
    string FilePath,
    string FileSha256,
    string WrittenExpression,
    int ArgumentCount,
    int Line,
    int Column,
    int SpanStart,
    int SpanLength,
    CSharpInvocationCaller? ContainingMethod);

public sealed record CSharpInvocationSyntaxReport(
    Guid WorkspaceId,
    Guid RepositoryId,
    Guid SnapshotId,
    string FingerprintSha256,
    string AnalyzerVersion,
    int FileCount,
    int InvocationCount,
    int MethodContainedCount,
    IReadOnlyList<CSharpInvocationSite> Invocations,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Observes invocation syntax in SHA-256-verified snapshot source. A written
/// invocation is NOT a resolved call target or a runtime call. No project
/// evaluation, compilation, scripts, generators or code execution occurs.
/// </summary>
public sealed class CSharpInvocationSyntaxInspector(SnapshotCaptureService capture)
{
    public const string Version = "roslyn-invocation-sites-v1";
    private const int MaxCSharpFiles = 250;
    private const long MaxCSharpBytes = 8L * 1024 * 1024;
    private const int MaxInvocations = 4500;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly CSharpParseOptions Options =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    public CSharpInvocationSyntaxReport Inspect(
        SnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        var manifest = capture.ReadManifest(snapshot);
        var files = manifest.Files
            .Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        if (files.Length > MaxCSharpFiles ||
            files.Sum(file => file.SizeBytes) > MaxCSharpBytes)
        {
            throw new SnapshotCaptureException(
                "Invocation syntax inspection is limited to 250 files / 8 MiB of C# text.", 413);
        }

        var sites = new List<CSharpInvocationSite>();
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
                    "A saved C# file is not valid UTF-8: " + file.Path, 422);
            }

            var tree = CSharpSyntaxTree.ParseText(
                source, options: Options, path: file.Path,
                cancellationToken: cancellationToken);
            var root = tree.GetRoot(cancellationToken);

            foreach (var invocation in root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sites.Count >= MaxInvocations)
                {
                    throw new SnapshotCaptureException(
                        "Invocation syntax occurrences exceeded the 4500-site limit.", 413);
                }

                // Attribute to a method only when the closest execution
                // context is a normal method or constructor. A lambda,
                // local function, property accessor, field initializer,
                // operator or top-level statement must not be falsely
                // attributed to a more distant enclosing method.
                var nearestScope = invocation.Ancestors().FirstOrDefault(node =>
                    node is MethodDeclarationSyntax or
                        ConstructorDeclarationSyntax or
                        LocalFunctionStatementSyntax or
                        AnonymousFunctionExpressionSyntax or
                        AccessorDeclarationSyntax or
                        PropertyDeclarationSyntax or
                        FieldDeclarationSyntax or
                        OperatorDeclarationSyntax or
                        ConversionOperatorDeclarationSyntax or
                        DestructorDeclarationSyntax or
                        GlobalStatementSyntax);

                CSharpInvocationCaller? caller = nearestScope switch
                {
                    MethodDeclarationSyntax method => new CSharpInvocationCaller(
                        "method", method.Identifier.ValueText,
                        method.SpanStart, method.Span.Length),
                    ConstructorDeclarationSyntax constructor => new CSharpInvocationCaller(
                        "constructor", constructor.Identifier.ValueText,
                        constructor.SpanStart, constructor.Span.Length),
                    _ => null
                };

                var location = tree.GetLineSpan(invocation.Span, cancellationToken);
                var written = invocation.Expression.ToString()
                    .Replace((char)10, ' ').Replace((char)13, ' ').Trim();
                if (written.Length > 160)
                    written = written[..160] + "...";

                sites.Add(new CSharpInvocationSite(
                    file.Path, file.Sha256, written,
                    invocation.ArgumentList.Arguments.Count,
                    location.StartLinePosition.Line + 1,
                    location.StartLinePosition.Character + 1,
                    invocation.SpanStart, invocation.Span.Length,
                    caller));
            }
        }

        return new CSharpInvocationSyntaxReport(
            snapshot.WorkspaceId,
            snapshot.RepositoryId,
            snapshot.Id,
            snapshot.FingerprintSha256,
            Version,
            files.Length,
            sites.Count,
            sites.Count(site => site.ContainingMethod is not null),
            sites,
            new[]
            {
                "An invocation is an observed syntactic expression, not a compiler-resolved call or a runtime execution.",
                "Caller containment is recorded only for direct method/constructor bodies; lambdas, local functions, accessors and initializers remain file-scoped.",
                "The written expression and argument count do not identify an overload, extension method, interface dispatch or dynamic target.",
                "Conditional compilation was parsed without project-specific symbols; inactive branches may be omitted.",
                "No project loading, semantic compilation, imported analyzers, generators, scripts, restore or source execution occurred.",
                "All paths, file SHA-256 hashes and spans are from the verified saved snapshot."
            });
    }
}
