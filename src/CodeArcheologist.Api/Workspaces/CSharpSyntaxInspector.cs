using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeArcheologist.Api.Workspaces;

public sealed record CSharpDeclaration(
    string Kind,
    string Name,
    string ContainingNamespace,
    string? ContainingType,
    string FilePath,
    string FileSha256,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    int SpanStart,
    int SpanLength,
    int? ParameterCount);

public sealed record CSharpParseIssue(
    string FilePath,
    string FileSha256,
    string Code,
    string Message,
    int Line,
    int Column,
    string Severity);

public sealed record CSharpSyntaxReport(
    Guid WorkspaceId,
    Guid RepositoryId,
    Guid SnapshotId,
    string FingerprintSha256,
    string AnalyzerVersion,
    string ExtractionMethod,
    string ParseConfiguration,
    int CSharpFileCount,
    int SyntaxErrorCount,
    bool HasSyntaxErrors,
    IReadOnlyList<CSharpDeclaration> Declarations,
    IReadOnlyList<CSharpParseIssue> Diagnostics,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Syntax-only Roslyn parser over copied, SHA-256 verified snapshot bytes.
/// ParseText/GetRoot never execute project tasks, referenced assemblies,
/// analyzers, source generators, scripts or package restore operations.
/// </summary>
public sealed class CSharpSyntaxInspector(SnapshotCaptureService capture)
{
    public const string AnalyzerVersion = "roslyn-csharp-syntax-v1";
    private const int MaxDeclarations = 25000;
    private const int MaxDiagnostics = 1000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    public CSharpSyntaxReport Inspect(SnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        // Existing manifest verification checks every saved file and the
        // original manifest digest before declaring the snapshot usable.
        var manifest = capture.ReadManifest(snapshot);
        var csFiles = manifest.Files
            .Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        var declarations = new List<CSharpDeclaration>();
        var diagnostics = new List<CSharpParseIssue>();
        var syntaxErrorCount = 0;

        foreach (var file in csFiles)
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
                source, options: ParseOptions, path: file.Path,
                cancellationToken: cancellationToken);

            foreach (var diagnostic in tree.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                    syntaxErrorCount++;
                if (diagnostics.Count >= MaxDiagnostics)
                    throw new SnapshotCaptureException(
                        "C# syntax diagnostics exceeded the bounded inspection limit.", 413);

                var span = diagnostic.Location.IsInSource
                    ? diagnostic.Location.GetLineSpan()
                    : default;
                diagnostics.Add(new CSharpParseIssue(
                    file.Path, file.Sha256, diagnostic.Id,
                    diagnostic.GetMessage(CultureInfo.InvariantCulture),
                    diagnostic.Location.IsInSource ? span.StartLinePosition.Line + 1 : 0,
                    diagnostic.Location.IsInSource ? span.StartLinePosition.Character + 1 : 0,
                    diagnostic.Severity.ToString()));
            }

            var root = tree.GetRoot(cancellationToken);
            foreach (var node in root.DescendantNodesAndSelf())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = Identify(node);
                if (identity is null) continue;

                if (declarations.Count >= MaxDeclarations)
                    throw new SnapshotCaptureException(
                        "C# declarations exceeded the bounded inspection limit.", 413);

                var location = tree.GetLineSpan(node.Span, cancellationToken);
                var containingNamespace = string.Join(".",
                    node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                        .Reverse().Select(ns => ns.Name.ToString()));
                var containingTypes = node.Ancestors()
                    .OfType<BaseTypeDeclarationSyntax>()
                    .Reverse().Select(t => t.Identifier.ValueText).ToArray();

                declarations.Add(new CSharpDeclaration(
                    identity.Value.Kind,
                    identity.Value.Name,
                    containingNamespace,
                    containingTypes.Length == 0 ? null : string.Join(".", containingTypes),
                    file.Path,
                    file.Sha256,
                    location.StartLinePosition.Line + 1,
                    location.StartLinePosition.Character + 1,
                    location.EndLinePosition.Line + 1,
                    location.EndLinePosition.Character + 1,
                    node.SpanStart,
                    node.Span.Length,
                    identity.Value.ParameterCount));
            }
        }

        return new CSharpSyntaxReport(
            snapshot.WorkspaceId,
            snapshot.RepositoryId,
            snapshot.Id,
            snapshot.FingerprintSha256,
            AnalyzerVersion,
            "Roslyn ParseText syntax nodes (no compilation)",
            "LanguageVersion.Preview; no custom preprocessor symbols",
            csFiles.Length,
            syntaxErrorCount,
            syntaxErrorCount > 0,
            declarations,
            diagnostics,
            new[]
            {
                "Observations identify parsed declarations, not resolved symbols or runtime behavior.",
                "No project evaluation, compilation, references, generators, analyzers or build targets were run.",
                "Conditional compilation was parsed without project-specific symbols; inactive branches are not represented.",
                "Namespace/type/member names may be ambiguous across partial types, files and overloads.",
                "Syntax errors may leave recoverable but incomplete declarations; review diagnostics before using findings.",
                "Evidence anchors belong to the saved snapshot file hash and source span, not the mutable working tree."
            });
    }

    private static (string Kind, string Name, int? ParameterCount)? Identify(SyntaxNode node) =>
        node switch
        {
            BaseNamespaceDeclarationSyntax ns => ("namespace", ns.Name.ToString(), null),
            RecordDeclarationSyntax record => ("record", record.Identifier.ValueText, null),
            ClassDeclarationSyntax cls => ("class", cls.Identifier.ValueText, null),
            StructDeclarationSyntax structure => ("struct", structure.Identifier.ValueText, null),
            InterfaceDeclarationSyntax iface => ("interface", iface.Identifier.ValueText, null),
            EnumDeclarationSyntax enumeration => ("enum", enumeration.Identifier.ValueText, null),
            DelegateDeclarationSyntax del => ("delegate", del.Identifier.ValueText,
                del.ParameterList.Parameters.Count),
            MethodDeclarationSyntax method => ("method", method.Identifier.ValueText,
                method.ParameterList.Parameters.Count),
            ConstructorDeclarationSyntax constructor => ("constructor", constructor.Identifier.ValueText,
                constructor.ParameterList.Parameters.Count),
            PropertyDeclarationSyntax property => ("property", property.Identifier.ValueText, null),
            _ => null
        };
}
