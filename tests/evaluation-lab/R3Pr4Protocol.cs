using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeArcheologist.EvaluationLab;

/// <summary>PR4 uses Git's canonical blob identity to pin the *exact* JSON bytes
/// approved before adaptive results exist. No Git process or network is invoked.
/// The existing SHA-256 source fingerprints remain the evidence authority.</summary>
public sealed record R3Pr4CaseLock(string Id, string CaseGitBlob, string LabelsGitBlob);

public sealed record R3Pr4Experiment(
    string SchemaVersion, string BaselineCommit, string LegacyLabConfigGitBlob,
    IReadOnlyList<string> Comparators, int PrimaryTrials, int DiagnosticExtraTrials,
    int TotalCases, int InvestigateCases, int DiagnosticCases,
    string PrimaryOutcome, double MinimumDetectionGainPoints,
    string ConfidenceInterval, double ConfidenceLevel,
    double MinimumAdaptiveCompletionRate, double MaximumAdaptiveCompletionLagPoints,
    double MinimumObjectionCoverage, int AllowedIntegrityBreaches,
    string ActionableUsefulness, string Verdict,
    IReadOnlyList<R3Pr4CaseLock> CaseLocks);

/// <summary>
/// Separate preregistration. The existing C7 config, cases, labels and
/// thresholds are not edited to accommodate PR4. Validate *before* any
/// comparator, model call or report write, including provisional trial runs.
/// </summary>
public static class R3Pr4Protocol
{
    public const string Version = "r3-pr4-preregistered-comparison-v1";
    public const string BaselineCommit = "a0d1864c7aa6fd3eedd6996d72866f99ee7d207a";
    public const string FileName = "r3-pr4-experiment.json";
    public static readonly IReadOnlyList<string> Comparators =
        [Comparator.Baseline, Comparator.GateB, Comparator.GateC, Comparator.Adaptive];

    public static R3Pr4Experiment? ReadAndValidate(string evalRoot, CorpusReport corpus,
        out IReadOnlyList<string> errors)
    {
        var failures = new List<string>();
        R3Pr4Experiment? specification;
        try
        {
            specification = JsonSerializer.Deserialize<R3Pr4Experiment>(
                File.ReadAllText(Path.Combine(evalRoot, FileName)), LabSchema.Json);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            errors = ["PR4 manifest unavailable or unreadable: " + error.GetType().Name];
            return null;
        }
        if (specification is null)
        {
            errors = ["PR4 manifest is empty."];
            return null;
        }
        if (!corpus.Valid) failures.Add("PR4 cannot admit an invalid legacy corpus.");
        if (specification.SchemaVersion != Version || specification.BaselineCommit != BaselineCommit)
            failures.Add("PR4 schema or frozen baseline commit changed.");
        if (specification.Comparators is null ||
            !specification.Comparators.SequenceEqual(Comparators, StringComparer.Ordinal))
            failures.Add("PR4 comparators changed.");
        if (specification.PrimaryTrials != 1 || specification.DiagnosticExtraTrials != 2 ||
            specification.TotalCases != 50 || specification.InvestigateCases != 46 ||
            specification.DiagnosticCases != 12)
            failures.Add("PR4 frozen case counts or trial schedule changed.");
        if (specification.PrimaryOutcome != "paired-material-issue-detection-difference" ||
            specification.MinimumDetectionGainPoints != 10 ||
            specification.ConfidenceInterval != "paired-case-cluster-bootstrap-95-v1" ||
            specification.ConfidenceLevel != 0.95 ||
            specification.MinimumAdaptiveCompletionRate != 0.90 ||
            specification.MaximumAdaptiveCompletionLagPoints != 5 ||
            specification.MinimumObjectionCoverage != 1 ||
            specification.AllowedIntegrityBreaches != 0 ||
            specification.ActionableUsefulness != "blinded-external-judgment-no-regression" ||
            specification.Verdict != "no-claim-of-gain-if-interval-includes-zero")
            failures.Add("PR4 preregistered thresholds or outcome changed.");

        var config = Path.Combine(evalRoot, "lab-config.json");
        if (!File.Exists(config) || GitBlob(File.ReadAllBytes(config)) !=
            specification.LegacyLabConfigGitBlob)
            failures.Add("PR4 legacy C7 configuration is no longer the pinned baseline.");

        if (corpus.Cases.Count != 50 ||
            corpus.Cases.Count(c => c.Case.Expectation == CaseExpectation.Investigate) != 46 ||
            corpus.Cases.Count(c => c.Case.DiagnosticRepeat) != 12 ||
            corpus.DraftLabels != 0 || corpus.ApprovedLabels != 50)
            failures.Add("PR4 requires exactly 50 owner-approved cases, 46 investigative, 12 diagnostic.");

        var locks = specification.CaseLocks;
        var actual = corpus.Cases.OrderBy(c => c.Case.Id, StringComparer.Ordinal).ToArray();
        if (locks is null || locks.Count != actual.Length ||
            !locks.Select(c => c.Id).SequenceEqual(actual.Select(c => c.Case.Id), StringComparer.Ordinal))
            failures.Add("PR4 case identities are missing, reordered or changed.");
        else
            foreach (var (locked, loaded) in locks.Zip(actual))
            {
                var file = Path.Combine(loaded.Directory, "case.json");
                var labels = Path.Combine(loaded.Directory, "labels.json");
                if (!File.Exists(file) || !File.Exists(labels) ||
                    GitBlob(File.ReadAllBytes(file)) != locked.CaseGitBlob ||
                    GitBlob(File.ReadAllBytes(labels)) != locked.LabelsGitBlob)
                    failures.Add("PR4 case/approved-label bytes changed: " + locked.Id);
            }

        errors = failures;
        return failures.Count == 0 ? specification : null;
    }

    /// <summary>Canonical Git blob: SHA-1("blob " + length + NUL + raw bytes).
    /// A provenance pin only, not a cryptographic MAC or an evidence citation.</summary>
    private static string GitBlob(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
        var input = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, input, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, input, header.Length, bytes.Length);
        return Convert.ToHexString(SHA1.HashData(input)).ToLowerInvariant();
    }
}
