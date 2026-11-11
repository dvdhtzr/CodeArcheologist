using System.Text;
using CodeArcheologist.Api.Investigations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeArcheologist.Api.Workspaces;

/// <summary>A location in copied, SHA-256-verified source; null spans mean file-level context.</summary>
public sealed record HostCautionAnchor(
    string FilePath, string FileSha256, int? Line, int? Column,
    int? SpanStart, int? SpanLength);

/// <summary>
/// Host-authored source-syntax warning. The status is never runtime verification
/// and the statement is never supplied by a model or a corpus label.
/// </summary>
public sealed record HostSyntaxCaution(
    string Id, string Kind, string Status, string Statement,
    string AnalyzerVersion, IReadOnlyList<HostCautionAnchor> Anchors);

/// <summary>
/// Conservative, deterministic warnings over a single selected HTTP candidate.
/// Reads only copied snapshot bytes; no project evaluation, semantic compilation,
/// source execution, or written-name callee binding.
/// </summary>
public sealed class CSharpHostCautionAnalyzer(
    SnapshotCaptureService capture,
    StaticCSharpProjectInspector projectInspector)
{
    public const string Version = "roslyn-host-cautions-v1";
    public const string Status = "host-syntax-caution";
    private const int MaxSourceBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    public IReadOnlyList<HostSyntaxCaution> Inspect(
        SnapshotRecord snapshot, CSharpEvidenceGraphReport graph,
        EvidenceGraphNode selected, CSharpMinimalApiCandidate? minimal,
        CSharpHttpAttributeCandidate? attribute, CancellationToken ct)
    {
        if (graph.WorkspaceId != snapshot.WorkspaceId ||
            graph.RepositoryId != snapshot.RepositoryId ||
            graph.SnapshotId != snapshot.Id ||
            graph.FingerprintSha256 != snapshot.FingerprintSha256 ||
            selected.FilePath is null || selected.FileSha256 is null ||
            (minimal is null) == (attribute is null) ||
            selected.Kind != (minimal is null ? "http-endpoint-attribute" : "http-minimal-mapping"))
            throw new SnapshotCaptureException("Host caution scope or candidate is inconsistent.", 409);

        var manifest = capture.ReadManifest(snapshot);
        var file = manifest.Files.SingleOrDefault(item =>
            item.Path == selected.FilePath && item.Sha256 == selected.FileSha256);
        if (file is null || file.SizeBytes > MaxSourceBytes)
            throw new SnapshotCaptureException("Host caution source is missing or exceeds its limit.", 413);

        string text;
        try { text = StrictUtf8.GetString(capture.ReadVerifiedSnapshotFile(snapshot, file)); }
        catch (DecoderFallbackException)
        {
            throw new SnapshotCaptureException("Host caution source is not valid UTF-8.", 422);
        }

        var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path: file.Path, cancellationToken: ct);
        var root = tree.GetRoot(ct);
        var cautions = new List<HostSyntaxCaution>();
        HostCautionAnchor Anchor(SyntaxNode node)
        {
            var at = tree.GetLineSpan(node.Span, ct).StartLinePosition;
            return new(file.Path, file.Sha256, at.Line + 1, at.Character + 1,
                node.SpanStart, node.Span.Length);
        }
        void Add(string kind, string statement, params SyntaxNode[] nodes)
        {
            if (nodes.Length == 0 || nodes.Any(node => node.SyntaxTree != tree))
                throw new SnapshotCaptureException("Host caution lacks a source anchor.", 409);
            cautions.Add(new("host-caution:" + kind + ":" + selected.Id,
                kind, Status, statement, Version,
                nodes.Select(Anchor).Distinct().ToArray()));
        }

        if (minimal is { } mapping)
        {
            if (mapping.FilePath != file.Path || mapping.FileSha256 != file.Sha256)
                throw new SnapshotCaptureException("Host caution mapping scope is invalid.", 409);
            var site = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .SingleOrDefault(node => node.SpanStart == mapping.SpanStart &&
                    node.Span.Length == mapping.SpanLength);
            if (site is null || !IsMapping(site))
                throw new SnapshotCaptureException("Host caution mapping is not saved syntax.", 409);

            // A method that contains the mapping may be conventionally invoked
            // outside the snapshot. No provisional caller link is NOT non-reachability.
            if (NearestExecutionScope(site) is MethodDeclarationSyntax method)
            {
                var declarationId = "declaration:" + file.Path + ":" + file.Sha256 +
                    ":" + method.SpanStart + ":" + method.Span.Length;
                var mappingRelation = graph.Edges.SingleOrDefault(edge =>
                    edge.Kind == "candidate-http-mapping" && edge.FromId == selected.Id);
                if (mappingRelation is null ||
                    !graph.Edges.Any(edge => edge.Kind == "contains-invocation" &&
                        edge.FromId == declarationId && edge.ToId == mappingRelation.ToId))
                    throw new SnapshotCaptureException("Host caution mapping containment is inconsistent.", 409);
                if (!graph.Edges.Any(edge => edge.Kind == "candidate-callee" &&
                    edge.Status == "provisional-local" && edge.ToId == declarationId))
                    Add("unobserved-registration",
                        "The mapping is written inside a method with no supported source-local caller link in this snapshot. How that method is invoked remains unverified.",
                        site, method);
            }

            if (TryGroupReceiver(site, root) is { } group)
                Add("route-group-receiver",
                    "The mapping receiver is associated by written syntax with a MapGroup invocation. Route strings remain separate observations; no effective URL is established.",
                    site, group);

            if (MappingScope(site) is { } scope &&
                site.Expression is MemberAccessExpressionSyntax selectedMember)
            {
                var selectedModifiers = Modifiers(site);
                var sibling = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(other => other != site && IsMapping(other) &&
                        MappingScope(other) == scope &&
                        other.Expression is MemberAccessExpressionSyntax member &&
                        member.Expression.ToString() == selectedMember.Expression.ToString() &&
                        !Modifiers(other).SetEquals(selectedModifiers))
                    .OrderBy(other => other.SpanStart).FirstOrDefault();
                if (sibling is not null)
                    Add("sibling-modifier-difference",
                        "Mappings in the same written registration scope have different authorization-related fluent modifiers. Effective authorization policy remains unverified.",
                        site, sibling);
            }
        }
        else if (attribute is { } endpoint)
        {
            if (endpoint.FilePath != file.Path || endpoint.FileSha256 != file.Sha256)
                throw new SnapshotCaptureException("Host caution attribute scope is invalid.", 409);
            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .SingleOrDefault(node => node.SpanStart == endpoint.MethodSpanStart &&
                    node.Span.Length == endpoint.MethodSpanLength);
            if (method is null)
                throw new SnapshotCaptureException("Host caution method is not saved syntax.", 409);

            var conditional = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(call => NearestExecutionScope(call) == method)
                .Where(call => ConditionalOwner(call, method) is not null)
                .OrderBy(call => call.SpanStart).FirstOrDefault();
            if (conditional is not null)
                Add("conditional-invocation",
                    "A direct invocation appears in a conditional source branch. The syntax does not establish that it occurs on every execution path.",
                    conditional, ConditionalOwner(conditional, method)!);
        }

        // Reuse the safe, no-MSBuild static XML inspector. Project declarations
        // are file-level candidates, NOT verified compilation or application scope.
        var projects = projectInspector.Inspect(snapshot, ct).Projects
            .OrderBy(project => project.ProjectPath, StringComparer.Ordinal).ToArray();
        if (projects.Length > 1)
        {
            SyntaxNode anchor = minimal is null
                ? root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(node =>
                    node.SpanStart == attribute!.MethodSpanStart &&
                    node.Span.Length == attribute!.MethodSpanLength)
                : root.DescendantNodes().OfType<InvocationExpressionSyntax>().Single(node =>
                    node.SpanStart == minimal.SpanStart &&
                    node.Span.Length == minimal.SpanLength);
            cautions.Add(new("host-caution:multiple-project-candidates:" + selected.Id,
                "multiple-project-candidates", Status,
                "This snapshot contains multiple static project-file candidates. Conclusions remain scoped to the selected entry point; effective project composition and cross-project behavior are unverified.",
                Version,
                new[] { Anchor(anchor) }.Concat(projects.Take(2).Select(project =>
                    new HostCautionAnchor(project.ProjectPath, project.ProjectSha256,
                        null, null, null, null))).ToArray()));
        }

        if (cautions.Select(c => c.Kind).Distinct(StringComparer.Ordinal).Count() != cautions.Count ||
            cautions.Count > 5)
            throw new SnapshotCaptureException("Host caution cardinality is inconsistent.", 409);
        return cautions.OrderBy(c => c.Kind, StringComparer.Ordinal).ToArray();
    }

    private static SyntaxNode? NearestExecutionScope(SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(parent =>
            parent is MethodDeclarationSyntax or ConstructorDeclarationSyntax or
                LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or
                AccessorDeclarationSyntax or PropertyDeclarationSyntax or
                FieldDeclarationSyntax or OperatorDeclarationSyntax or
                ConversionOperatorDeclarationSyntax or DestructorDeclarationSyntax or
                GlobalStatementSyntax);

    private static bool IsMapping(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax member &&
        member.Name is IdentifierNameSyntax name &&
        name.Identifier.ValueText is "MapGet" or "MapPost" or "MapPut" or
            "MapPatch" or "MapDelete";

    private static bool IsGroupCall(InvocationExpressionSyntax call) =>
        call.Expression is MemberAccessExpressionSyntax member &&
        member.Name is IdentifierNameSyntax name &&
        name.Identifier.ValueText == "MapGroup";

    private static SyntaxNode? MappingScope(SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(parent =>
            parent is BlockSyntax or CompilationUnitSyntax);

    private static InvocationExpressionSyntax? TryGroupReceiver(
        InvocationExpressionSyntax mapping, SyntaxNode root)
    {
        if (mapping.Expression is not MemberAccessExpressionSyntax member)
            return null;
        if (member.Expression is InvocationExpressionSyntax direct && IsGroupCall(direct))
            return direct;
        if (member.Expression is not IdentifierNameSyntax receiver ||
            MappingScope(mapping) is not { } scope)
            return null;

        var declarations = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(decl => decl.Identifier.ValueText == receiver.Identifier.ValueText &&
                decl.SpanStart < mapping.SpanStart && MappingScope(decl) == scope)
            .ToArray();
        if (declarations.Length != 1 ||
            declarations[0].Initializer?.Value is not InvocationExpressionSyntax group ||
            !IsGroupCall(group))
            return null;

        var reassigned = scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Any(assignment => assignment.SpanStart < mapping.SpanStart &&
                assignment.Left is IdentifierNameSyntax id &&
                id.Identifier.ValueText == receiver.Identifier.ValueText);
        var mutated = scope.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
            .Any(unary => unary.SpanStart < mapping.SpanStart &&
                unary.Operand is IdentifierNameSyntax id &&
                id.Identifier.ValueText == receiver.Identifier.ValueText);
        return reassigned || mutated ? null : group;
    }

    private static HashSet<string> Modifiers(InvocationExpressionSyntax mapping)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        SyntaxNode current = mapping;
        while (current.Parent is MemberAccessExpressionSyntax member &&
               member.Expression == current &&
               member.Parent is InvocationExpressionSyntax outer &&
               outer.Expression == member)
        {
            if (member.Name is IdentifierNameSyntax name &&
                name.Identifier.ValueText is "RequireAuthorization" or "AllowAnonymous")
                result.Add(name.Identifier.ValueText);
            current = outer;
        }
        return result;
    }

    private static SyntaxNode? ConditionalOwner(InvocationExpressionSyntax call, MethodDeclarationSyntax method)
    {
        foreach (var parent in call.Ancestors().TakeWhile(node => node != method))
        {
            if (parent is IfStatementSyntax branch &&
                (branch.Statement.Span.Contains(call.Span) ||
                 branch.Else?.Statement.Span.Contains(call.Span) == true))
                return branch;
            if (parent is SwitchSectionSyntax section) return section;
            if (parent is ConditionalExpressionSyntax ternary &&
                (ternary.WhenTrue.Span.Contains(call.Span) ||
                 ternary.WhenFalse.Span.Contains(call.Span)))
                return ternary;
            if (parent is CatchClauseSyntax caught && caught.Block.Span.Contains(call.Span))
                return caught;
        }

        // A guard with a syntactically direct early exit makes a later call
        // conditional. This is NOT an attempt at complete control-flow analysis.
        var statement = call.Ancestors().OfType<StatementSyntax>()
            .FirstOrDefault(s => s.Parent is BlockSyntax);
        if (statement?.Parent is BlockSyntax block)
            return block.Statements.TakeWhile(s => s != statement)
                .OfType<IfStatementSyntax>()
                .FirstOrDefault(branch => branch.Statement is ReturnStatementSyntax or ThrowStatementSyntax ||
                    branch.Statement is BlockSyntax body &&
                    body.Statements.Any(s => s is ReturnStatementSyntax or ThrowStatementSyntax));
        return null;
    }
}
