using System.Text;
using System.Text.Json;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>
/// Pure, deterministic A/B comparison. Agreement is only agreement between
/// two recorded opinions; it does not establish independently verified truth.
/// </summary>
internal static class V2Pr4cComparison
{
    internal sealed record Result(
        IReadOnlyList<V2Pr4cDispute> Disputes,
        IReadOnlyList<V2Pr4cCriticalConsensus> CriticalFlags,
        IReadOnlyList<V2Pr4cReasonVariation> ReasonVariations);

    internal static Result Build(V2Pr4bBatchKey batch,
        V2Pr4bJudgeRecord a, V2Pr4bJudgeRecord b,
        string batchKeySha, string judgeASha, string judgeBSha)
    {
        var disputes = new List<V2Pr4cDispute>();
        var critical = new List<V2Pr4cCriticalConsensus>();
        var variations = new List<V2Pr4cReasonVariation>();
        var aa = a.Submission.Assessments.ToDictionary(x => x.PacketId,
            StringComparer.Ordinal);
        var bb = b.Submission.Assessments.ToDictionary(x => x.PacketId,
            StringComparer.Ordinal);
        foreach (var packet in batch.Packets.OrderBy(x => x.PacketId,
            StringComparer.Ordinal))
        {
            var left = aa[packet.PacketId];
            var right = bb[packet.PacketId];
            // Deterministically swap presentation order, without exposing
            // original judge slots to the adjudicator.
            var orientationHash = V2Pr4bValidation.Sha(Encoding.UTF8.GetBytes(
                batchKeySha + judgeASha + judgeBSha + packet.PacketId));
            var firstIsA = Convert.ToInt32(orientationHash[..2], 16) % 2 == 0;
            foreach (var criterion in packet.Criteria.OrderBy(x => x.CriterionId,
                StringComparer.Ordinal))
            {
                var ga = left.Grades.Single(x => x.CriterionId == criterion.CriterionId);
                var gb = right.Grades.Single(x => x.CriterionId == criterion.CriterionId);
                var first = FromGrade(firstIsA ? ga : gb);
                var second = FromGrade(firstIsA ? gb : ga);
                var dimensions = new List<string>();
                if (ga.Grade != gb.Grade) dimensions.Add("grade");
                if (ga.Grounding != gb.Grounding) dimensions.Add("grounding");
                if (!EqualSet(ga.SourceProofRefs, gb.SourceProofRefs))
                    dimensions.Add("source-proof-refs");
                if (!EqualSet(ga.ReportClaimRefs, gb.ReportClaimRefs))
                    dimensions.Add("report-claim-refs");
                Add(packet.PacketId, V2Pr4cItemKind.Criterion,
                    criterion.CriterionId, first, second, dimensions,
                    criterion.ProofRefs, packet.ClaimIds, firstIsA);
            }
            var allProofs = packet.Criteria.SelectMany(x => x.ProofRefs)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            foreach (var claimId in packet.ClaimIds.OrderBy(x => x,
                StringComparer.Ordinal))
            {
                var ca = left.Claims.Single(x => x.ClaimId == claimId);
                var cb = right.Claims.Single(x => x.ClaimId == claimId);
                var first = FromClaim(firstIsA ? ca : cb);
                var second = FromClaim(firstIsA ? cb : ca);
                var dimensions = new List<string>();
                if (ca.Grounding != cb.Grounding) dimensions.Add("grounding");
                if (ca.Materiality != cb.Materiality) dimensions.Add("materiality");
                if (!EqualSet(ca.SourceProofRefs, cb.SourceProofRefs))
                    dimensions.Add("source-proof-refs");
                if (ca.Materiality == V2Materiality.Critical &&
                    cb.Materiality == V2Materiality.Critical &&
                    ca.Grounding == cb.Grounding)
                    critical.Add(new(packet.PacketId, claimId, ca.Grounding,
                        "unreviewed-critical-consensus-not-source-truth"));
                Add(packet.PacketId, V2Pr4cItemKind.Claim, claimId,
                    first, second, dimensions, allProofs, [], firstIsA);
            }
        }
        V2Pr4bValidation.Require(disputes.Select(x => x.DisputeId)
            .Distinct(StringComparer.Ordinal).Count() == disputes.Count,
            "Hash identity collision in PR4c dispute census.");
        return new(disputes, critical, variations);

        void Add(string packetId, V2Pr4cItemKind kind, string itemId,
            V2Pr4cOpinion first, V2Pr4cOpinion second,
            List<string> dimensions, IReadOnlyList<string> proofRefs,
            IReadOnlyList<string> reportClaimRefs, bool firstIsA)
        {
            if (!string.Equals(first.Reason, second.Reason, StringComparison.Ordinal))
                variations.Add(new(packetId, kind, itemId,
                    "unclassified-reason-variance-not-automatically-semantic"));
            if (dimensions.Count == 0) return;
            var identity = V2Pr4bValidation.Sha(Encoding.UTF8.GetBytes(
                batchKeySha + "|" + judgeASha + "|" + judgeBSha + "|" +
                packetId + "|" + kind + "|" + itemId + "|" +
                string.Join(",", dimensions)));
            disputes.Add(new("DSP-" + identity[..20], packetId, kind, itemId,
                dimensions.ToArray(), first, second,
                proofRefs.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                reportClaimRefs.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                firstIsA));
        }
    }

    internal static V2Pr4cOpinion FromGrade(V2CriterionGrade grade) =>
        new(grade.Grade, grade.Grounding, null,
            Sorted(grade.SourceProofRefs), Sorted(grade.ReportClaimRefs),
            grade.Reason);

    internal static V2Pr4cOpinion FromClaim(V2Pr4bClaimGrade claim) =>
        new(null, claim.Grounding, claim.Materiality,
            Sorted(claim.SourceProofRefs), [], claim.Reason);

    internal static string[] Sorted(IEnumerable<string> refs) =>
        refs.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    internal static bool EqualSet(IEnumerable<string> a, IEnumerable<string> b) =>
        a.ToHashSet(StringComparer.Ordinal).SetEquals(b);

    internal static bool Same<T>(T a, T b) =>
        string.Equals(JsonSerializer.Serialize(a, V2Schemas.Json),
            JsonSerializer.Serialize(b, V2Schemas.Json), StringComparison.Ordinal);
}
