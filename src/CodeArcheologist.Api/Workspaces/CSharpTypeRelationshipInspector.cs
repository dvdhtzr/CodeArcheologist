using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeArcheologist.Api.Workspaces;

public sealed record CSharpTypeTarget(
    string Name,
    string Namespace,
    string FilePath,
    string FileSha256,
    int Line,
    int Column,
    int SpanStart,
    int SpanLength);

public sealed record CSharpTypeUsage(
    string UsageKind,
    string WrittenType,
    string Status,
    string Reason,
    string FilePath,
    string FileSha256,
    int Line,
    int Column,
    int SpanStart,
    int SpanLength,
    CSharpTypeTarget? Target);

public sealed record CSharpTypeRelationshipReport(
    Guid WorkspaceId,
    Guid RepositoryId,
    Guid SnapshotId,
    string FingerprintSha256,
    string AnalyzerVersion,
    string BindingContext,
    int CSharpFileCount,
    int SyntaxErrorCount,
    int ProvisionalLinkCount,
    int UnresolvedCount,
    IReadOnlyList<CSharpTypeUsage> Usages,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Attempts source-local type bindings from a synthetic Roslyn compilation
/// assembled only from immutable, hash-verified snapshot files. This does NOT
/// load/evaluate projects, restore packages, emit assemblies, run imported
/// analyzers, source generators, build targets or execute source code.
/// 
/// Binding across project boundaries is NOT project-verified: every matching
/// link is deliberately classified "provisional-local".
/// </summary>
public sealed class CSharpTypeRelationshipInspector(SnapshotCaptureService capture)
{
    public const string Version = "roslyn-synthetic-type-links-v1";
    private const int MaxCSharpFiles = 250;
    private const long MaxCSharpBytes = 8L * 1024 * 1024;
    private const int MaxUsages = 6000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly CSharpParseOptions Options =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    public CSharpTypeRelationshipReport Inspect(
        SnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        // Revalidate the manifest hash and every copied artifact, including
        // non-C# files, before trusting snapshot identity.
        var manifest = capture.ReadManifest(snapshot);
        var sourceFiles = manifest.Files
            .Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        if (sourceFiles.Length > MaxCSharpFiles ||
            sourceFiles.Sum(file => file.SizeBytes) > MaxCSharpBytes)
        {
            throw new SnapshotCaptureException(
                "Initial C# relationship analysis is limited to 250 files / 8 MiB of C# text.", 413);
        }

        var sourceByPath = sourceFiles.ToDictionary(
            file => file.Path, file => file, StringComparer.Ordinal);
        var trees = new List<SyntaxTree>(sourceFiles.Length);
        var syntaxErrors = 0;

        foreach (var file in sourceFiles)
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

            var tree = CSharpSyntaxTree.ParseText(source,
                options: Options, path: file.Path,
                cancellationToken: cancellationToken);
            trees.Add(tree);
            syntaxErrors += tree.GetDiagnostics(cancellationToken)
                .Count(issue => issue.Severity == DiagnosticSeverity.Error);
        }

        // This is an in-memory binding approximation, not a project build.
        // There are deliberately NO reference assemblies, loaded plugins,
        // MSBuild project evaluations, restores, source generators or emit.
        var compilation = CSharpCompilation.Create(
            "CodeArcheologistSnapshot_" + snapshot.Id.ToString("N"),
            syntaxTrees: trees,
            references: Array.Empty<MetadataReference>(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var usages = new List<CSharpTypeUsage>();
        foreach (var tree in trees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(cancellationToken);
            var candidates = new List<(TypeSyntax Type, string UsageKind)>();

            foreach (var node in root.DescendantNodes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node is BaseTypeSyntax baseType)
                    candidates.Add((baseType.Type, "base-type"));
                else if (node is ObjectCreationExpressionSyntax creation)
                    candidates.Add((creation.Type, "object-creation"));
            }

            var file = sourceByPath[tree.FilePath];
            foreach (var (type, kind) in candidates.OrderBy(
                candidate => candidate.Type.SpanStart))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (usages.Count >= MaxUsages)
                    throw new SnapshotCaptureException(
                        "C# type references exceeded the bounded inspection limit.", 413);

                var location = tree.GetLineSpan(type.Span, cancellationToken);
                var bound = model.GetTypeInfo(type, cancellationToken).Type;
                CSharpTypeTarget? target = null;
                var reason = "No unique source declaration was bound inside the synthetic compilation.";

                if (bound is INamedTypeSymbol named && named.TypeKind != TypeKind.Error)
                {
                    var sourceDeclarations = named.OriginalDefinition.DeclaringSyntaxReferences;
                    if (sourceDeclarations.Length == 1)
                    {
                        var syntax = sourceDeclarations[0].GetSyntax(cancellationToken);
                        var targetPath = syntax.SyntaxTree.FilePath;
                        if (sourceByPath.TryGetValue(targetPath, out var targetFile))
                        {
                            var targetLine = syntax.SyntaxTree.GetLineSpan(
                                syntax.Span, cancellationToken);
                            target = new CSharpTypeTarget(
                                named.Name,
                                named.ContainingNamespace is { IsGlobalNamespace: false } ns
                                    ? ns.ToDisplayString() : "",
                                targetPath,
                                targetFile.Sha256,
                                targetLine.StartLinePosition.Line + 1,
                                targetLine.StartLinePosition.Character + 1,
                                syntax.SpanStart,
                                syntax.Span.Length);
                            reason = "Roslyn bound to a single declaration in the synthetic snapshot compilation; actual project membership and compilation remain unverified.";
                        }
                    }
                    else if (sourceDeclarations.Length > 1)
                    {
                        reason = "The symbol has multiple source declarations (for example, partial types); this first pass does not choose a target.";
                    }
                    else
                    {
                        reason = "No declaration for this type is present among the captured C# files.";
                    }
                }

                usages.Add(new CSharpTypeUsage(
                    kind,
                    type.ToString(),
                    target is null ? "unresolved" : "provisional-local",
                    reason,
                    file.Path,
                    file.Sha256,
                    location.StartLinePosition.Line + 1,
                    location.StartLinePosition.Character + 1,
                    type.SpanStart,
                    type.Span.Length,
                    target));
            }
        }

        return new CSharpTypeRelationshipReport(
            snapshot.WorkspaceId,
            snapshot.RepositoryId,
            snapshot.Id,
            snapshot.FingerprintSha256,
            Version,
            "Reference-free synthetic compilation over captured .cs files; not project-evaluated",
            sourceFiles.Length,
            syntaxErrors,
            usages.Count(usage => usage.Status == "provisional-local"),
            usages.Count(usage => usage.Status == "unresolved"),
            usages,
            new[]
            {
                "Every provisional link is a candidate within a synthetic compilation, NOT a verified project-level dependency.",
                "The analysis does not load .csproj, restore packages, bind external assemblies or establish real compiler configuration.",
                "Only base-type clauses and explicit object creation type names are inspected; calls, DI and other type uses are omitted.",
                "Partial types, generated sources, preprocessor symbols, aliases and missing files may prevent or invalidate bindings.",
                "Syntax errors and missing references may produce unresolved or misleading candidate results.",
                "Evidence paths, hashes and spans belong to the saved snapshot, not the current filesystem working tree."
            });
    }
}
