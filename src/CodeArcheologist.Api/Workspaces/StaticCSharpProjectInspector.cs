using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CodeArcheologist.Api.Workspaces;

public sealed record StaticProjectFile(
    string Path,
    string Sha256,
    string InclusionBasis);

public sealed record StaticProjectCandidate(
    string ProjectPath,
    string ProjectSha256,
    string? DeclaredSdk,
    bool DefaultCompileItemsInferred,
    IReadOnlyList<StaticProjectFile> CandidateFiles,
    IReadOnlyList<string> UnresolvedRules);

public sealed record StaticProjectMembershipReport(
    Guid WorkspaceId,
    Guid RepositoryId,
    Guid SnapshotId,
    string FingerprintSha256,
    string AnalyzerVersion,
    string Method,
    int ProjectCount,
    int CSharpFileCount,
    IReadOnlyList<StaticProjectCandidate> Projects,
    IReadOnlyList<StaticProjectFile> UnmappedFiles,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Conservative static reading of copied, SHA-256 verified .csproj XML.
/// Does not invoke or approximate MSBuild evaluation. Every membership is
/// a candidate, NEVER a verified compilation unit or dependency edge.
/// </summary>
public sealed class StaticCSharpProjectInspector(SnapshotCaptureService capture)
{
    public const string Version = "static-csproj-membership-v1";
    private const int MaxProjects = 100;
    private const long MaxProjectXmlBytes = 2L * 1024 * 1024;
    private const long MaxTotalXmlBytes = 8L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> RecognizedSdks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.NET.Sdk",
            "Microsoft.NET.Sdk.Web",
            "Microsoft.NET.Sdk.Worker",
            "Microsoft.NET.Sdk.Razor"
        };

    public StaticProjectMembershipReport Inspect(
        SnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        // ReadManifest verifies the manifest hash AND every saved artifact.
        var manifest = capture.ReadManifest(snapshot);
        var projects = manifest.Files
            .Where(file => file.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        var source = manifest.Files
            .Where(file => file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        if (projects.Length > MaxProjects ||
            projects.Any(file => file.SizeBytes > MaxProjectXmlBytes) ||
            projects.Sum(file => file.SizeBytes) > MaxTotalXmlBytes)
        {
            throw new SnapshotCaptureException(
                "Static project inspection is limited to 100 projects, 2 MiB per project XML, 8 MiB total.", 413);
        }

        var projectReports = new List<StaticProjectCandidate>();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var sourceByPath = source.ToDictionary(file => file.Path, StringComparer.Ordinal);
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = capture.ReadVerifiedSnapshotFile(snapshot, project);
            string xml;
            try
            {
                xml = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new SnapshotCaptureException("Invalid UTF-8 project XML: " + project.Path, 422);
            }

            XDocument document;
            try
            {
                var readerSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxProjectXmlBytes,
                    MaxCharactersFromEntities = 0
                };
                using var textReader = new StringReader(xml);
                using var reader = XmlReader.Create(textReader, readerSettings);
                document = XDocument.Load(reader, LoadOptions.None);
            }
            catch (XmlException)
            {
                throw new SnapshotCaptureException(
                    "Project XML cannot be safely parsed (DTD, invalid XML or document limit): " +
                    project.Path, 422);
            }

            if (document.Root?.Name.LocalName != "Project")
            {
                throw new SnapshotCaptureException(
                    "Saved .csproj has no valid Project root: " + project.Path, 422);
            }

            var root = document.Root!;
            var sdk = ((string?)root.Attribute("Sdk"))?.Trim();
            var unresolved = new SortedSet<string>(StringComparer.Ordinal);
            var candidate = new Dictionary<string, string>(StringComparer.Ordinal);
            var projectDirectory = Parent(project.Path);

            var hasProjectCondition = HasCondition(root);
            if (hasProjectCondition)
                unresolved.Add("Project has a condition that was not evaluated.");

            var recognizedSdk = sdk is not null && RecognizedSdks.Contains(sdk);
            if (!recognizedSdk)
                unresolved.Add("An unrecognized or absent SDK prevents inference of implicit Compile items.");

            if (root.Elements().Any(node => node.Name.LocalName is "Import" or "ImportGroup" or "Sdk"))
                unresolved.Add("Project imports and nested SDK declarations were not evaluated.");

            // If default-items settings depend on conditions, properties,
            // imported props or repeated declarations, do not infer defaults.
            var enableDefault = recognizedSdk && !hasProjectCondition;
            foreach (var key in new[] { "EnableDefaultCompileItems", "EnableDefaultItems" })
            {
                var settings = root.Elements()
                    .Where(group => group.Name.LocalName == "PropertyGroup")
                    .SelectMany(group => group.Elements()
                        .Where(item => item.Name.LocalName == key)
                        .Select(item => (Group: group, Item: item)))
                    .ToArray();

                if (settings.Length == 0) continue;
                if (settings.Length != 1 ||
                    HasCondition(settings[0].Group) ||
                    HasCondition(settings[0].Item) ||
                    !bool.TryParse(settings[0].Item.Value.Trim(), out var enabled))
                {
                    enableDefault = false;
                    unresolved.Add(key + " is conditional, repeated or not a literal boolean.");
                    continue;
                }

                if (!enabled) enableDefault = false;
            }

            if (enableDefault)
            {
                var prefix = projectDirectory.Length == 0 ? "" : projectDirectory + "/";
                foreach (var file in source)
                {
                    if (file.Path.StartsWith(prefix, StringComparison.Ordinal))
                        candidate[file.Path] = "sdk-default-pattern";
                }
            }

            var removePaths = new List<string>();
            foreach (var group in root.Elements()
                .Where(element => element.Name.LocalName == "ItemGroup"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var item in group.Elements()
                    .Where(element => element.Name.LocalName == "Compile"))
                {
                    var include = (string?)item.Attribute("Include");
                    var remove = (string?)item.Attribute("Remove");
                    var exclude = (string?)item.Attribute("Exclude");
                    if (hasProjectCondition || HasCondition(group) || HasCondition(item))
                    {
                        unresolved.Add("Conditional Compile items were not evaluated.");
                        continue;
                    }
                    if (exclude is not null)
                    {
                        unresolved.Add("Compile Exclude rules were not evaluated.");
                        continue;
                    }
                    if (include is not null)
                    {
                        foreach (var path in include.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var resolved = ResolveLiteral(projectDirectory, path);
                            if (resolved is null)
                                unresolved.Add("A Compile Include pattern/property/path is unsupported: " + path);
                            else if (sourceByPath.ContainsKey(resolved))
                                candidate[resolved] = "literal-compile-include";
                            else
                                unresolved.Add("A literal Compile Include is absent from this snapshot: " + path);
                        }
                    }
                    else if (remove is not null)
                    {
                        foreach (var path in remove.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var resolved = ResolveLiteral(projectDirectory, path);
                            if (resolved is null)
                                unresolved.Add("A Compile Remove pattern/property/path is unsupported: " + path);
                            else
                                removePaths.Add(resolved);
                        }
                    }
                    else if (item.Attribute("Update") is null)
                    {
                        unresolved.Add("A Compile item without supported Include/Remove was not evaluated.");
                    }
                }
            }

            // Literal removes are applied to the static candidate set only.
            foreach (var path in removePaths)
                candidate.Remove(path);

            var files = candidate.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry =>
                {
                    var file = sourceByPath[entry.Key];
                    matched.Add(file.Path);
                    return new StaticProjectFile(file.Path, file.Sha256, entry.Value);
                }).ToArray();

            // These are permanent unknowns in a static, unevaluated view.
            unresolved.Add("Imported/default item exclusion properties and real project evaluation are not available.");
            unresolved.Add("Build conditions, wildcard rules and project references may alter actual membership.");

            projectReports.Add(new StaticProjectCandidate(
                project.Path, project.Sha256, sdk, enableDefault, files, unresolved.ToArray()));
        }

        return new StaticProjectMembershipReport(
            snapshot.WorkspaceId, snapshot.RepositoryId, snapshot.Id,
            snapshot.FingerprintSha256, Version, "safe-static-csproj-xml-no-msbuild",
            projects.Length, source.Length, projectReports,
            source.Where(file => !matched.Contains(file.Path))
                .Select(file => new StaticProjectFile(file.Path, file.Sha256, "no-static-project-candidate"))
                .ToArray(),
            new[]
            {
                "All project membership is candidate-only, never verified compiler input or a resolved dependency.",
                "Project imports, target framework conditions, implicit excludes and property expansion were not evaluated.",
                "Recognized SDK default Compile patterns are heuristics and may include files from nested project directories.",
                "Only literal, unconditional Compile Include/Remove paths are applied; wildcard or conditional rules are reported as unresolved.",
                "All source files and project XML were read solely from the hash-verified saved snapshot."
            });
    }

    private static bool HasCondition(XElement element) =>
        element.Attributes().Any(attribute => attribute.Name.LocalName == "Condition");

    private static string Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "" : path[..index];
    }

    private static string? ResolveLiteral(string projectDirectory, string raw)
    {
        var value = raw.Trim().Replace('\\', '/');
        if (value.Length == 0 || value.StartsWith('/') ||
            value.Contains(':') ||
            value.IndexOfAny(new[] { '*', '?', (char)36, '@', '%', '|', '#' }) >= 0)
            return null;

        var segments = new List<string>();
        if (projectDirectory.Length > 0)
            segments.AddRange(projectDirectory.Split('/'));

        foreach (var segment in value.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                if (segment == "..")
                {
                    if (segments.Count == 0) return null;
                    segments.RemoveAt(segments.Count - 1);
                }
                continue;
            }

            if (segment.Any(char.IsControl)) return null;
            segments.Add(segment);
        }
        return segments.Count > 0 ? string.Join("/", segments) : null;
    }
}
