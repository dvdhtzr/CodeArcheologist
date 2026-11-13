using System.Security.Cryptography;
using System.Text;
using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;

/// <summary>Model-free V2 handoff observation tests over fenced disposable SQLite.</summary>
internal static class V2HandoffSelfTests
{
    private static void Require(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }

    private static async Task SetupAsync(Func<WorkspaceStore, ExecutionFence,
        InvestigationExecutionContext, GateCEvidenceSession, Task> body,
        bool adaptive = false)
    {
        await GateC6bSelfTests.ScenarioAsync("V2 PR3b fenced observation: " +
            (adaptive ? "adaptive" : "fixed"), async f =>
        {
            var store = f.Store;
            store.Initialize();
            using var ownership = new ExecutionOwnershipState();
            Require(ownership.TryAcquire(store), "OS+SQLite owner not acquired");
            var fence = ownership.Fence!;
            var scope = GateC6bSelfTests.SeedSnapshot(store);
            var created = store.CreateInvestigation(scope, Guid.NewGuid(), fence,
                adaptiveQuestionSubjectKind: adaptive ? "http-endpoint-attribute" : null);
            var run = created.Record ??
                throw new InvalidOperationException("Unable to seed observed run");
            store.EnableV2Handoff(run.RunId, fence);
            var attempt = store.ClaimNextQueuedAttempt(fence, TimeSpan.FromMinutes(6))!.Attempt;
            var snapshot = store.GetSnapshot(scope.WorkspaceId, scope.RepositoryId,
                scope.SnapshotId)!;
            var session = new GateC6bCheckpointSelfTests.Evidence()
                .OpenVerified(snapshot, scope, run.RunId, GateCRole.Investigator,
                    CancellationToken.None);
            await body(store, fence, new(snapshot, run, attempt, fence), session);
        });
    }

    private static string EvidencePrompt(GateCEvidenceSession session,
        GateCDiscoveryResponse response) =>
        "Question: inspect observed syntax, cite issued handles only." +
        "\n<untrusted-evidence-json>\n" +
        GateC3IndependentRunner.ComposeEvidenceView(response, session.SourceExcerpt,
            session.SourceExcerptTruncated) +
        "\n</untrusted-evidence-json>";

    public static async Task RunAsync()
    {
        await SetupAsync(async (store, fence, execution, session) =>
        {
            var invoked = 0;
            using (V2ModelHandoff.BeginIfEnabled(store, execution, session.Role, session))
            {
                var selection = await V2ModelHandoff.InvokeAsync(
                    "Select one capability", "GateCDiscoverySelection", () =>
                    {
                        invoked++;
                        return Task.FromResult("selected");
                    });
                Require(selection == "selected", "Selection model adapter value was altered");
                var result = session.Request(GateCEvidenceRegistry.Relationships,
                    CancellationToken.None).Response!;
                var prompt = EvidencePrompt(session, result);
                var answer = await V2ModelHandoff.InvokeAsync(
                    prompt, "GateCIndependentAssessment", () =>
                    {
                        invoked++;
                        return Task.FromResult("source observed");
                    });
                Require(answer == "source observed", "Assessment model output changed");
                var events = store.ListV2HandoffEvents(execution.Run.RunId,
                    execution.Attempt.AttemptId);
                Require(invoked == 2 && events.Count == 6 &&
                    events.Select(e => e.EventKind).SequenceEqual([
                        "prepared", "adapter-entered", "completed",
                        "prepared", "adapter-entered", "completed"]),
                    "Actual model handoffs were not recorded as lifecycle events");
                Require(events[3].EvidenceLinksJson != "[]" &&
                    events[0].EvidenceLinksJson == "[]" &&
                    events.All(e => e.ContentSha256 == e.ComputeDigest()) &&
                    events.All(e => !e.PromptSha256.Contains("Select", StringComparison.Ordinal)),
                    "Evidence mapping, checksum or sensitive prompt persistence invalid");
                GateC6bSelfTests.ExpectSqliteAbort(store,
                    "DELETE FROM v2_handoff_events;");
                GateC6bSelfTests.ExpectSqliteAbort(store,
                    "UPDATE v2_handoff_events SET event_kind='faulted';");
            }

        });

        await SetupAsync(async (store, _, execution, session) =>
        {
            // Genuine SQLite rejection in PREPARED phase means zero calls.
            GateC6bSelfTests.Exec(store, """
                CREATE TRIGGER v2_model_fault BEFORE INSERT ON v2_handoff_events
                WHEN NEW.event_kind='prepared'
                BEGIN SELECT RAISE(ABORT,'v2 injected prepare failure'); END;
                """);
            var invoked = 0;
            try
            {
                using var observing = V2ModelHandoff.BeginIfEnabled(
                    store, execution, session.Role, session);
                await V2ModelHandoff.InvokeAsync("selection",
                    "GateCDiscoverySelection", () =>
                    {
                        invoked++;
                        return Task.FromResult("invalid");
                    });
                throw new InvalidOperationException("Failed pre-handoff write reached model");
            }
            catch (StagePersistenceException) { }
            Require(invoked == 0,
                "A model was invoked despite an injected precommit failure");
            var afterFault = store.ListV2HandoffEvents(
                execution.Run.RunId, execution.Attempt.AttemptId);
            Require(afterFault.Count == 0, "Failed transaction published an event");
        });

        await SetupAsync(async (store, fence, execution, session) =>
        {
            // Retry history is owned by the original attempt, never duplicated.
            var callCount = 0;
            GateC6bSelfTests.Exec(store, """
                CREATE TRIGGER v2_model_finish_fault BEFORE INSERT ON v2_handoff_events
                WHEN NEW.event_kind='completed'
                BEGIN SELECT RAISE(ABORT,'v2 injected completion failure'); END;
                """);
            try
            {
                using var observing = V2ModelHandoff.BeginIfEnabled(
                    store, execution, session.Role, session);
                await V2ModelHandoff.InvokeAsync("selection",
                    "GateCDiscoverySelection", () =>
                    {
                        callCount++;
                        return Task.FromResult("returned");
                    });
                throw new InvalidOperationException("Failure to commit terminal observation was hidden");
            }
            catch (StagePersistenceException) { }
            Require(callCount == 1, "Completion injection unexpectedly blocked model invocation");
            var rows = store.ListV2HandoffEvents(execution.Run.RunId,
                execution.Attempt.AttemptId);
            Require(rows.Count == 2 && rows[1].EventKind == V2HandoffContract.Entered,
                "Provider entered event incorrectly converted to success after failure");

            var stale = new ExecutionFence(Guid.NewGuid(), fence.Epoch);
            try
            {
                store.RecordV2Handoff(stale, execution.Attempt.AttemptId,
                    execution.Run.RunId, GateCRole.Investigator, Guid.NewGuid(), 2,
                    "GateCDiscoverySelection", V2HandoffContract.Prepared,
                    new string('a', 64), "[]", "[]",
                    execution.Run.Scope.FingerprintSha256);
                throw new InvalidOperationException("Stale owner accepted V2 event");
            }
            catch (ExecutionFenceException) { }
        });

        await SetupAsync(async (store, _, execution, session) =>
        {
            Require(!store.IsV2HandoffEnabled(Guid.NewGuid()),
                "Observation applied to a foreign run");
            var invoked = 0;
            using (var observer = V2ModelHandoff.BeginIfEnabled(
                store, execution, GateCRole.Investigator, session))
            {
                var issued = session.Request(GateCEvidenceRegistry.Relationships,
                    CancellationToken.None).Response!;
                try
                {
                    await V2ModelHandoff.InvokeAsync(
                        EvidencePrompt(session, issued).Replace(
                            execution.Run.Scope.FingerprintSha256,
                            new string('f', 64), StringComparison.Ordinal),
                        "GateCIndependentAssessment", () =>
                        {
                            invoked++;
                            return Task.FromResult("wrong");
                        });
                    throw new InvalidOperationException("Forged evidence view was accepted");
                }
                catch (StagePersistenceException) { }
            }
            Require(invoked == 0 &&
                store.ListV2HandoffEvents(execution.Run.RunId,
                    execution.Attempt.AttemptId).Count == 0,
                "Foreign evidence reached the model or the immutable record");
            await Task.CompletedTask;
        });
        Console.WriteLine("GREEN: V2 PR3b fixed model-boundary tests: immutable events, " +
            "fenced precommit, unknown after failure, role/snapshot integrity.");
    }
}
