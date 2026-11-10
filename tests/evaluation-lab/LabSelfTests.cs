using System.Text.Json;
using System.Text.Json.Nodes;
using CodeArcheologist.Api.Investigations;

namespace CodeArcheologist.EvaluationLab;

/// <summary>
/// Model-free C6c-1 checks: corpus validation and its negative cases, every
/// fixture captured in isolation with satisfiable anchors, deterministic
/// baseline, the production Gate C path driven by a model-free role fixture,
/// integrity-breach detection on forged reports, and repository non-modification.
/// </summary>
internal static class LabSelfTests
{
    public static async Task<int> RunAsync(string evalRoot)
    {
        var before = TreeDigest(evalRoot);
        Check("real corpus is valid", () => Expect(CorpusValidator.Validate(evalRoot).Valid, "corpus invalid"));
        NegativeCorpusCases(evalRoot);
        var corpus = CorpusValidator.Validate(evalRoot);

        foreach (var loaded in corpus.Cases)
        {
            await CheckAsync($"{loaded.Case.Id}: isolated capture, satisfiable anchors, baseline", async () =>
            {
                string tempRoot;
                await using (var captured = await LabHarness.CaptureAsync(loaded, CancellationToken.None))
                {
                    tempRoot = Path.GetDirectoryName(captured.Store.DataDirectory)!;
                    Expect(captured.Store.DataDirectory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.Ordinal),
                        "lab data directory is not disposable");
                    var bad = LabHarness.UnsatisfiableAnchors(captured);
                    Expect(bad.Count == 0, "unsatisfiable anchors: " + string.Join(",", bad));
                    var score = LabScoring.Score(LabHarness.Baseline(captured, 1), captured);
                    Expect(score.Completed && score.IntegrityBreaches.Count == 0, "baseline breached integrity");
                }
                Expect(!Directory.Exists(tempRoot), "lab temp directory not removed");
            });
        }

        await CheckAsync("scoring detects every integrity breach class", async () =>
        {
            await using var captured = await LabHarness.CaptureAsync(corpus.Cases[0], CancellationToken.None);
            var good = LabHarness.Baseline(captured, 1);
            Expect(LabScoring.Score(good, captured).IntegrityBreaches.Count == 0, "clean report flagged");
            Breach(good with { SnapshotId = Guid.NewGuid() }, captured, IntegrityBreach.ScopeMismatch);
            Breach(good with { EntryPointId = "other" }, captured, IntegrityBreach.ScopeMismatch);
            var forged = good.Claims[0] with { Citations = [good.Claims[0].Citations[0] with { Line = 999 }] };
            Breach(good with { Claims = [forged, .. good.Claims.Skip(1)] }, captured, IntegrityBreach.ForeignCitation);
            var invented = new LabClaim("observation", "x", "invented", [new("not-in-package", "A.cs", null, 1, "candidate")]);
            Breach(good with { Claims = [.. good.Claims, invented] }, captured, IntegrityBreach.ForeignCitation);
            Breach(good with { SchemaVersion = "c6c-lab-report-v0" }, captured, IntegrityBreach.SchemaRegression);
            var facts = new LabGateCFacts(["CompletedValidated", "CompletedValidated", "CompletedValidated"],
                ["S1", "S2"], ["S1", "S2"], false, false, true, 3);
            var gateC = good with { Comparator = Comparator.GateC, GateC = facts };
            var ok = LabScoring.Score(gateC, captured);
            Expect(ok.IntegrityBreaches.Count == 0 && ok.ObjectionCoverage == 1.0, "clean Gate C facts flagged");
            Breach(gateC with { GateC = facts with { AnyVerifiedFact = true } }, captured, IntegrityBreach.FactPromotion);
            Breach(gateC with { GateC = facts with { AnyStructurallySupported = true } }, captured, IntegrityBreach.FactPromotion);
            Breach(gateC with { GateC = facts with { AddressedConcernIds = ["S1"] } }, captured, IntegrityBreach.DiscardedObjection);
            Breach(gateC with { GateC = facts with { AddressedConcernIds = ["S1", "S1", "S2"] } }, captured, IntegrityBreach.DiscardedObjection);
            Breach(gateC with { GateC = facts with { CheckpointCount = 2 } }, captured, IntegrityBreach.FalseCompletion);
            Breach(gateC with { GateC = facts with { RoleStatuses = ["CompletedValidated", "TimedOut", "CompletedValidated"] } },
                captured, IntegrityBreach.FalseCompletion);
            Breach(gateC with { GateC = null }, captured, IntegrityBreach.FalseCompletion);
            var claim = new LabClaim("hypothesis", "investigator", "This endpoint is verified at runtime.", []);
            var flagged = LabScoring.Score(good with { Claims = [.. good.Claims, claim] }, captured);
            Expect(flagged.ReviewFlags.Count == 1 && flagged.IntegrityBreaches.Count == 0,
                "lexical screen must flag for review without scoring a breach");
            Expect(flagged.PendingJudgment.Contains("actionable-usefulness"), "usefulness must stay pending");
            var summary = LabScoring.Summarize([ok, LabScoring.Score(gateC with { Outcome = "interrupted" }, captured)]);
            Expect(summary.Single().Evaluations == 2 && summary.Single().Completed == 1,
                "fixed denominators must include incomplete evaluations");
        });

        foreach (var id in new[] { "F01-attribute-private-helper", "F10-prompt-injection-comment" })
            await CheckAsync($"{id}: production Gate C path (model-free roles) scores cleanly", async () =>
            {
                var loaded = corpus.Cases.Single(c => c.Case.Id == id);
                await using var captured = await LabHarness.CaptureAsync(loaded, CancellationToken.None);
                var report = await LabHarness.GateCAsync(captured, 1, new FixtureRoles(), TimeSpan.FromMinutes(1),
                    CancellationToken.None);
                var score = LabScoring.Score(report, captured);
                Expect(report.Outcome == "completed" && report.GateC?.CheckpointCount == 3,
                    "Gate C fixture run did not complete: " + report.Outcome + "/" + report.Diagnostic);
                Expect(score.IntegrityBreaches.Count == 0, "breaches: " + string.Join(",", score.IntegrityBreaches));
                Expect(score.ObjectionCoverage == 1.0, "objection coverage not complete");
                Expect(score.CitedObservationAnchors >= 1, "no label anchor cited");
                var json = JsonSerializer.Serialize(report, LabSchema.Json);
                Expect(JsonSerializer.Deserialize<LabReport>(json, LabSchema.Json) is not null, "report not round-trippable");
            });

        Check("repository corpus unmodified by lab runs", () =>
            Expect(TreeDigest(evalRoot) == before, "eval/ tree changed during self-test"));
        Console.WriteLine(_failed == 0
            ? $"GREEN: C6c-1 lab self-test ({_passed} checks): corpus validation, isolated capture of {corpus.Cases.Count} fixtures, satisfiable anchors, baseline, production Gate C path (model-free roles), integrity-breach detection, fixed denominators, non-modification."
            : $"RED: {_failed} lab self-test check(s) failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static int _passed, _failed;

    private static void Check(string name, Action body) =>
        CheckAsync(name, () => { body(); return Task.CompletedTask; }).GetAwaiter().GetResult();

    private static async Task CheckAsync(string name, Func<Task> body)
    {
        try { await body(); _passed++; Console.WriteLine("  ok: " + name); }
        catch (Exception error) { _failed++; Console.Error.WriteLine($"  FAILED: {name}: {error.Message}"); }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Breach(LabReport report, CapturedCase captured, string expected)
    {
        var breaches = LabScoring.Score(report, captured).IntegrityBreaches;
        Expect(breaches.Contains(expected), $"expected {expected}, got [{string.Join(",", breaches)}]");
    }

    private static string TreeDigest(string root) => LabSchema.Sha256(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(root, f) + ":" + LabSchema.Sha256(File.ReadAllBytes(f))))));

    /// <summary>Copies the corpus to a temp dir, applies one mutation, expects a specific error.</summary>
    private static void NegativeCorpusCases(string evalRoot)
    {
        void Mutate(string name, Action<string> mutate, string expectedFragment) => Check("corpus rejects " + name, () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), "code-archeologist-lab-corpus-" + Guid.NewGuid().ToString("N"));
            try
            {
                Copy(evalRoot, temp);
                mutate(temp);
                var report = CorpusValidator.Validate(temp);
                Expect(report.Errors.Any(e => e.Contains(expectedFragment, StringComparison.Ordinal)),
                    $"expected '{expectedFragment}', got [{string.Join("; ", report.Errors)}]");
            }
            finally { Directory.Delete(temp, recursive: true); }
        });
        const string first = "cases/F01-attribute-private-helper";
        void Json(string root, string file, Action<JsonNode> edit)
        {
            var path = Path.Combine(root, file);
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            edit(node);
            File.WriteAllText(path, node.ToJsonString());
        }
        Mutate("tampered fixture bytes", t => File.AppendAllText(Path.Combine(t, first, "source/Controller.cs"), "// x"),
            "does not match its pinned SHA-256");
        Mutate("CRLF-converted fixture (diagnosed, not accepted)", t =>
            {
                var f = Path.Combine(t, first, "source/Controller.cs");
                File.WriteAllText(f, File.ReadAllText(f).Replace("\n", "\r\n"));
            }, "line endings were converted to CRLF");
        Mutate("unpinned extra fixture file", t => File.WriteAllText(Path.Combine(t, first, "source/Extra.cs"), "class X {}"),
            "differs from the pinned manifest");
        Mutate("budget that differs from the host", t => Json(t, "lab-config.json", n => n["budgets"]!["roleTimeoutSeconds"] = 120),
            "but the host enforces 90");
        Mutate("nonzero integrity tolerance", t => Json(t, "lab-config.json",
            n => n["provisionalThresholds"]!["allowedIntegrityBreaches"] = 1), "zero-tolerance");
        Mutate("labels of another case", t => Json(t, first + "/labels.json", n => n["caseId"] = "F02-minimal-api-lambda-static"),
            "labels belong to another case");
        Mutate("approval without owner review", t => Json(t, first + "/labels.json", n => n["status"] = "owner-approved"),
            "approved labels must record owner review");
        Mutate("anchor outside pinned files", t => Json(t, first + "/labels.json",
            n => n["requiredObservations"]![0]!["anchor"]!["filePath"] = "../secrets.cs"), "anchor is not in a pinned fixture file");
        Mutate("unknown forbidden-claim category", t => Json(t, first + "/labels.json",
            n => n["forbiddenClaims"]![0]!["category"] = "vibes"), "unknown forbidden-claim category");
        Mutate("unknown JSON member", t => Json(t, first + "/case.json", n => n["extra"] = true), "unreadable or invalid");
        Mutate("unsupported category in C6c-1", t => Json(t, first + "/case.json", n => n["category"] = "held-out"),
            "only fixture cases are supported");
        Mutate("case id that differs from its directory", t => Json(t, first + "/case.json", n => n["id"] = "F99-other-case"),
            "case id must equal directory name");
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>Model-free role fixture: cites the first issued handle; host still validates everything.</summary>
    private sealed class FixtureRoles : IGateCRoleStageRunner
    {
        public Task<GateC3RoleOutcome> RunIndependentAsync(GateCEvidenceSession session, Guid runId,
            GateCScope scope, CancellationToken ct)
        {
            var view = session.Request(GateCEvidenceRegistry.Relationships, ct).Response!;
            var handle = view.Items[0].Handle;
            var role = session.Role;
            var assessment = role == GateCRole.Investigator
                ? new GateCIndependentAssessment(GateCContract.Version, runId, scope, role, [handle],
                    [new GateCHypothesis("H1", "The candidate may route to this method.", [handle], "Not runtime proof")],
                    [], ["Runtime registration unknown"])
                : new GateCIndependentAssessment(GateCContract.Version, runId, scope, role, [handle], [],
                    [new GateCConcern("S1", GateCConcernKind.InsufficientEvidence, "Registration is not evidenced.", [handle])],
                    ["Hosting unknown"]);
            return Task.FromResult(new GateC3RoleOutcome(role, GateCRoleStatus.CompletedValidated, "fixture",
                assessment, GateCEvidenceRegistry.Relationships, session.ToolCallsUsed, session.ReportLinks(),
                session.GraphAnalyzerVersion));
        }

        public Task<GateC4ReviewerOutcome> ReviewAsync(GateCIndependentAssessment investigator,
            GateCIndependentAssessment skeptic, Guid runId, GateCScope scope, CancellationToken ct)
        {
            var review = new GateCReviewerAssessment(GateCContract.Version, runId, scope, GateCRole.Reviewer,
                [new GateCReviewDecision("H1", GateCRecommendation.ContestedOrInsufficient, ["S1"], "Unverified.")], []);
            return Task.FromResult(new GateC4ReviewerOutcome(GateCRoleStatus.CompletedValidated, "fixture", review,
                GateCContractValidator.ValidateReview(review, runId, scope, investigator, skeptic)));
        }
    }
}
