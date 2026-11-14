using System.Text.Json;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>
/// PR4c first-write, source-scoped adjudication. Preserves all PR4b records
/// and every unresolved dispute; never computes a developer task score.
/// </summary>
public static class V2Pr4cAdjudication
{
    private const string Qualification =
        "diagnostic-adjudication-with-original-judgments-and-unresolved-disputes-not-scoreable";

    public static int Import(string batchDir, string exportDir,
        string answerFile, TextWriter log)
    {
        try
        {
            var target = Path.Combine(exportDir, V2Pr4cSchemas.RecordFile);
            V2Pr4bValidation.Require(!File.Exists(target) &&
                !V2Pr4bValidation.Overlap(answerFile,
                    Path.Combine(exportDir, V2Pr4cSchemas.UploadFolder)) &&
                !V2Pr4bValidation.Overlap(answerFile,
                    Path.Combine(batchDir, V2Pr4bSchemas.UploadFolder)),
                "Adjudication is immutable and private; answer may not be uploaded.");
            var census = V2Pr4cExport.ReadVerified(batchDir, exportDir);
            var censusSha = V2Pr4bValidation.ShaFile(
                Path.Combine(exportDir, V2Pr4cSchemas.KeyFile));
            var submission = V2Pr4bValidation.Read<V2Pr4cSubmission>(
                answerFile, 2 * 1024 * 1024);
            var judgeA = V2Pr4bValidation.Read<V2Pr4bJudgeRecord>(
                Path.Combine(batchDir, V2Pr4bSchemas.RecordA), 3 * 1024 * 1024);
            var judgeB = V2Pr4bValidation.Read<V2Pr4bJudgeRecord>(
                Path.Combine(batchDir, V2Pr4bSchemas.RecordB), 3 * 1024 * 1024);
            Validate(census, censusSha, submission, judgeA, judgeB);
            var record = new V2Pr4cRecord(V2Pr4cSchemas.Record,
                censusSha, V2Pr4bValidation.ShaFile(answerFile),
                census.BatchKeySha256, census.JudgeARecordSha256,
                census.JudgeBRecordSha256, submission.ReviewerId,
                submission.SessionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Qualification, submission, "");
            record = record with { ContentSha256 = Digest(record) };
            var pending = target + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(pending, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, record, V2Schemas.Json);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(pending, target); // refuses a pre-existing first record
            }
            finally
            {
                try { if (File.Exists(pending)) File.Delete(pending); }
                catch (IOException) { /* Orphan is not an accepted record. */ }
            }
            var unresolved = submission.Decisions.Count(x =>
                x.Disposition == V2AdjudicationDisposition.Unresolved);
            log.WriteLine($"GREEN: first immutable PR4c adjudication imported: " +
                $"{submission.Decisions.Count} decisions, {unresolved} unresolved. " +
                $"{census.CriticalConsensusFlags.Count} critical consensus flags " +
                "remain unconfirmed. NOT READY FOR SCORING.");
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.WriteLine("RED: PR4c adjudication rejected (" +
                e.GetType().Name + "): " + e.Message);
            log.WriteLine("NOT READY FOR SCORING.");
            return 1;
        }
    }

    public static int Audit(string batchDir, string exportDir, TextWriter log)
    {
        try
        {
            var census = V2Pr4cExport.ReadVerified(batchDir, exportDir);
            var path = Path.Combine(exportDir, V2Pr4cSchemas.RecordFile);
            var record = V2Pr4bValidation.Read<V2Pr4cRecord>(
                path, 3 * 1024 * 1024);
            var a = V2Pr4bValidation.Read<V2Pr4bJudgeRecord>(
                Path.Combine(batchDir, V2Pr4bSchemas.RecordA), 3 * 1024 * 1024);
            var b = V2Pr4bValidation.Read<V2Pr4bJudgeRecord>(
                Path.Combine(batchDir, V2Pr4bSchemas.RecordB), 3 * 1024 * 1024);
            var keySha = V2Pr4bValidation.ShaFile(
                Path.Combine(exportDir, V2Pr4cSchemas.KeyFile));
            V2Pr4bValidation.Require(record.SchemaVersion == V2Pr4cSchemas.Record &&
                record.ContentSha256 == Digest(record) &&
                record.CensusSha256 == keySha &&
                record.BatchKeySha256 == census.BatchKeySha256 &&
                record.JudgeARecordSha256 == census.JudgeARecordSha256 &&
                record.JudgeBRecordSha256 == census.JudgeBRecordSha256 &&
                record.Qualification == Qualification &&
                record.ImportedAtUnixMs > 0 &&
                record.Submission is not null &&
                record.ReviewerId == record.Submission.ReviewerId &&
                record.SessionId == record.Submission.SessionId &&
                V2Pr4bValidation.IsHash(record.SubmissionSha256),
                "Adjudication record was altered or has foreign provenance.");
            Validate(census, keySha, record.Submission, a, b);
            var unresolved = record.Submission.Decisions.Count(x =>
                x.Disposition == V2AdjudicationDisposition.Unresolved);
            log.WriteLine($"GREEN: PR4c fully revalidated {census.Disputes.Count} " +
                $"dispute(s), {unresolved} unresolved; agreement is NOT source truth; " +
                "calibration mismatches and critical consensus retained. NOT READY FOR SCORING.");
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.WriteLine("RED: PR4c full audit rejected (" +
                e.GetType().Name + "): " + e.Message);
            return 1;
        }
    }

    private static string Digest(V2Pr4cRecord record) =>
        V2Pr4bValidation.Sha(JsonSerializer.SerializeToUtf8Bytes(
            record with { ContentSha256 = "" }, V2Schemas.Json));

    private static void Validate(V2Pr4cCensus census, string censusSha,
        V2Pr4cSubmission submission, V2Pr4bJudgeRecord a,
        V2Pr4bJudgeRecord b)
    {
        V2Pr4bValidation.Require(
            submission.SchemaVersion == V2Pr4cSchemas.Submission &&
            submission.CensusSha256 == censusSha &&
            V2Pr4bValidation.IsId(submission.ReviewerId) &&
            V2Pr4bValidation.IsId(submission.SessionId) &&
            !string.Equals(submission.ReviewerId, a.JudgeId,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(submission.ReviewerId, b.JudgeId,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(submission.SessionId, a.SessionId,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(submission.SessionId, b.SessionId,
                StringComparison.OrdinalIgnoreCase) &&
            submission.Decisions is not null &&
            submission.Decisions.Count == census.Disputes.Count,
            "Third judge identity or complete dispute census invalid.");
        var disputes = census.Disputes.ToDictionary(x => x.DisputeId,
            StringComparer.Ordinal);
        V2Pr4bValidation.Require(submission.Decisions.All(x =>
            x is not null && x.DisputeId is not null &&
            disputes.ContainsKey(x.DisputeId)) &&
            submission.Decisions.Select(x => x.DisputeId)
                .Distinct(StringComparer.Ordinal).Count() == disputes.Count,
            "Missing, duplicate or foreign adjudication decision.");
        foreach (var decision in submission.Decisions)
            ValidateDecision(decision, disputes[decision.DisputeId]);
    }

    private static void ValidateDecision(V2Pr4cDecision decision,
        V2Pr4cDispute dispute)
    {
        V2Pr4bValidation.Require(
            !string.IsNullOrWhiteSpace(decision.Explanation) &&
            decision.Explanation.Length is >= 20 and <= 3000 &&
            Enum.IsDefined(decision.Disposition) &&
            Enum.IsDefined(decision.ResolutionSource) &&
            decision.SourceProofRefs is not null &&
            decision.ReportClaimRefs is not null &&
            decision.SourceProofRefs.Count ==
                decision.SourceProofRefs.Distinct(StringComparer.Ordinal).Count() &&
            decision.ReportClaimRefs.Count ==
                decision.ReportClaimRefs.Distinct(StringComparer.Ordinal).Count() &&
            decision.SourceProofRefs.All(dispute.AllowedProofRefs.Contains) &&
            decision.ReportClaimRefs.All(dispute.AllowedClaimRefs.Contains) &&
            (decision.Grade is null or >= 0 and <= 3) &&
            (decision.Grounding is null || Enum.IsDefined(decision.Grounding.Value)) &&
            (decision.Materiality is null || Enum.IsDefined(decision.Materiality.Value)),
            "Adjudicator used foreign proof, invalid grade or unsupported explanation.");
        if (decision.Disposition == V2AdjudicationDisposition.Unresolved)
        {
            V2Pr4bValidation.Require(
                decision.ResolutionSource == V2Pr4cResolutionSource.None &&
                decision.Grade is null && decision.Grounding is null &&
                decision.Materiality is null &&
                decision.ReportClaimRefs.Count == 0,
                "Unresolved must not quietly select or promote a verdict.");
            return; // References to permitted source proofs can explain uncertainty.
        }
        V2Pr4bValidation.Require(
            decision.SourceProofRefs.Count > 0 &&
            decision.Grounding is not null &&
            (dispute.Kind == V2Pr4cItemKind.Criterion
                ? decision.Materiality is null
                : decision.Grade is null && decision.Materiality is not null),
            "Resolved decision needs source proof and type-correct judgment.");
        if (decision.Disposition == V2AdjudicationDisposition.Confirmed)
        {
            V2Pr4bValidation.Require(decision.ResolutionSource is
                    (V2Pr4cResolutionSource.First or V2Pr4cResolutionSource.Second),
                "Confirmed dispute must identify the source-grounded opinion.");
            var chosen = decision.ResolutionSource == V2Pr4cResolutionSource.First
                ? dispute.First : dispute.Second;
            V2Pr4bValidation.Require(decision.Grade == chosen.Grade &&
                decision.Grounding == chosen.Grounding &&
                decision.Materiality == chosen.Materiality &&
                V2Pr4cComparison.EqualSet(decision.SourceProofRefs,
                    chosen.SourceProofRefs) &&
                V2Pr4cComparison.EqualSet(decision.ReportClaimRefs,
                    chosen.ReportClaimRefs),
                "Confirmed disposition cannot silently rewrite a judge's fields.");
        }
        else
            V2Pr4bValidation.Require(
                decision.Disposition == V2AdjudicationDisposition.Revised &&
                decision.ResolutionSource == V2Pr4cResolutionSource.Independent,
                "Revised disposition requires an independently stated resolution.");
        V2Pr4bValidation.Require(
            decision.Grade != 3 || decision.Grounding == V2GroundingStatus.Supported,
            "Full credit cannot be promoted without supported grounding.");
    }
}
