using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.EvaluationLab;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>
/// Pre-model, model-free host-capability probe. Diagnostic only.
/// It cannot establish actual model delivery, semantic truth or complete unavailability.
/// </summary>
public static class V2AnswerabilityPreflight
{
    public const string PlanSchema = "v2-answerability-plan-v1";
    public const string RecordSchema = "v2-answerability-preflight-record-v1";
    private static readonly string[] FixedCapabilities =
        GateCEvidenceRegistry.Discover().Select(x => x.Identifier).ToArray();

    public sealed record Binding(
        [property: System.Text.Json.Serialization.JsonRequired] string CriterionId,
        [property: System.Text.Json.Serialization.JsonRequired] string EvidenceId,
        [property: System.Text.Json.Serialization.JsonRequired] int ProofIndex);

    public sealed record Plan(
        [property: System.Text.Json.Serialization.JsonRequired] string SchemaVersion,
        [property: System.Text.Json.Serialization.JsonRequired] string CaseId,
        [property: System.Text.Json.Serialization.JsonRequired] string Comparator,
        [property: System.Text.Json.Serialization.JsonRequired] GateCRole Role,
        [property: System.Text.Json.Serialization.JsonRequired] string EntryKind,
        [property: System.Text.Json.Serialization.JsonRequired] string EntryPath,
        [property: System.Text.Json.Serialization.JsonRequired] int EntryLine,
        [property: System.Text.Json.Serialization.JsonRequired] string SourceReviewImportSha256,
        [property: System.Text.Json.Serialization.JsonRequired] IReadOnlyList<Binding> Bindings);

    public sealed record Record(
        [property: System.Text.Json.Serialization.JsonRequired] string SchemaVersion,
        [property: System.Text.Json.Serialization.JsonRequired] string CaseSha256,
        [property: System.Text.Json.Serialization.JsonRequired] string GroundTruthSha256,
        [property: System.Text.Json.Serialization.JsonRequired] string PlanSha256,
        [property: System.Text.Json.Serialization.JsonRequired] string SourceReviewImportSha256,
        [property: System.Text.Json.Serialization.JsonRequired] string CapturedSnapshotFingerprint,
        [property: System.Text.Json.Serialization.JsonRequired] string CapabilityContractVersion,
        [property: System.Text.Json.Serialization.JsonRequired] string EvidenceContractVersion,
        [property: System.Text.Json.Serialization.JsonRequired] V2Answerability Answerability,
        [property: System.Text.Json.Serialization.JsonRequired] string QualificationStatus);

    public sealed record HostView(
        IReadOnlyList<string> Capabilities,
        IReadOnlyList<GateC5EvidenceLink> Links,
        bool EvidenceTruncated,
        bool InputWithinBudget);

    /// <summary>
    /// Host-result-only classifier. Source anchors must be verified separately
    /// by PR2 and exact snapshot capture; matching an anchor is necessary, not
    /// sufficient to prove a semantic answer.
    /// </summary>
    public static IReadOnlyList<V2CriterionAnswerability> Classify(
        V2GroundTruth truth, Plan plan, IReadOnlyList<HostView> hostViews)
    {
        var result = new List<V2CriterionAnswerability>();
        foreach (var criterion in truth.Criteria)
        {
            var evidenceIds = criterion.RequiredEvidenceIds;
            var bindings = plan.Bindings.Where(x => x.CriterionId == criterion.Id).ToArray();
            if (evidenceIds.Count == 0 || bindings.Length != evidenceIds.Count ||
                bindings.Any(b => b.ProofIndex < 0 || b.ProofIndex >= criterion.SourceProofs.Count))
            {
                result.Add(new(criterion.Id, V2AnswerabilityState.Undetermined,
                    "No complete, source-reviewed evidence-to-proof mapping; positive availability unproven.",
                    evidenceIds));
                continue;
            }
            if (criterion.Kind is V2CriterionKind.RequiredUncertainty or
                V2CriterionKind.ForbiddenAssertion or V2CriterionKind.NegativeExpectation)
            {
                result.Add(new(criterion.Id, V2AnswerabilityState.Undetermined,
                    "Boundary/negative expectations require separate interpretive review; tool visibility is not proof.",
                    evidenceIds));
                continue;
            }
            var eligible = hostViews.Where(v => v.InputWithinBudget && !v.EvidenceTruncated &&
                v.Capabilities.Count <= GateC3IndependentRunner.MaxViewsPerRole &&
                v.Capabilities.Count <= GateCEvidenceSession.MaxToolCalls &&
                v.Links.Count <= GateCEvidenceSession.MaxTotalItems);
            var found = eligible.FirstOrDefault(view => bindings.All(binding =>
            {
                var proof = criterion.SourceProofs[binding.ProofIndex];
                return proof.Snapshot == V2SnapshotSide.After &&
                    view.Links.Any(link => link.FilePath == proof.FilePath &&
                        link.FileSha256 == proof.FileSha256 &&
                        link.Line >= proof.StartLine && link.Line <= proof.EndLine &&
                        link.Kind == GateCEvidenceKind.ObservedSyntax);
            }));
            if (found is not null)
                result.Add(new(criterion.Id, V2AnswerabilityState.Retrievable,
                    "A fresh verified read-only host session exposes all declared observed-syntax anchors " +
                    "within an authorized capability combination and the configured input/tool budgets. " +
                    "No model request or actual delivery was observed.",
                    evidenceIds));
            else
                result.Add(new(criterion.Id, V2AnswerabilityState.Undetermined,
                    "No permitted host view established all required observed-syntax anchors. " +
                    "Source excerpts, alternate code context or unsupported inference prevent a sound 'unavailable' claim.",
                    evidenceIds));
        }
        return result;
    }

    public static async Task<int> RunAsync(string root, string sources, string review,
        string planFile, string outputFile, TextWriter log, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var plan = V2CorpusValidator.ReadStrict<Plan>(planFile, problems);
        if (plan is null) return Reject(problems, log);
        if (plan.SchemaVersion != PlanSchema || plan.Comparator != "gate-c" ||
            plan.Role is not (GateCRole.Investigator or GateCRole.Skeptic) ||
            string.IsNullOrWhiteSpace(plan.EntryKind) ||
            string.IsNullOrWhiteSpace(plan.EntryPath) || plan.EntryLine < 1 ||
            plan.Bindings is null)
            problems.Add("invalid fixed Gate C preflight plan, role, selector, or schema");
        if (plan.CaseId is null || !System.Text.RegularExpressions.Regex.IsMatch(
            plan.CaseId, "^[A-Za-z][A-Za-z0-9-]{2,80}$"))
            problems.Add("invalid case identity");
        if (problems.Count > 0) return Reject(problems, log);
        // Never interpret a case ID or path as an unrestricted filesystem location.
        var caseFolder = Path.Combine(root, "cases", plan.CaseId);
        var casePath = Path.Combine(caseFolder, "case.json");
        var truthPath = Path.Combine(caseFolder, "ground-truth.json");
        var item = V2CorpusValidator.ReadStrict<V2Case>(casePath, problems);
        var truth = V2CorpusValidator.ReadStrict<V2GroundTruth>(truthPath, problems);
        if (item is null || truth is null) return Reject(problems, log);
        var structural = V2CorpusValidator.Validate(root);
        problems.AddRange(structural.Errors);
        if (problems.Count > 0) return Reject(problems, log);
        if (item.Id != plan.CaseId || truth.CaseId != plan.CaseId ||
            item.Partition != V2CorpusPartition.Diagnostic ||
            item.Task == V2TaskFamily.ChangeImpact)
            problems.Add("PR3a permits only current-code or boundary diagnostic cases; no change-impact scoring");
        if (!item.AuthorizedPaths.Contains(plan.EntryPath, StringComparer.Ordinal))
            problems.Add("entry-point selector is outside case-authorized paths");
        if (plan.Bindings.GroupBy(x => (x.CriterionId, x.EvidenceId)).Any(g => g.Count() != 1))
            problems.Add("duplicate evidence-to-criterion mapping");
        if (problems.Count > 0) return Reject(problems, log);
        var criteria = truth.Criteria.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var binding in plan.Bindings)
        {
            if (!criteria.TryGetValue(binding.CriterionId, out var criterion) ||
                !criterion.RequiredEvidenceIds.Contains(binding.EvidenceId, StringComparer.Ordinal) ||
                binding.ProofIndex < 0 || binding.ProofIndex >= criterion.SourceProofs.Count)
                problems.Add("invalid or foreign criterion/evidence/proof binding");
        }
        // Bind the independent PR2 review to exact source-truth bytes, not an agent report.
        var importPath = Path.Combine(review, "review-import.json");
        var manifestPath = Path.Combine(review, "manifest.json");
        var receipt = V2CorpusValidator.ReadStrict<V2SourceReviewImport>(importPath, problems);
        var manifest = V2CorpusValidator.ReadStrict<V2SourceReviewManifest>(manifestPath, problems);
        if (receipt is null || manifest is null) return Reject(problems, log);
        if (receipt.Answers?.Cases is null || manifest.Cases is null ||
            manifest.Cases.Count == 0 || receipt.Answers.Cases.Count == 0 ||
            manifest.Cases.Any(x => x is null) || receipt.Answers.Cases.Any(x => x is null))
            return Reject(["incomplete source-review import or manifest"], log);
        if (manifest.Cases.GroupBy(x => x.CaseId, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
            receipt.Answers.Cases.GroupBy(x => x.CaseId, StringComparer.Ordinal).Any(g => g.Count() > 1))
            return Reject(["duplicate source-review case identities"], log);
        var caseHash = Hash(File.ReadAllBytes(casePath));
        var truthHash = Hash(File.ReadAllBytes(truthPath));
        var importHash = Hash(File.ReadAllBytes(importPath));
        if (importHash != plan.SourceReviewImportSha256 ||
            receipt.SchemaVersion != V2SourceReviewSchemas.Import ||
            manifest.SchemaVersion != V2SourceReviewSchemas.Packet ||
            receipt.ExportSha256 != Hash(File.ReadAllBytes(manifestPath)) ||
            receipt.Answers.ExportSha256 != receipt.ExportSha256)
            problems.Add("review record/plan/manifest digest mismatch");
        var reviewedCase = manifest.Cases.SingleOrDefault(x => x.CaseId == plan.CaseId);
        var answer = receipt.Answers.Cases.SingleOrDefault(x => x.CaseId == plan.CaseId);
        if (reviewedCase is null || answer is null ||
            reviewedCase.CaseSha256 != caseHash || reviewedCase.GroundTruthSha256 != truthHash ||
            string.IsNullOrWhiteSpace(receipt.Answers.ReviewerId) ||
            string.Equals(receipt.Answers.ReviewerId, reviewedCase.AuthorId, StringComparison.OrdinalIgnoreCase))
            problems.Add("case is not bound to an independently reviewed source packet");
        else
        {
            var grades = answer.Criteria;
            if (grades is null || grades.Count != truth.Criteria.Count ||
                grades.Any(x => x is null) ||
                grades.Select(x => x.CriterionId).Distinct(StringComparer.Ordinal).Count() !=
                    truth.Criteria.Count ||
                grades.Any(x => x.Verdict != V2SourceVerdict.Supported))
                problems.Add("source review contains an unresolved, contradicted or missing criterion");
            else if (grades.Any(x => !criteria.ContainsKey(x.CriterionId)) ||
                truth.Criteria.Any(x => !grades.Any(y => y.CriterionId == x.Id)) ||
                grades.Any(x => string.IsNullOrWhiteSpace(x.Reason) ||
                    x.ProofRefs is null || x.ProofRefs.Count == 0))
                problems.Add("source review criterion identity, rationale or proof citations are incomplete");
            // Validate the packet path before constructing or opening any file path.
            if (!SafeReviewFile(reviewedCase.PacketFile) ||
                reviewedCase.PacketFile != reviewedCase.CaseId + ".txt")
                problems.Add("source review packet filename is unsafe");
            else
            {
                var packetPath = Path.Combine(review, reviewedCase.PacketFile);
                var instructionPath = Path.Combine(review, "INSTRUCTIONS.txt");
                if (!File.Exists(packetPath) ||
                    Hash(File.ReadAllBytes(packetPath)) != reviewedCase.PacketSha256 ||
                    !File.Exists(instructionPath) ||
                    Hash(File.ReadAllBytes(instructionPath)) != manifest.InstructionsSha256)
                    problems.Add("source review packet or instructions changed after import");
            }
        }
        if (problems.Count > 0) return Reject(problems, log);
        // Preflight input roots must never become output destinations.
        var outPath = Path.GetFullPath(outputFile);
        if (Inside(outPath, root) || Inside(outPath, sources) || Inside(outPath, review) ||
            string.Equals(outPath, Path.GetFullPath(planFile), StringComparison.OrdinalIgnoreCase) ||
            File.Exists(outPath))
            return Reject(["output path overlaps a source/review input or already exists"], log);
        var sourceCheck = V2SourceReview.Preflight(root, sources);
        problems.AddRange(sourceCheck.Errors);
        if (problems.Count > 0 || sourceCheck.CaseCount == 0) return Reject(
            problems.Count == 0 ? ["empty source preflight"] : problems, log);
        var sourcePath = Path.Combine(sources, plan.CaseId, "after");
        var scratch = Path.Combine(Path.GetTempPath(), "code-archeologist-v2-pr3a-" +
            Guid.NewGuid().ToString("N"));
        var original = Environment.GetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR");
        Microsoft.Extensions.DependencyInjection.ServiceProvider? services = null;
        try
        {
            var (serviceProvider, _, _, _, snapshot, graph) =
                await LabHarness.CaptureSourceAsync(sourcePath, Path.Combine(scratch, "data"),
                    "V2 PR3a " + plan.CaseId, ct);
            services = serviceProvider;
            if (snapshot.FingerprintSha256 != item.After.SnapshotSha256)
            {
                log.WriteLine("RED: captured host snapshot fingerprint differs from the V2 case.");
                return 1;
            }
            var matches = graph.Nodes.Where(x => x.Kind == plan.EntryKind &&
                x.FilePath == plan.EntryPath && x.Line == plan.EntryLine).ToArray();
            if (matches.Length != 1)
            {
                log.WriteLine("RED: entry-point selector did not resolve to one host-evidence node.");
                return 1;
            }
            var scope = new GateCScope(snapshot.WorkspaceId, snapshot.RepositoryId,
                snapshot.Id, snapshot.FingerprintSha256, matches[0].Id,
                SnapshotEvidenceTools.Version);
            var discovery = services.GetRequiredService<GateCEvidenceDiscovery>();
            var views = new List<HostView>();
            // Every possible 1..3 view combination gets a FRESH role-local host session.
            // Never combine handles from distinct sessions or infer retrieval from a label.
            for (var size = 1; size <= GateC3IndependentRunner.MaxViewsPerRole; size++)
                foreach (var subset in Choose(FixedCapabilities, size))
                {
                    var session = discovery.OpenVerified(snapshot, scope, Guid.NewGuid(), plan.Role, ct);
                    var responses = new List<GateCDiscoveryResponse>();
                    var accepted = true;
                    foreach (var capability in subset)
                    {
                        var issued = session.Request(capability, ct);
                        if (!issued.Check.Accepted || issued.Response is null)
                        {
                            accepted = false;
                            break;
                        }
                        responses.Add(issued.Response);
                    }
                    if (!accepted) continue;
                    var merged = GateC3IndependentRunner.MergeViews(responses, session.ToolCallsUsed);
                    var promptView = GateC3IndependentRunner.ComposeEvidenceView(
                        merged, session.SourceExcerpt, session.SourceExcerptTruncated);
                    views.Add(new(subset, session.ReportLinks(),
                        merged.Truncated || session.SourceExcerptTruncated,
                        promptView.Length <= GateC3IndependentRunner.MaxInputText));
                }
            var outcomes = Classify(truth, plan, views);
            // Bind inputs and host versions into one immutable pre-model output.
            var planHash = Hash(File.ReadAllBytes(planFile));
            var digest = Hash(Encoding.UTF8.GetBytes(string.Join("\n",
                "v2-answerability-digest-v1", caseHash, truthHash, importHash,
                planHash, snapshot.FingerprintSha256,
                GateCEvidenceRegistry.Version, SnapshotEvidenceTools.Version,
                JsonSerializer.Serialize(outcomes, V2Schemas.Json))));
            var answerability = new V2Answerability(V2Schemas.Answerability,
                plan.CaseId, plan.Comparator, digest, outcomes);
            var record = new Record(RecordSchema, caseHash, truthHash, planHash,
                importHash, snapshot.FingerprintSha256, GateCEvidenceRegistry.Version,
                SnapshotEvidenceTools.Version, answerability, "not-ready-for-scoring");
            // Never overwrite or mutate input material; missing output is a failed preflight.
            using var dest = new FileStream(outputFile, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, V2Schemas.Json);
            dest.Write(bytes);
            dest.Flush(flushToDisk: true);
            log.WriteLine($"GREEN: V2 PR3a preflight completed for {plan.CaseId} / {plan.Role}; " +
                $"{outcomes.Count} criteria; NOT READY FOR SCORING.");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.WriteLine("RED: V2 PR3a host preflight rejected: " + error.Message);
            return 1;
        }
        finally
        {
            if (services is not null) await services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("CODE_ARCHEOLOGIST_DATA_DIR", original);
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { log.WriteLine("WARNING: disposable V2 preflight cleanup failed."); }
            catch (UnauthorizedAccessException) { log.WriteLine("WARNING: disposable V2 preflight cleanup denied."); }
        }
    }

    private static IEnumerable<string[]> Choose(string[] source, int size)
    {
        for (var i = 0; i < (1 << source.Length); i++)
        {
            var chosen = Enumerable.Range(0, source.Length)
                .Where(bit => (i & (1 << bit)) != 0).Select(bit => source[bit]).ToArray();
            if (chosen.Length == size) yield return chosen;
        }
    }

    private static bool Inside(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SafeReviewFile(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.EndsWith(".txt", StringComparison.Ordinal) &&
        Path.GetFileName(value) == value && !value.Contains('\\') && !value.Contains(':');

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static int Reject(IReadOnlyList<string> errors, TextWriter log)
    {
        foreach (var error in errors) log.WriteLine("V2 answerability error: " + error);
        log.WriteLine("RED: V2 answerability preflight rejected; NOT READY FOR SCORING.");
        return 1;
    }
}
