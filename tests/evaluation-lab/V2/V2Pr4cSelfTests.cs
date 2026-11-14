using System.IO.Compression;
using System.Text.Json;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>
/// PR4c test reuses the real PR2→PR4a→PR4b disposable fixture, so that
/// adjudication is proven against actually frozen first-write judge records.
/// </summary>
public static class V2Pr4cSelfTests
{
    public static int Run() => V2Pr4bSelfTests.Run(withAdjudication: true);

    internal static void Exercise(string batchDir, string temp)
    {
        static void Check(bool yes, string why)
        {
            if (!yes) throw new InvalidOperationException(why);
        }
        var output = Path.Combine(temp, "pr4c-adjudication");
        using var log = new StringWriter();
        Check(V2Pr4cExport.Export(batchDir, output, log) == 0,
            "Valid PR4c census rejected: " + log);
        var census = V2Pr4cExport.ReadVerified(batchDir, output);
        var batch = V2Pr4bBatch.ReadVerifiedBatch(batchDir);
        var officialId = batch.Packets.Single(x => x.Origin == "official").PacketId;
        var calibrationId = batch.Packets.Single(x => x.Origin == "calibration").PacketId;
        Check(census.Disputes.Count == 3 &&
            census.Disputes.Count(x => x.PacketId == officialId) == 2 &&
            census.Disputes.Count(x => x.PacketId == calibrationId) == 1 &&
            census.Disputes.Any(x => x.Kind == V2Pr4cItemKind.Criterion &&
                x.Dimensions.Contains("grade")) &&
            census.Disputes.Any(x => x.Kind == V2Pr4cItemKind.Claim &&
                x.Dimensions.Contains("materiality")),
            "Complete official/calibration criterion/claim disagreement census incorrect.");
        Check(census.CriticalConsensusFlags.Count == 1 &&
            census.CriticalConsensusFlags[0].PacketId == calibrationId &&
            census.CriticalConsensusFlags[0].ReviewStatus.Contains("unreviewed",
                StringComparison.Ordinal),
            "Critical agreement was silently promoted into semantic truth.");
        Check(census.CalibrationDifferencesA.Count == 0 &&
            census.CalibrationDifferencesB.Count == 1 &&
            census.ReasonVariations.All(x =>
                x.Status.Contains("unclassified", StringComparison.Ordinal)),
            "Private calibration differences or unclassified reason variance were lost.");
        using (var zip = ZipFile.OpenRead(Path.Combine(output,
            "upload-these-to-adjudicator.zip")))
        {
            Check(zip.Entries.Count == census.PublicPackets.Count + 1 &&
                zip.Entries.All(e =>
                    !e.FullName.Contains("DO-NOT-UPLOAD", StringComparison.Ordinal) &&
                    !e.FullName.Contains("private", StringComparison.OrdinalIgnoreCase)),
                "Shareable adjudication ZIP includes private source or judge identities.");
        }
        foreach (var p in census.PublicPackets)
        {
            var publicPacket = File.ReadAllText(Path.Combine(output,
                V2Pr4cSchemas.UploadFolder, p.PacketFile));
            Check(publicPacket.Contains("Opinion First", StringComparison.Ordinal) &&
                publicPacket.Contains("Opinion Second", StringComparison.Ordinal) &&
                publicPacket.Contains("FROZEN SOURCE-FIRST", StringComparison.Ordinal) &&
                !publicPacket.Contains("Origin: calibration", StringComparison.Ordinal) &&
                !publicPacket.Contains("Judge A", StringComparison.Ordinal) &&
                !publicPacket.Contains("Judge B", StringComparison.Ordinal),
                "Adjudication packet lost source-first orientation or exposed judge origin.");
        }
        Check(V2Pr4cExport.Export(batchDir, output, TextWriter.Null) != 0,
            "Second export overwrote frozen PR4c adjudication census.");

        var aPath = Path.Combine(batchDir, V2Pr4bSchemas.RecordA);
        var bPath = Path.Combine(batchDir, V2Pr4bSchemas.RecordB);
        var recordA = V2Pr4bValidation.Read<V2Pr4bJudgeRecord>(aPath);
        var recordB = V2Pr4bValidation.Read<V2Pr4bJudgeRecord>(bPath);
        var aSha = V2Pr4bValidation.ShaFile(aPath);
        var bSha = V2Pr4bValidation.ShaFile(bPath);
        var sourceKey = V2Pr4bValidation.ShaFile(
            Path.Combine(batchDir, V2Pr4bSchemas.KeyFile));
        var aAsB = recordA with
        {
            JudgeSlot = "B",
            JudgeId = "other-declared-judge",
            SessionId = "other-declared-session"
        };
        var identical = V2Pr4cComparison.Build(batch, recordA, aAsB,
            sourceKey, aSha, bSha);
        Check(identical.Disputes.Count == 0 &&
            identical.CriticalFlags.Count == 1,
            "Matching judge values were misrepresented as adjudicated source truth.");
        Check(V2Pr4cComparison.EqualSet(["OBS-01:P1", "OBS-01:P2"],
            ["OBS-01:P2", "OBS-01:P1"]),
            "Proof-set permutation created a false semantic dispute.");
        var reasonOnly = aAsB with
        {
            Submission = aAsB.Submission with
            {
                Assessments = aAsB.Submission.Assessments.Select((x, i) =>
                    i != 0 ? x : x with
                    {
                        Grades = x.Grades.Select((g, j) =>
                            j != 0 ? g : g with
                            {
                                Reason = "Different words, same substantive structured judgment."
                            }).ToArray()
                    }).ToArray()
            }
        };
        var reasonCompared = V2Pr4cComparison.Build(batch, recordA, reasonOnly,
            sourceKey, aSha, bSha);
        Check(reasonCompared.Disputes.Count == 0 &&
            reasonCompared.ReasonVariations.Count >= 1,
            "Reason text changes were incorrectly scored as semantic disagreements.");

        var keySha = V2Pr4bValidation.ShaFile(
            Path.Combine(output, V2Pr4cSchemas.KeyFile));
        var answers = census.Disputes.Select(x =>
        {
            if (x.PacketId == calibrationId)
                return new V2Pr4cDecision(x.DisputeId,
                    V2AdjudicationDisposition.Unresolved,
                    V2Pr4cResolutionSource.None, null, null, null, [], [],
                    "The calibration disagreement remains open until an independent source-label review.");
            if (x.Kind == V2Pr4cItemKind.Criterion)
                return new V2Pr4cDecision(x.DisputeId,
                    V2AdjudicationDisposition.Confirmed,
                    V2Pr4cResolutionSource.First, x.First.Grade,
                    x.First.Grounding, x.First.Materiality,
                    x.First.SourceProofRefs, x.First.ReportClaimRefs,
                    "The pinned return statement directly supports this original opinion.");
            return new V2Pr4cDecision(x.DisputeId,
                V2AdjudicationDisposition.Revised,
                V2Pr4cResolutionSource.Independent, null,
                V2GroundingStatus.Supported, V2Materiality.Contextual,
                x.AllowedProofRefs.Take(1).ToArray(), [],
                "The pinned source establishes the observation, but its materiality needs independent qualification.");
        }).ToArray();
        var submission = new V2Pr4cSubmission(V2Pr4cSchemas.Submission,
            keySha, "third-source-reviewer", "independent-adjudication-session",
            answers);
        var answerFile = Path.Combine(temp, "third-judge.json");
        void Save(V2Pr4cSubmission doc) =>
            File.WriteAllText(answerFile, JsonSerializer.Serialize(doc, V2Schemas.Json));
        int Import() => V2Pr4cAdjudication.Import(batchDir, output, answerFile,
            TextWriter.Null);
        Save(submission);

        // All invalid attempts must fail before accepting an adjudication.
        Save(submission with { Decisions = submission.Decisions.Skip(1).ToArray() });
        Check(Import() != 0, "Missing dispute was accepted.");
        Save(submission with { Decisions = [answers[0], answers[0], .. answers.Skip(2)] });
        Check(Import() != 0, "Duplicated dispute ID was accepted.");
        var forged = answers[0] with { SourceProofRefs = ["FOREIGN:P99"] };
        Save(submission with { Decisions = [forged, .. answers.Skip(1)] });
        Check(Import() != 0, "Invented source-proof reference accepted.");
        var unauthorized = answers.First(x =>
            x.Disposition == V2AdjudicationDisposition.Confirmed);
        Save(submission with
        {
            Decisions = answers.Select(x => x.DisputeId == unauthorized.DisputeId
                ? x with { Grade = 0 } : x).ToArray()
        });
        Check(Import() != 0,
            "Confirmed opinion silently revised without independent disposition.");
        Save(submission with { ReviewerId = recordA.JudgeId });
        Check(Import() != 0,
            "An original judge could adjudicate the same batch by declared ID.");
        Save(submission);
        var recordFile = Path.Combine(output, V2Pr4cSchemas.RecordFile);
        Check(!File.Exists(recordFile), "A rejected input left an accepted record.");

        Check(Import() == 0 &&
            V2Pr4cAdjudication.Audit(batchDir, output, TextWriter.Null) == 0,
            "Complete source-grounded third-judge import/audit failed.");
        var recorded = V2Pr4bValidation.Read<V2Pr4cRecord>(recordFile);
        Check(recorded.Submission.Decisions.Count == census.Disputes.Count &&
            recorded.Submission.Decisions.Count(x =>
                x.Disposition == V2AdjudicationDisposition.Unresolved) == 1 &&
            recorded.ContentSha256.Length == 64,
            "Adjudication record hid unresolved items or lost digest.");
        Check(Import() != 0, "A complete adjudication was overwritten.");

        var bytes = File.ReadAllBytes(recordFile);
        try
        {
            var wrong = recorded with
            {
                Submission = recorded.Submission with
                {
                    Decisions = recorded.Submission.Decisions.Select(x =>
                        x.Disposition == V2AdjudicationDisposition.Unresolved
                            ? x with { Explanation = "Unresolved but quietly reworded after import." }
                            : x).ToArray()
                }
            };
            File.WriteAllText(recordFile, JsonSerializer.Serialize(wrong, V2Schemas.Json));
            Check(V2Pr4cAdjudication.Audit(batchDir, output, TextWriter.Null) != 0,
                "Tampered adjudicator wording passed immutable record audit.");
        }
        finally { File.WriteAllBytes(recordFile, bytes); }

        var originalCensus = Path.Combine(output, V2Pr4cSchemas.KeyFile);
        var sourceCensus = File.ReadAllBytes(originalCensus);
        try
        {
            File.AppendAllText(originalCensus, "MUTATED");
            Check(V2Pr4cAdjudication.Audit(batchDir, output, TextWriter.Null) != 0,
                "Changed census bytes passed digest-bound audit.");
        }
        finally { File.WriteAllBytes(originalCensus, sourceCensus); }

        var originalA = File.ReadAllBytes(aPath);
        try
        {
            File.AppendAllText(aPath, "MUTATED");
            Check(V2Pr4cAdjudication.Audit(batchDir, output, TextWriter.Null) != 0,
                "Altered original judge A record passed audit.");
        }
        finally { File.WriteAllBytes(aPath, originalA); }
        var zipPath = Path.Combine(output, "upload-these-to-adjudicator.zip");
        var originalZip = File.ReadAllBytes(zipPath);
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            {
                using var member = zip.CreateEntry(
                    "DO-NOT-UPLOAD-pr4c-private-census.json").Open();
                member.Write([1, 2, 3]);
            }
            Check(V2Pr4cAdjudication.Audit(batchDir, output, TextWriter.Null) != 0,
                "Injected private key inside public ZIP passed audit.");
        }
        finally { File.WriteAllBytes(zipPath, originalZip); }
        Check(V2Pr4cAdjudication.Audit(batchDir, output, TextWriter.Null) == 0,
            "Valid restored PR4c chain could not be audited.");
    }
}
