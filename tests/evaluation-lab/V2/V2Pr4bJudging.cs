using System.Text.Json;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>
/// Strict external-judge import. Never sends a prompt, scores a study,
/// combines judge opinions or performs adjudication.
/// </summary>
public static class V2Pr4bJudging
{
    public static int Import(string batchDir, string slot, string answerFile,
        TextWriter log)
    {
        try
        {
            V2Pr4bValidation.Require(slot is ("A" or "B"),
                "Judge slot must be A or B.");
            var target = Path.Combine(batchDir,
                slot == "A" ? V2Pr4bSchemas.RecordA : V2Pr4bSchemas.RecordB);
            V2Pr4bValidation.Require(!File.Exists(target),
                "First complete judgment already recorded for slot " + slot + ".");
            V2Pr4bValidation.Require(!V2Pr4bValidation.Overlap(answerFile,
                Path.Combine(batchDir, V2Pr4bSchemas.UploadFolder)),
                "Never place judges' answers inside the public upload folder.");
            var batch = V2Pr4bBatch.ReadVerifiedBatch(batchDir);
            var keyHash = V2Pr4bValidation.ShaFile(
                Path.Combine(batchDir, V2Pr4bSchemas.KeyFile));
            var submission = V2Pr4bValidation.Read<V2Pr4bSubmission>(answerFile,
                2 * 1024 * 1024);
            V2Pr4bValidation.Require(submission.SchemaVersion == V2Pr4bSchemas.Submission &&
                submission.BatchKeySha256 == keyHash &&
                V2Pr4bValidation.IsId(submission.JudgeId) &&
                V2Pr4bValidation.IsId(submission.SessionId) &&
                submission.Assessments is not null &&
                submission.Assessments.Count == batch.Packets.Count,
                "Submission schema, complete census or batch identity invalid.");
            var scopes = batch.Packets.ToDictionary(x => x.PacketId,
                StringComparer.Ordinal);
            V2Pr4bValidation.Require(submission.Assessments.All(a =>
                a is not null && a.PacketId is not null && scopes.ContainsKey(a.PacketId)) &&
                submission.Assessments.Select(x => x.PacketId)
                    .Distinct(StringComparer.Ordinal).Count() == batch.Packets.Count,
                "Foreign or duplicate packet judgment.");
            foreach (var answer in submission.Assessments)
                V2Pr4bValidation.ValidateAssessment(answer, scopes[answer.PacketId]);

            var other = Path.Combine(batchDir,
                slot == "A" ? V2Pr4bSchemas.RecordB : V2Pr4bSchemas.RecordA);
            if (File.Exists(other))
            {
                var earlier = V2Pr4bValidation.Read<V2Pr4bJudgeRecord>(other,
                    3 * 1024 * 1024);
                VerifyRecord(batchDir, batch, earlier, other);
                V2Pr4bValidation.Require(!string.Equals(earlier.JudgeId,
                    submission.JudgeId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(earlier.SessionId, submission.SessionId,
                        StringComparison.OrdinalIgnoreCase),
                    "Independent judge passes require distinct declared judge/session IDs.");
            }
            var results = new List<V2Pr4bCalibrationDifference>();
            var total = 0;
            var agreement = 0;
            var answers = submission.Assessments.ToDictionary(x => x.PacketId,
                StringComparer.Ordinal);
            foreach (var expected in batch.ExpectedCalibration)
            {
                var actual = answers[expected.PacketId];
                var grades = actual.Grades.ToDictionary(x => x.CriterionId,
                    StringComparer.Ordinal);
                foreach (var correct in expected.Grades)
                {
                    total++;
                    var given = grades[correct.CriterionId];
                    if (given.Grade == correct.Grade &&
                        given.Grounding == correct.Grounding)
                        agreement++;
                    else results.Add(new(expected.PacketId, correct.CriterionId,
                        "Criterion grade or grounding differs from frozen calibration."));
                }
                var claims = actual.Claims.ToDictionary(x => x.ClaimId,
                    StringComparer.Ordinal);
                foreach (var correct in expected.Claims)
                {
                    total++;
                    var given = claims[correct.ClaimId];
                    if (given.Grounding == correct.Grounding &&
                        given.Materiality == correct.Materiality)
                        agreement++;
                    else results.Add(new(expected.PacketId, correct.ClaimId,
                        "Claim grounding or materiality differs from frozen calibration."));
                }
            }
            V2Pr4bValidation.Require(total > 0, "Calibration decisions are missing.");
            var record = new V2Pr4bJudgeRecord(V2Pr4bSchemas.Record, keyHash,
                V2Pr4bValidation.ShaFile(answerFile), slot, submission.JudgeId,
                submission.SessionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                total, agreement, results,
                "diagnostic-only-calibration-disclosed-not-qualified-for-scoring",
                submission);
            // Do not write ANY record until packet identity, both complete censuses,
            // original judge identity, and the frozen calibration key pass checks.
            using var stream = new FileStream(target, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, record, V2Schemas.Json);
            stream.Flush(flushToDisk: true);
            log.WriteLine("GREEN: first complete V2 PR4b judgment recorded for slot " +
                slot + ", " + batch.Packets.Count + " packets.");
            log.WriteLine("Calibration matched " + agreement + "/" + total +
                " frozen decisions; " + results.Count +
                " disagreement(s). This is disclosure, NOT qualification.");
            log.WriteLine("No adjudication or scoring. NOT READY FOR SCORING.");
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.WriteLine("RED: PR4b judgment rejected (" + e.GetType().Name +
                "): " + e.Message);
            log.WriteLine("NOT READY FOR SCORING.");
            return 1;
        }
    }

    public static int Audit(string batchDir, TextWriter log)
    {
        try
        {
            var batch = V2Pr4bBatch.ReadVerifiedBatch(batchDir);
            var pathA = Path.Combine(batchDir, V2Pr4bSchemas.RecordA);
            var pathB = Path.Combine(batchDir, V2Pr4bSchemas.RecordB);
            V2Pr4bValidation.Require(File.Exists(pathA) && File.Exists(pathB),
                "Both complete independent judge slots required.");
            var a = V2Pr4bValidation.Read<V2Pr4bJudgeRecord>(
                pathA, 3 * 1024 * 1024);
            var b = V2Pr4bValidation.Read<V2Pr4bJudgeRecord>(
                pathB, 3 * 1024 * 1024);
            VerifyRecord(batchDir, batch, a, pathA);
            VerifyRecord(batchDir, batch, b, pathB);
            V2Pr4bValidation.Require(a.JudgeSlot == "A" && b.JudgeSlot == "B" &&
                !string.Equals(a.JudgeId, b.JudgeId,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(a.SessionId, b.SessionId,
                    StringComparison.OrdinalIgnoreCase),
                "Duplicate judge identity/session across blinded passes.");
            log.WriteLine("GREEN: both V2 PR4b frozen blind judgments independently audited; " +
                "calibration A=" + a.CalibrationAgreements + "/" + a.CalibrationDecisions +
                ", B=" + b.CalibrationAgreements + "/" + b.CalibrationDecisions + ".");
            log.WriteLine("Judgments remain separate. PR4c will adjudicate. NOT READY FOR SCORING.");
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.WriteLine("RED: PR4b audit rejected (" + e.GetType().Name + "): " + e.Message);
            return 1;
        }
    }

    private static void VerifyRecord(string dir, V2Pr4bBatchKey batch,
        V2Pr4bJudgeRecord record, string recordFile)
    {
        var keyHash = V2Pr4bValidation.ShaFile(
            Path.Combine(dir, V2Pr4bSchemas.KeyFile));
        V2Pr4bValidation.Require(record.SchemaVersion == V2Pr4bSchemas.Record &&
            record.BatchKeySha256 == keyHash &&
            record.Submission is not null &&
            record.Submission.SchemaVersion == V2Pr4bSchemas.Submission &&
            record.Submission.BatchKeySha256 == keyHash &&
            record.Submission.JudgeId == record.JudgeId &&
            record.Submission.SessionId == record.SessionId &&
            record.Qualification ==
                "diagnostic-only-calibration-disclosed-not-qualified-for-scoring" &&
            record.JudgeSlot is ("A" or "B") &&
            record.ImportedAtUnixMs > 0 &&
            V2Pr4bValidation.IsHash(record.SubmissionSha256) &&
            record.Submission.Assessments is not null &&
            record.Submission.Assessments.Count == batch.Packets.Count,
            "V2 PR4b imported record scope or identity changed.");
        var scope = batch.Packets.ToDictionary(x => x.PacketId,
            StringComparer.Ordinal);
        V2Pr4bValidation.Require(
            record.Submission.Assessments.Select(x => x.PacketId)
                .ToHashSet(StringComparer.Ordinal).SetEquals(scope.Keys) &&
            record.Submission.Assessments.Select(x => x.PacketId)
                .Distinct(StringComparer.Ordinal).Count() == scope.Count,
            "V2 PR4b archived judgment packet census drift.");
        foreach (var item in record.Submission.Assessments)
            V2Pr4bValidation.ValidateAssessment(item, scope[item.PacketId]);
        // Stored submission hash is of the original exact external answer file.
        // We cannot re-hash that file if owner moved it; record is append-only
        // and retains the complete parsed submission. No signature is claimed.
        V2Pr4bValidation.Require(Path.GetFileName(recordFile) ==
            (record.JudgeSlot == "A" ? V2Pr4bSchemas.RecordA : V2Pr4bSchemas.RecordB),
            "Unexpected judge slot record filename.");
        var all = record.Submission.Assessments.ToDictionary(x => x.PacketId,
            StringComparer.Ordinal);
        var total = 0;
        var matches = 0;
        var discrepancies = new List<V2Pr4bCalibrationDifference>();
        foreach (var expected in batch.ExpectedCalibration)
        {
            var given = all[expected.PacketId];
            foreach (var g in expected.Grades)
            {
                total++;
                var actual = given.Grades.Single(x => x.CriterionId == g.CriterionId);
                if (g.Grade == actual.Grade && g.Grounding == actual.Grounding) matches++;
                else discrepancies.Add(new(expected.PacketId, g.CriterionId,
                    "Criterion grade or grounding differs from frozen calibration."));
            }
            foreach (var c in expected.Claims)
            {
                total++;
                var actual = given.Claims.Single(x => x.ClaimId == c.ClaimId);
                if (c.Grounding == actual.Grounding && c.Materiality == actual.Materiality)
                    matches++;
                else discrepancies.Add(new(expected.PacketId, c.ClaimId,
                    "Claim grounding or materiality differs from frozen calibration."));
            }
        }
        V2Pr4bValidation.Require(record.CalibrationDecisions == total &&
            record.CalibrationAgreements == matches &&
            record.CalibrationDifferences is not null &&
            record.CalibrationDifferences.SequenceEqual(discrepancies),
            "Stored calibration disclosure changed after judge import.");
    }
}
