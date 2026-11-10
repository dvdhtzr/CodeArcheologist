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

        foreach (var loaded in corpus.Cases.Where(c => c.Case.Category == CaseCategory.Fixture))
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

        foreach (var loaded in corpus.Cases.Where(c => c.Case.Category == CaseCategory.ChangeExperiment))
        {
            await CheckAsync($"{loaded.Case.Id}: before/after delta and one-hop impact match the declared impact", async () =>
            {
                await using var captured = await LabHarness.CaptureAsync(loaded, CancellationToken.None);
                Expect(LabHarness.UnsatisfiableAnchors(captured).Count == 0, "after-version anchors unsatisfiable");
                var change = await LabHarness.CheckChangeAsync(captured, CancellationToken.None);
                Expect(change.Confirmed, change.Detail);
                Expect(LabScoring.Score(LabHarness.Baseline(captured, 1), captured).IntegrityBreaches.Count == 0, "baseline breach");
                if (loaded.Case.Id == "C03-rename-helper")
                {
                    // Negative: a flipped impact declaration must be detected, not absorbed.
                    var flipped = loaded with { Case = loaded.Case with { Change = loaded.Case.Change! with { Impact = "unaffected" } } };
                    await using var again = await LabHarness.CaptureAsync(flipped, CancellationToken.None);
                    Expect(!(await LabHarness.CheckChangeAsync(again, CancellationToken.None)).Confirmed,
                        "flipped impact declaration was accepted");
                }
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

        await CheckAsync("UTF-8 BOM project and source files capture (regression)", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "code-archeologist-lab-bom-" + Guid.NewGuid().ToString("N"));
            var src = Path.Combine(root, "source");
            Directory.CreateDirectory(src);
            var bom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(Path.Combine(src, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", bom);
            File.WriteAllText(Path.Combine(src, "Controller.cs"),
                "namespace Bom;\n[Route(\"api/x\")]\npublic class XController\n{\n    [HttpGet]\n    public int Get() => Help();\n    private int Help() => 1;\n}\n", bom);
            Expect(File.ReadAllBytes(Path.Combine(src, "App.csproj"))[0] == 0xEF, "fixture lacks a BOM");
            try
            {
                var (services, _, _, _, _, graph) = await LabHarness.CaptureSourceAsync(src,
                    Path.Combine(root, "data"), "BOM regression", CancellationToken.None);
                await using var _ = services;
                var entry = graph.Nodes.SingleOrDefault(n => n.Kind == "http-endpoint-attribute");
                Expect(entry is { Line: 5 }, "BOM-prefixed source lost its HTTP candidate or shifted lines");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(root, recursive: true);
            }
        });

        await CuratedMachineryAsync(evalRoot);

        await CheckAsync("owner tools: label review writes approval only on 'a', and blinded judging reveals counts", async () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), "code-archeologist-lab-owner-" + Guid.NewGuid().ToString("N"));
            try
            {
                Copy(evalRoot, Path.Combine(temp, "eval"));
                var copy = Path.Combine(temp, "eval");
                // Start from all-draft labels so the review path is exercised whatever the corpus state.
                static string Unsign(string t) => t.Replace("\"status\": \"owner-approved\"", "\"status\": \"draft\"")
                    .Replace("\"authoredBy\": \"assistant-draft-owner-reviewed\"", "\"authoredBy\": \"assistant-draft\"");
                foreach (var f in Directory.GetFiles(Path.Combine(copy, "cases"), "labels.json", SearchOption.AllDirectories))
                    File.WriteAllText(f, Unsign(File.ReadAllText(f)));
                var output = new StringWriter();
                Expect(LabOwnerTools.ReviewLabels(copy, null, new StringReader("x\na\ns\nq\n"), output) == 0, "review failed: " + output);
                var after = CorpusValidator.Validate(copy);
                Expect(after.Valid && after.ApprovedLabels == 1 && after.DraftLabels == after.Cases.Count - 1,
                    "review must approve exactly the one case answered 'a'");
                var first = after.Cases.OrderBy(c => c.Case.Id, StringComparer.Ordinal)
                    .First(c => c.Labels.Status == LabelStatus.OwnerApproved);
                Expect(first.Labels.AuthoredBy == "assistant-draft-owner-reviewed", "approval provenance missing");
                var originalText = Unsign(File.ReadAllText(Path.Combine(evalRoot, "cases", Path.GetFileName(first.Directory), "labels.json")));
                var signedText = File.ReadAllText(Path.Combine(first.Directory, "labels.json"));
                Expect(signedText == originalText
                        .Replace("\"status\": \"draft\"", "\"status\": \"owner-approved\"")
                        .Replace("\"authoredBy\": \"assistant-draft\"", "\"authoredBy\": \"assistant-draft-owner-reviewed\""),
                    "sign-off must change only the status and provenance values");
                // Line endings and escaping are preserved byte for byte (no CRLF or \u002B rewrite).
                var crlf = originalText.Replace("\n", "\r\n").Replace("\"description\": \"", "\"description\": \"a + b ");
                var signedLabels = JsonSerializer.Deserialize<LabLabels>(crlf, LabSchema.Json)! with
                    { Status = LabelStatus.OwnerApproved, AuthoredBy = "assistant-draft-owner-reviewed" };
                Expect(LabOwnerTools.TrySign(crlf, signedLabels, out var crlfSigned) && crlfSigned.Contains("a + b") &&
                       crlfSigned.Count(ch => ch == '\r') == crlf.Count(ch => ch == '\r'), "sign-off rewrote line endings or escaping");
                Expect(!LabOwnerTools.TrySign(originalText + originalText, signedLabels, out _), "ambiguous files must be refused");

                var runs = Path.Combine(temp, "runs");
                Directory.CreateDirectory(runs);
                LabReport Report(string caseId, string comparator, string text) => new(LabSchema.Report, caseId, comparator, 1,
                    "completed", "d", Guid.NewGuid(), new string('a', 64), "e", [new("observation", "x", text, [])], null, 1);
                foreach (var (c, cmp, text) in new[] { ("K1", Comparator.GateB, "source-observation: plain"),
                    ("K1", Comparator.GateC, "H1 ContestedOrInsufficient: richer"), ("K2", Comparator.GateB, "b2"), ("K2", Comparator.GateC, "c2") })
                    File.WriteAllText(Path.Combine(runs, $"{c}.{cmp}.t1.report.json"), JsonSerializer.Serialize(Report(c, cmp, text), LabSchema.Json));
                var judged = new StringWriter();
                var path = Path.Combine(temp, "judgments.json");
                LabOwnerTools.Judge(runs, Comparator.GateB, Comparator.GateC, path, new StringReader("l\ny\nn\nt\nn\nn\n"), judged, seed: 7);
                var shown = judged.ToString();
                Expect(!shown.Contains("source-observation:") && !shown.Contains("ContestedOrInsufficient:"),
                    "comparator-revealing prefixes were shown");
                Expect(!shown[..shown.IndexOf("=== REVEAL", StringComparison.Ordinal)].Contains("gate-"), "comparator names shown before reveal");
                var file = JsonSerializer.Deserialize<LabOwnerTools.JudgmentFile>(File.ReadAllText(path), LabSchema.Json)!;
                Expect(file.Judgments.Count == 2 && file.Judgments.Count(j => j.Preferred == "tie") == 1, "judgments not recorded");
                Expect(shown.Contains("Pairs judged: 2") && shown.Contains("blinding is partial"), "reveal incomplete");
                // Resumable: a second session skips already judged cases.
                var again = new StringWriter();
                LabOwnerTools.Judge(runs, Comparator.GateB, Comparator.GateC, path, new StringReader(""), again, seed: 7);
                Expect(again.ToString().Contains("Pairs judged: 2") && !again.ToString().Contains("Case 3"), "judging is not resumable");
            }
            finally { Directory.Delete(temp, recursive: true); }
        });

        Check("repository corpus unmodified by lab runs", () =>
            Expect(TreeDigest(evalRoot) == before, "eval/ tree changed during self-test"));
        Console.WriteLine(_failed == 0
            ? $"GREEN: C6c lab self-test ({_passed} checks): corpus validation, isolated capture of {corpus.Cases.Count(c => c.Case.Category == CaseCategory.Fixture)} fixtures and {corpus.Cases.Count(c => c.Case.Category == CaseCategory.ChangeExperiment)} change experiments, curated pinning/tamper/expectation machinery, satisfiable anchors, baseline, production Gate C path (model-free roles), integrity-breach detection, fixed denominators, non-modification."
            : $"RED: {_failed} lab self-test check(s) failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static int _passed, _failed;

    /// <summary>
    /// Curated-case machinery without network or real repositories: a
    /// synthetic checkout built from a fixture is pinned in a temp corpus copy,
    /// then verified, tampered and rejected.
    /// </summary>
    private static async Task CuratedMachineryAsync(string evalRoot)
    {
        var temp = Path.Combine(Path.GetTempPath(), "code-archeologist-lab-curated-" + Guid.NewGuid().ToString("N"));
        var corpusCopy = Path.Combine(temp, "eval");
        var curatedRoot = Path.Combine(temp, "checkouts");
        try
        {
            Copy(evalRoot, corpusCopy);
            var checkout = Path.Combine(curatedRoot, "R99");
            Copy(Path.Combine(evalRoot, "cases", "F01-attribute-private-helper", "source"), Path.Combine(checkout, "app"));
            Copy(Path.Combine(evalRoot, "cases", "F12-no-invocations", "source"), Path.Combine(checkout, "app2"));
            File.WriteAllText(Path.Combine(checkout, "LICENSE"), "MIT License (synthetic test)");
            File.Delete(Path.Combine(checkout, "app2", "Controller.cs"));
            File.WriteAllText(Path.Combine(checkout, "app2", "Plain.cs"), "namespace P; public class Plain { public int X() => 1; }");
            var pinnedLines = new Dictionary<string, IReadOnlyList<string>>();
            async Task<(string Fingerprint, int Files)> Pin(string sub)
            {
                var (services, _, _, _, snap, _) = await LabHarness.CaptureSourceAsync(Path.Combine(checkout, sub),
                    Path.Combine(temp, "pin-" + Guid.NewGuid().ToString("N")), "pin", CancellationToken.None);
                pinnedLines[sub] = LabHarness.ManifestLines(Microsoft.Extensions.DependencyInjection
                    .ServiceProviderServiceExtensions.GetRequiredService<CodeArcheologist.Api.Workspaces.SnapshotCaptureService>(services)
                    .ReadManifest(snap));
                await services.DisposeAsync();
                return (snap.FingerprintSha256, snap.FileCount);
            }
            var app = await Pin("app");
            var plain = await Pin("app2");
            var licenceSha = LabSchema.Sha256(File.ReadAllBytes(Path.Combine(checkout, "LICENSE")));
            CuratedRepository Repo(string id, string sub, string group, (string Fingerprint, int Files) pin) => new(id,
                "example/synthetic", "https://github.com/example/synthetic", new string('a', 40), "MIT", "LICENSE",
                licenceSha, sub, pin.Fingerprint, pin.Files, group, "synthetic self-test repository");
            var manifest = new CuratedManifest(LabSchema.Curated, [Repo("R99", "app", "attribute-controllers", app)]);
            var manifestPath = Path.Combine(corpusCopy, "curated", "repositories.json");
            File.WriteAllLines(Path.Combine(corpusCopy, "curated", "manifests", "R99.tsv"), pinnedLines["app"]);
            var real = JsonSerializer.Deserialize<CuratedManifest>(File.ReadAllText(manifestPath), LabSchema.Json)!;
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(real with
                { Repositories = [.. real.Repositories, .. manifest.Repositories] }, LabSchema.Json));
            var caseDir = Path.Combine(corpusCopy, "cases", "R99-synthetic-checkout");
            Directory.CreateDirectory(caseDir);
            File.WriteAllText(Path.Combine(caseDir, "case.json"), JsonSerializer.Serialize(new LabCase(LabSchema.Case,
                "R99-synthetic-checkout", CaseCategory.Curated, "Synthetic", "Self-test",
                new CaseSource("curated-checkout", "", new Dictionary<string, string>(), "R99"),
                new EntryPointSelector("http-endpoint-attribute", "Controller.cs", 6), false), LabSchema.Json));
            File.WriteAllText(Path.Combine(caseDir, "labels.json"), JsonSerializer.Serialize(new LabLabels(LabSchema.Labels,
                "R99-synthetic-checkout", LabelStatus.Draft, "assistant-draft",
                [new("O1", "Get invokes Format.", new("Controller.cs", 9))], [new("U1", "Provisional callee.")], [], []),
                LabSchema.Json));

            await CheckAsync("curated: pinned synthetic checkout validates and captures", async () =>
            {
                var report = CorpusValidator.Validate(corpusCopy);
                Expect(report.Valid, "synthetic curated corpus invalid: " + string.Join("; ", report.Errors));
                var loaded = report.Cases.Single(c => c.Case.Id == "R99-synthetic-checkout");
                await using var captured = await LabHarness.CaptureAsync(loaded, CancellationToken.None, curatedRoot);
                Expect(LabHarness.UnsatisfiableAnchors(captured).Count == 0, "anchors unsatisfiable");
                Expect(LabScoring.Score(LabHarness.Baseline(captured, 1), captured).IntegrityBreaches.Count == 0, "breach");
                Expect(File.Exists(Path.Combine(checkout, "app", "Controller.cs")), "checkout was modified or removed");
            });
            await CheckAsync("curated: missing root, tampered licence and tampered bytes fail closed", async () =>
            {
                var loaded = CorpusValidator.Validate(corpusCopy).Cases.Single(c => c.Case.Id == "R99-synthetic-checkout");
                await ExpectLabFailure(() => LabHarness.CaptureAsync(loaded, CancellationToken.None, null), "not acquired");
                var licence = Path.Combine(checkout, "LICENSE");
                var original = File.ReadAllText(licence);
                File.WriteAllText(licence, original + " changed");
                await ExpectLabFailure(() => LabHarness.CaptureAsync(loaded, CancellationToken.None, curatedRoot), "licence");
                File.WriteAllText(licence, original);
                var source = Path.Combine(checkout, "app", "Controller.cs");
                var bytes = File.ReadAllText(source);
                File.WriteAllText(source, bytes.Replace("\n", "\r\n"));
                await ExpectLabFailure(() => LabHarness.CaptureAsync(loaded, CancellationToken.None, curatedRoot),
                    "changed file Controller.cs");
                File.WriteAllText(source, bytes);
            });
            await CheckAsync("curated: no-supported-candidate expectation confirms and detects violations", async () =>
            {
                var none = new LoadedCase(new LabCase(LabSchema.Case, "R98-x", CaseCategory.Curated, "t", "p",
                        new CaseSource("curated-checkout", "", new Dictionary<string, string>(), "R99"), null, false,
                        CaseExpectation.NoSupportedCandidate),
                    new LabLabels(LabSchema.Labels, "R98-x", LabelStatus.Draft, "assistant-draft", [], [new("U1", "u")], [], []),
                    caseDir, Repo("R99", "app2", "unsupported-convention", plain));
                var ok = await LabHarness.CheckExpectationAsync(none, curatedRoot, CancellationToken.None);
                Expect(ok.Confirmed, "zero-candidate checkout not confirmed: " + ok.Detail);
                var wrong = none with { Curated = Repo("R99", "app", "unsupported-convention", app) };
                var bad = await LabHarness.CheckExpectationAsync(wrong, curatedRoot, CancellationToken.None);
                Expect(!bad.Confirmed && bad.Detail.Contains("unexpected candidates"), "candidate presence not detected");
            });
            await CheckAsync("curated: capture-limit expectation fails closed with no snapshot record", async () =>
            {
                var big = Path.Combine(checkout, "big");
                Directory.CreateDirectory(big);
                await using (var f = File.Create(Path.Combine(big, "Huge.cs")))
                    f.SetLength(8L * 1024 * 1024 + 1);
                var limit = new LoadedCase(new LabCase(LabSchema.Case, "R96-x", CaseCategory.Curated, "t", "p",
                        new CaseSource("curated-checkout", "", new Dictionary<string, string>(), "R99"), null, false,
                        CaseExpectation.CaptureLimitFailClosed),
                    new LabLabels(LabSchema.Labels, "R96-x", LabelStatus.Draft, "assistant-draft", [], [new("U1", "u")], [], []),
                    caseDir, Repo("R99", "big", "capture-limit", ("", 0)));
                var result = await LabHarness.CheckExpectationAsync(limit, curatedRoot, CancellationToken.None);
                Expect(result.Confirmed, "limit case not confirmed: " + result.Detail);
                var small = limit with { Curated = Repo("R99", "app2", "capture-limit", ("", 0)) };
                var violated = await LabHarness.CheckExpectationAsync(small, curatedRoot, CancellationToken.None);
                Expect(!violated.Confirmed && violated.Detail.Contains("unexpectedly succeeded"), "success not flagged");
                Directory.Delete(big, recursive: true);
            });
            void Mutated(string name, Func<CuratedManifest, CuratedManifest> edit, string fragment) =>
                Check("curated manifest rejects " + name, () =>
                {
                    var current = JsonSerializer.Deserialize<CuratedManifest>(File.ReadAllText(manifestPath), LabSchema.Json)!;
                    var saved = File.ReadAllText(manifestPath);
                    try
                    {
                        File.WriteAllText(manifestPath, JsonSerializer.Serialize(edit(current), LabSchema.Json));
                        var errors = CorpusValidator.Validate(corpusCopy).Errors;
                        Expect(errors.Any(e => e.Contains(fragment, StringComparison.Ordinal)),
                            $"expected '{fragment}', got [{string.Join("; ", errors)}]");
                    }
                    finally { File.WriteAllText(manifestPath, saved); }
                });
            CuratedManifest Edit(CuratedManifest m, Func<CuratedRepository, CuratedRepository> f) =>
                m with { Repositories = m.Repositories.Select(r => r.Id == "R99" ? f(r) : r).ToArray() };
            Mutated("an abbreviated commit SHA", m => Edit(m, r => r with { CommitSha = "abc123" }), "full 40-hex SHA");
            Mutated("a non-permissive licence", m => Edit(m, r => r with { License = "GPL-3.0" }), "approved permissive licence");
            Mutated("a traversal subtree", m => Edit(m, r => r with { Subtree = "../outside" }), "safe relative path");
            Mutated("an unpinned fingerprint", m => Edit(m, r => r with { SnapshotFingerprint = "" }), "fingerprint must be pinned");
            Mutated("a mismatched URL", m => Edit(m, r => r with { Url = "https://example.com/x" }), "url must be");
            Mutated("a group/expectation mismatch", m => Edit(m, r => r with { Group = "unsupported-convention" }),
                "expectation must be no-supported-candidate");
            Check("curated validator rejects a per-file manifest that does not reproduce the fingerprint", () =>
            {
                var tsv = Path.Combine(corpusCopy, "curated", "manifests", "R99.tsv");
                var saved = File.ReadAllText(tsv);
                try
                {
                    File.AppendAllText(tsv, "file\tExtra.cs\t1\t" + new string('0', 64) + "\n");
                    Expect(CorpusValidator.Validate(corpusCopy).Errors.Any(e => e.Contains("does not reproduce")),
                        "tampered per-file manifest accepted");
                }
                finally { File.WriteAllText(tsv, saved); }
            });
            Check("curated validator rejects a case-insensitive path collision", () =>
            {
                var tsv = Path.Combine(corpusCopy, "curated", "manifests", "R99.tsv");
                var saved = File.ReadAllText(tsv);
                try
                {
                    File.AppendAllText(tsv, "file\tDocs/a.cs\t1\t" + new string('0', 64) + "\n" +
                        "file\tdocs/b.cs\t1\t" + new string('0', 64) + "\n");
                    Expect(CorpusValidator.Validate(corpusCopy).Errors.Any(e => e.Contains("case-insensitive path collision")),
                        "case collision accepted");
                }
                finally { File.WriteAllText(tsv, saved); }
            });
            Mutated("a repository without a case", m => m with { Repositories = [.. m.Repositories,
                m.Repositories.Single(r => r.Id == "R99") with { Id = "R97" }] }, "R97 has no case");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task ExpectLabFailure(Func<Task<CapturedCase>> capture, string fragment)
    {
        try
        {
            await using var unexpected = await capture();
        }
        catch (LabCaseException error) when (error.Message.Contains(fragment, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("expected a fail-closed lab error containing: " + fragment);
    }

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
        Mutate("approval without owner review", t => Json(t, first + "/labels.json", n => { n["status"] = "owner-approved"; n["authoredBy"] = "assistant-draft"; }),
            "approved labels must record owner review");
        Mutate("anchor outside pinned files", t => Json(t, first + "/labels.json",
            n => n["requiredObservations"]![0]!["anchor"]!["filePath"] = "../secrets.cs"), "anchor is not in a pinned fixture file");
        Mutate("unknown forbidden-claim category", t => Json(t, first + "/labels.json",
            n => n["forbiddenClaims"]![0]!["category"] = "vibes"), "unknown forbidden-claim category");
        Mutate("unknown JSON member", t => Json(t, first + "/case.json", n => n["extra"] = true), "unreadable or invalid");
        Mutate("unsupported case category", t => Json(t, first + "/case.json", n => n["category"] = "held-out"),
            "only fixture, curated and change-experiment cases are supported");
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
