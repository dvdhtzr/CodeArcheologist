namespace CodeArcheologist.Api.Workspaces;

public sealed record SourceInventoryFile(
    string Path,
    string Sha256,
    long SizeBytes,
    string Language,
    string Role);

public sealed record SourceInventoryCount(
    string Name,
    int FileCount,
    long TotalBytes);

public sealed record SourceInventoryReport(
    Guid WorkspaceId,
    Guid RepositoryId,
    Guid SnapshotId,
    string FingerprintSha256,
    string AnalyzerVersion,
    string DetectionMethod,
    int IncludedFileCount,
    long IncludedBytes,
    int ExcludedEntryCount,
    IReadOnlyList<SourceInventoryCount> Languages,
    IReadOnlyList<SourceInventoryCount> Roles,
    IReadOnlyList<SourceInventoryFile> Files,
    IReadOnlyList<SnapshotExclusion> Exclusions,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Deterministic, content-independent filename classification over a VERIFIED
/// immutable snapshot manifest. No project files, source files, build tools,
/// Git commands, compiler loaders or model agents are opened/invoked here.
/// The observed filename and hash are evidence; classification is a heuristic.
/// </summary>
public static class SourceInventoryBuilder
{
    public const string Version = "manifest-inventory-v1";
    public const string Method = "path-and-extension-only";

    private static readonly HashSet<string> SourceLanguages = new(StringComparer.Ordinal)
    {
        "C#", "F#", "Visual Basic", "TypeScript", "JavaScript", "Python",
        "Java", "Go", "Rust", "C", "C++", "Kotlin", "Swift", "Ruby",
        "PHP", "Bash", "PowerShell"
    };

    private static readonly HashSet<string> DependencyManifests =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "package.json", "package-lock.json", "pnpm-lock.yaml", "yarn.lock",
            "npm-shrinkwrap.json", "requirements.txt", "pipfile", "pipfile.lock",
            "pyproject.toml", "poetry.lock", "uv.lock",
            "cargo.toml", "cargo.lock", "go.mod", "go.sum",
            "pom.xml", "gradle.properties", "build.gradle", "build.gradle.kts",
            "gemfile", "gemfile.lock", "composer.json", "composer.lock",
            "packages.config", "nuget.config", "directory.packages.props"
        };

    public static SourceInventoryReport Build(SnapshotManifest manifest)
    {
        var files = manifest.Files
            .Select(file => Classify(file))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        var exclusions = manifest.Exclusions
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Reason, StringComparer.Ordinal)
            .ToArray();

        return new SourceInventoryReport(
            manifest.WorkspaceId,
            manifest.RepositoryId,
            manifest.SnapshotId,
            manifest.FingerprintSha256,
            Version,
            Method,
            files.Length,
            files.Sum(file => file.SizeBytes),
            exclusions.Length,
            Summarize(files, file => file.Language),
            Summarize(files, file => file.Role),
            files,
            exclusions,
            new[]
            {
                "Language and role labels are inferred from filenames only; content was not parsed.",
                "Project files and package manifests were not evaluated, restored, or executed.",
                "No symbol, API, dependency, entry-point, or behavior claims are made.",
                "Excluded subtrees were not inspected; captured files are not scanned for secrets."
            });
    }

    private static SourceInventoryFile Classify(SnapshotFile file)
    {
        var name = file.Path.Split('/')[^1];
        var extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
        var language = Language(name, extension);
        var role = Role(file.Path, name, extension, language);
        return new SourceInventoryFile(
            file.Path, file.Sha256, file.SizeBytes, language, role);
    }

    private static string Language(string name, string extension)
    {
        if (string.Equals(name, "Dockerfile", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase))
            return "Dockerfile";

        return extension switch
        {
            ".cs" => "C#",
            ".fs" or ".fsx" => "F#",
            ".vb" => "Visual Basic",
            ".ts" or ".tsx" or ".mts" or ".cts" => "TypeScript",
            ".js" or ".jsx" or ".mjs" or ".cjs" => "JavaScript",
            ".py" => "Python",
            ".java" => "Java",
            ".go" => "Go",
            ".rs" => "Rust",
            ".c" or ".h" => "C",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" => "C++",
            ".kt" or ".kts" => "Kotlin",
            ".swift" => "Swift",
            ".rb" => "Ruby",
            ".php" => "PHP",
            ".sh" or ".bash" => "Bash",
            ".ps1" or ".psm1" or ".psd1" => "PowerShell",
            ".sql" => "SQL",
            ".json" or ".jsonc" => "JSON",
            ".yaml" or ".yml" => "YAML",
            ".xml" or ".csproj" or ".fsproj" or ".vbproj" or ".props"
                or ".targets" or ".config" => "XML",
            ".toml" => "TOML",
            ".md" or ".mdx" => "Markdown",
            ".html" or ".htm" => "HTML",
            ".css" or ".scss" or ".sass" => "CSS",
            _ => "Unknown"
        };
    }

    private static string Role(
        string path, string name, string extension, string language)
    {
        if (extension is ".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx" ||
            name.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            return "project-manifest";

        if (DependencyManifests.Contains(name))
            return "dependency-manifest";

        if (language == "Dockerfile" ||
            name.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
            return "build-configuration";

        if (language == "SQL")
            return "database-script";

        if (SourceLanguages.Contains(language))
        {
            var segments = path.Split('/');
            var inTestDirectory = segments.Take(segments.Length - 1).Any(
                part => part.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("__tests__", StringComparison.OrdinalIgnoreCase));
            var testSuffix = name.EndsWith(".test" + extension, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".spec" + extension, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("Tests" + extension, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("Test" + extension, StringComparison.OrdinalIgnoreCase);
            return inTestDirectory || testSuffix ? "test-source-candidate" : "source-code";
        }

        if (language == "Markdown" ||
            name.StartsWith("readme", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("license", StringComparison.OrdinalIgnoreCase))
            return "documentation";

        if (language is "JSON" or "YAML" or "XML" or "TOML" or "CSS" ||
            extension is ".ini" or ".editorconfig" or ".config" ||
            name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
            return "configuration";

        return "unclassified";
    }

    private static SourceInventoryCount[] Summarize(
        IEnumerable<SourceInventoryFile> files,
        Func<SourceInventoryFile, string> keySelector) =>
        files.GroupBy(keySelector, StringComparer.Ordinal)
            .Select(group => new SourceInventoryCount(
                group.Key, group.Count(), group.Sum(file => file.SizeBytes)))
            .OrderByDescending(group => group.FileCount)
            .ThenBy(group => group.Name, StringComparer.Ordinal)
            .ToArray();
}
