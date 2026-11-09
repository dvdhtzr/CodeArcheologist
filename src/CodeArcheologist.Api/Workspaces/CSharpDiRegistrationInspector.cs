using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeArcheologist.Api.Workspaces;

public sealed record CSharpDiRegistrationCandidate(
    string Lifetime,
    string WrittenMemberName,
    string WrittenReceiver,
    string? ServiceTypeSyntax,
    string? ImplementationTypeSyntax,
    string RegistrationShape,
    string FactoryStatus,
    string FilePath,
    string FileSha256,
    int Line,
    int Column,
    int SpanStart,
    int SpanLength);

public sealed record CSharpDiRegistrationReport(
    Guid WorkspaceId,
    Guid RepositoryId,
    Guid SnapshotId,
    string FingerprintSha256,
    string AnalyzerVersion,
    int CSharpFileCount,
    int CandidateCount,
    int UnresolvedShapeCount,
    IReadOnlyList<CSharpDiRegistrationCandidate> Candidates,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Finds conventional AddScoped/AddSingleton/AddTransient member-call spellings
/// in SHA-256-verified copied snapshot source. A written registration is not
/// a compiler-bound Microsoft DI call or a runtime service resolution.
/// </summary>
public sealed class CSharpDiRegistrationInspector(SnapshotCaptureService capture)
{
    public const string Version = "roslyn-di-registration-candidates-v1";
    private const int MaxFiles = 250;
    private const long MaxSourceBytes = 8L * 1024 * 1024;
    private const int MaxCandidates = 3000;
    private const int MaxWrittenTypeLength = 256;
    private const int MaxReceiverLength = 160;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly CSharpParseOptions Options =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    public CSharpDiRegistrationReport Inspect(
        SnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        var manifest = capture.ReadManifest(snapshot);
        var files = manifest.Files
            .Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        if (files.Length > MaxFiles ||
            files.Sum(file => file.SizeBytes) > MaxSourceBytes)
            throw new SnapshotCaptureException(
                "DI registration candidate inspection is limited to 250 C# files / 8 MiB.", 413);

        var results = new List<CSharpDiRegistrationCandidate>();
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
                if (invocation.Expression is not MemberAccessExpressionSyntax member)
                    continue;

                var writtenMember = member.Name switch
                {
                    GenericNameSyntax generic => generic.Identifier.ValueText,
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    _ => string.Empty
                };
                var lifetime = writtenMember switch
                {
                    "AddScoped" => "scoped",
                    "AddSingleton" => "singleton",
                    "AddTransient" => "transient",
                    _ => null
                };
                if (lifetime is null) continue;
                if (results.Count >= MaxCandidates)
                    throw new SnapshotCaptureException(
                        "DI registration syntax occurrences exceeded the 3000-site limit.", 413);

                var receiver = member.Expression.ToString()
                    .Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (receiver.Length > MaxReceiverLength)
                    receiver = receiver[..MaxReceiverLength] + "...";

                var (service, implementation, shape, factoryStatus) =
                    Classify(member.Name, invocation.ArgumentList);

                var span = tree.GetLineSpan(invocation.Span, cancellationToken);
                results.Add(new CSharpDiRegistrationCandidate(
                    lifetime,
                    writtenMember,
                    receiver,
                    service,
                    implementation,
                    shape,
                    factoryStatus,
                    file.Path,
                    file.Sha256,
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1,
                    invocation.SpanStart,
                    invocation.Span.Length));
            }
        }

        return new CSharpDiRegistrationReport(
            snapshot.WorkspaceId, snapshot.RepositoryId, snapshot.Id,
            snapshot.FingerprintSha256, Version, files.Length,
            results.Count,
            results.Count(item => item.RegistrationShape == "unresolved"),
            results,
            [
                "The analyzer recognizes only conventional unqualified member names AddScoped, AddSingleton, AddTransient, not verified Microsoft.Extensions.DependencyInjection API symbols.",
                "The receiver, interface/service and implementation types are written source syntax only; namespace aliases, assembly identity and actual type symbols are not resolved.",
                "A syntactic two-type registration is a candidate service-to-implementation association, not proof of successful runtime DI binding.",
                "Generic factory/instance overloads and nonliteral or unsupported forms remain explicit unknowns, not invented service implementation identities.",
                "A same-named unrelated custom member can be reported as a candidate (false positive). Reflection, scanning, TryAdd, keyed and conditional registrations are not evaluated.",
                "DI registration order, overrides, scopes, disposal behavior, missing constructors, open generics and actual runtime resolution are not analyzed.",
                "Snapshot parsing is bounded, uses strict UTF-8 and SHA-256 verified saved source only, with no untrusted source execution or MSBuild.",
                "The graph joins each candidate to one independently observed invocation site using exact path, file SHA-256 and syntax span."
            ]);
    }

    private static (string? Service, string? Implementation, string Shape, string FactoryStatus)
        Classify(SimpleNameSyntax method, ArgumentListSyntax arguments)
    {
        if (method is GenericNameSyntax generic)
        {
            var types = generic.TypeArgumentList.Arguments;
            if (types.Count == 2 && arguments.Arguments.Count == 0)
            {
                var service = WrittenType(types[0]);
                var implementation = WrittenType(types[1]);
                return service is null || implementation is null
                    ? (null, null, "unresolved", "unresolved")
                    : (service, implementation, "generic-service-implementation", "no-factory-argument");
            }

            if (types.Count == 1)
            {
                var service = WrittenType(types[0]);
                if (service is null)
                    return (null, null, "unresolved", "unresolved");
                if (arguments.Arguments.Count == 0)
                    return (service, null, "generic-self-or-service", "no-factory-argument");
                return (service, null, "generic-service-with-argument",
                    FactoryShape(arguments.Arguments[0].Expression));
            }
            return (null, null, "unresolved", "unresolved");
        }

        // A simple non-generic typeof(IService), typeof(Implementation)
        // pair is observable syntax. Anything else has unknown types.
        if (arguments.Arguments.Count == 2 &&
            arguments.Arguments[0].Expression is TypeOfExpressionSyntax serviceType &&
            arguments.Arguments[1].Expression is TypeOfExpressionSyntax implementationType)
        {
            var service = WrittenType(serviceType.Type);
            var implementation = WrittenType(implementationType.Type);
            return service is null || implementation is null
                ? (null, null, "unresolved", "unresolved")
                : (service, implementation, "typeof-service-implementation",
                    "no-factory-argument");
        }

        return (null, null, "unresolved", "unresolved");
    }

    private static string? WrittenType(TypeSyntax syntax)
    {
        var text = syntax.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length is >= 1 and <= MaxWrittenTypeLength ? text : null;
    }

    private static string FactoryShape(ExpressionSyntax expression) => expression switch
    {
        SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax =>
            "lambda-factory-syntax-unresolved",
        AnonymousMethodExpressionSyntax => "anonymous-factory-syntax-unresolved",
        _ => "factory-or-instance-unresolved"
    };
}
