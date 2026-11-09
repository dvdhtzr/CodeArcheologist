using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeArcheologist.Api.Workspaces;

public sealed record CSharpProvisionalCalleeTarget(
    string Name,
    string FilePath,
    string FileSha256,
    int Line,
    int Column,
    int SpanStart,
    int SpanLength);

public sealed record CSharpProvisionalInvocationBinding(
    string FilePath,
    string FileSha256,
    int SpanStart,
    int SpanLength,
    string Status,
    string Reason,
    CSharpProvisionalCalleeTarget? Target);

public sealed record CSharpProvisionalInvocationReport(
    Guid WorkspaceId,
    Guid RepositoryId,
    Guid SnapshotId,
    string FingerprintSha256,
    string AnalyzerVersion,
    string BindingContext,
    int InvocationCount,
    int ProvisionalCount,
    int UnresolvedCount,
    IReadOnlyList<CSharpProvisionalInvocationBinding> Bindings,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Binds invocation syntax against a synthetic, reference-free compilation
/// of verified snapshot source copies. Positive matches remain provisional:
/// no .csproj, references, compilation configuration or generated code is
/// loaded. No emit, imported analyzers, generators, restore or source
/// execution takes place.
/// </summary>
public sealed class CSharpProvisionalInvocationBinder(SnapshotCaptureService capture)
{
    public const string Version = "roslyn-synthetic-callees-v1";
    private const int MaxCSharpFiles = 250;
    private const long MaxCSharpBytes = 8L * 1024 * 1024;
    private const int MaxInvocations = 4500;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    public CSharpProvisionalInvocationReport Inspect(
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
                "Synthetic callee binding is limited to 250 files / 8 MiB of C# text.", 413);
        }

        var byPath = files.ToDictionary(
            file => file.Path, file => file, StringComparer.Ordinal);
        var trees = new List<SyntaxTree>(files.Length);
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

            trees.Add(CSharpSyntaxTree.ParseText(
                source, options: ParseOptions, path: file.Path,
                cancellationToken: cancellationToken));
        }

        // Deliberately no metadata references: the result cannot be
        // interpreted as actual compiler/project method binding.
        var compilation = CSharpCompilation.Create(
            "CodeArcheologistCallees_" + snapshot.Id.ToString("N"),
            syntaxTrees: trees,
            references: Array.Empty<MetadataReference>(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var bindings = new List<CSharpProvisionalInvocationBinding>();
        foreach (var tree in trees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(cancellationToken);
            var file = byPath[tree.FilePath];

            foreach (var invocation in root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bindings.Count >= MaxInvocations)
                    throw new SnapshotCaptureException(
                        "Synthetic callee binding exceeded the 4500-invocation limit.", 413);

                CSharpProvisionalCalleeTarget? target = null;
                var reason = "No unique captured ordinary-method declaration was bound.";

                // No CandidateSymbols fallback: ambiguous/overloaded or
                // under-referenced bindings must remain explicitly unresolved.
                var binding = model.GetSymbolInfo(invocation, cancellationToken);
                if (binding.Symbol is IMethodSymbol method)
                {
                    if (method.MethodKind != MethodKind.Ordinary ||
                        method.ReducedFrom is not null)
                    {
                        reason = "This is not a directly bound ordinary source method; delegate, local-function, extension and other calls remain unresolved.";
                    }
                    else
                    {
                        var sourceRefs = method.OriginalDefinition.DeclaringSyntaxReferences;
                        if (sourceRefs.Length == 1)
                        {
                            var declaration = sourceRefs[0].GetSyntax(cancellationToken);
                            var targetPath = declaration.SyntaxTree.FilePath;
                            if (declaration is MethodDeclarationSyntax methodDeclaration &&
                                byPath.TryGetValue(targetPath, out var targetFile))
                            {
                                var position = declaration.SyntaxTree.GetLineSpan(
                                    declaration.Span, cancellationToken);
                                target = new CSharpProvisionalCalleeTarget(
                                    methodDeclaration.Identifier.ValueText,
                                    targetPath,
                                    targetFile.Sha256,
                                    position.StartLinePosition.Line + 1,
                                    position.StartLinePosition.Character + 1,
                                    declaration.SpanStart,
                                    declaration.Span.Length);
                                reason = "Roslyn selected one captured ordinary-method declaration in a reference-free synthetic compilation. Real project references, compiler inputs and dispatch remain unverified.";
                            }
                            else
                            {
                                reason = "The chosen symbol is not one ordinary method declaration in the captured source set.";
                            }
                        }
                        else if (sourceRefs.Length > 1)
                        {
                            reason = "The chosen method has multiple source declarations; no target is selected.";
                        }
                        else
                        {
                            reason = "The chosen method has no declaration in the captured C# files.";
                        }
                    }
                }
                else if (binding.CandidateSymbols.Length > 0)
                {
                    reason = "Roslyn found multiple or otherwise unusable candidates in the reference-free compilation; guessing a method target is prohibited.";
                }

                bindings.Add(new CSharpProvisionalInvocationBinding(
                    file.Path, file.Sha256,
                    invocation.SpanStart, invocation.Span.Length,
                    target is null ? "unresolved" : "provisional-local",
                    reason, target));
            }
        }

        return new CSharpProvisionalInvocationReport(
            snapshot.WorkspaceId,
            snapshot.RepositoryId,
            snapshot.Id,
            snapshot.FingerprintSha256,
            Version,
            "No .csproj or metadata references; synthetic source-only Roslyn compilation",
            bindings.Count,
            bindings.Count(item => item.Target is not null),
            bindings.Count(item => item.Target is null),
            bindings,
            new[]
            {
                "A matched method is provisional-local: reference-free synthetic binding is not the real compiler's project-aware call graph.",
                "Ambiguous, unsupported, external, delegate, local-function, reduced-extension and missing-source targets are unresolved; no guessed fallback.",
                "The binder does not evaluate .csproj, build, restore, emit, load imported analyzers or generators, or execute captured code.",
                "Virtual/interface dispatch, dynamic calls, reflection, implicit calls, conditional compilation, aliases and overload behavior cannot be certified.",
                "Source target locations are captured file SHA-256 and syntax spans, not durable cross-revision symbol identities."
            });
    }
}
