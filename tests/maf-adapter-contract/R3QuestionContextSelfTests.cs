using CodeArcheologist.Api.Execution;
using CodeArcheologist.Api.Investigations;
using CodeArcheologist.Api.Workspaces;

internal static class R3QuestionContextSelfTests
{
    private static void Expect(bool condition, string diagnostic)
    {
        if (!condition) throw new InvalidOperationException(diagnostic);
    }

    public static async Task RunAsync()
    {
        await GateC6bSelfTests.ScenarioAsync(
            "R3 context: v7 admission, immutable question, idempotency, scope and stale data",
            f =>
            {
                f.Store.Initialize();
                var scope = GateC6bSelfTests.SeedSnapshot(f.Store);
                using var ownership = new ExecutionOwnershipState();
                Expect(ownership.TryAcquire(f.Store), "OS lock and DB epoch required");
                var fence = ownership.Fence!;
                var key = Guid.NewGuid();
                var admitted = f.Store.CreateInvestigation(scope, key, fence,
                    "http-endpoint-attribute");
                Expect(admitted.Created && !admitted.Busy && !admitted.Conflict &&
                    admitted.Record is not null, "R3 run must be admitted");
                var run = admitted.Record!.RunId;
                var context = f.Store.GetQuestionContext(
                    scope.WorkspaceId, scope.RepositoryId, scope.SnapshotId, run)
                    ?? throw new InvalidOperationException("Question context missing after admission.");
                Expect(InvestigationQuestionContract.Verify(context),
                    "the saved question must be fully host-verifiable");
                Expect(context.RunId == run && context.QuestionId != run &&
                    context.SubjectNodeId == scope.EntryPointId &&
                    context.QuestionKind == InvestigationQuestionContract.ExplainHttpEntryPoint &&
                    context.QuestionTemplateVersion == InvestigationQuestionContract.TemplateVersion,
                    "run/question identities and versions must remain distinct");
                Expect(context.CreatedAtUtc.Offset == TimeSpan.Zero,
                    "question timestamp must be UTC");
                Expect(GateC6bSelfTests.Scalar(f.Store,
                    "SELECT COUNT(*) FROM investigation_question_contexts;") == 1,
                    "exactly one context row should be committed");

                var replay = f.Store.CreateInvestigation(scope, key, fence,
                    "http-endpoint-attribute");
                Expect(!replay.Created && !replay.Conflict && !replay.Busy &&
                    replay.Record?.RunId == run, "identical retry must replay even with occupied slot");
                var otherSubject = f.Store.CreateInvestigation(scope, key, fence,
                    "http-minimal-mapping");
                Expect(otherSubject.Conflict && !otherSubject.Created,
                    "same idempotency key cannot change subject kind");
                var legacyCollision = f.Store.CreateInvestigation(scope, key, fence);
                Expect(legacyCollision.Conflict && !legacyCollision.Created,
                    "legacy requests cannot alias structured question keys");
                var busy = f.Store.CreateInvestigation(scope, Guid.NewGuid(), fence,
                    "http-endpoint-attribute");
                Expect(busy.Busy && !busy.Created &&
                    GateC6bSelfTests.Scalar(f.Store,
                        "SELECT COUNT(*) FROM investigation_question_contexts;") == 1,
                    "busy admission must not leave orphan question rows");

                Expect(f.Store.GetQuestionContext(scope.WorkspaceId, scope.RepositoryId,
                    Guid.NewGuid(), run) is null, "foreign snapshot must return no context");
                Expect(f.Store.GetQuestionContext(Guid.NewGuid(), scope.RepositoryId,
                    scope.SnapshotId, run) is null, "foreign workspace must return no context");
                Expect(f.Store.GetQuestionContext(scope.WorkspaceId, scope.RepositoryId,
                    scope.SnapshotId, Guid.NewGuid()) is null,
                    "unknown run must return no context");

                var second = new WorkspaceStore();
                // A second WorkspaceStore instance can
                // read but cannot acquire execution ownership while lock is held.
                second.Initialize();
                Expect(second.GetQuestionContext(scope.WorkspaceId, scope.RepositoryId,
                    scope.SnapshotId, run) == context,
                    "question context must survive reopening the database");

                GateC6bSelfTests.ExpectSqliteAbort(f.Store,
                    "UPDATE investigation_question_contexts SET question_kind='other';");
                GateC6bSelfTests.ExpectSqliteAbort(f.Store,
                    "DELETE FROM investigation_question_contexts;");
                return Task.CompletedTask;
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 context: v6-to-v7 migration preserves legacy run and no synthetic question",
            f =>
            {
                f.Store.InitializeTo(6);
                var scope = GateC6bSelfTests.SeedSnapshot(f.Store);
                Guid legacyRun;
                using (var owner = new ExecutionOwnershipState())
                {
                    Expect(owner.TryAcquire(f.Store), "legacy test must own execution");
                    legacyRun = f.Store.CreateInvestigation(scope, Guid.NewGuid(), owner.Fence!)
                        .Record!.RunId;
                }
                f.Store.Initialize();
                f.Store.Initialize();
                Expect(GateC6bSelfTests.Scalar(f.Store, "PRAGMA user_version;") == 8,
                    "forward migration must reach schema v7");
                var prior = f.Store.GetInvestigation(scope.WorkspaceId, scope.RepositoryId,
                    scope.SnapshotId, legacyRun);
                Expect(prior is not null && prior.Summary.Status == "running",
                    "migration must preserve historical run and not perform recovery");
                Expect(f.Store.GetQuestionContext(scope.WorkspaceId, scope.RepositoryId,
                    scope.SnapshotId, legacyRun) is null,
                    "old run must not receive retroactively invented question context");
                return Task.CompletedTask;
            });

        await GateC6bSelfTests.ScenarioAsync(
            "R3 context: invalid and altered identity is rejected model-free",
            f =>
            {
                f.Store.Initialize();
                var scope = GateC6bSelfTests.SeedSnapshot(f.Store);
                try
                {
                    _ = InvestigationQuestionContract.RequestDigest(scope, "syntax-declaration");
                    throw new InvalidOperationException("Unsupported subject was accepted.");
                }
                catch (ArgumentException) { }
                var context = InvestigationQuestionContract.Create(Guid.NewGuid(), scope,
                    "http-minimal-mapping", DateTimeOffset.UtcNow);
                Expect(InvestigationQuestionContract.Verify(context),
                    "a fresh supported context must verify");
                Expect(!InvestigationQuestionContract.Verify(context with
                    { ContextDigest = new string('0', 64) }),
                    "altered context digest must fail");
                Expect(!InvestigationQuestionContract.Verify(context with
                    { Scope = context.Scope with { SnapshotId = Guid.NewGuid() } }),
                    "foreign saved snapshot must fail");
                Expect(!InvestigationQuestionContract.Verify(context with
                    { QuestionKind = "freeform-shell-command" }),
                    "unsupported question must fail");
                Expect(!InvestigationQuestionContract.Verify(context with
                    { QuestionTemplateVersion = "untrusted-template" }),
                    "prompt template changes must fail");
                Expect(!InvestigationQuestionContract.Verify(context with
                    { SubjectNodeId = "another-node" }),
                    "subject rewriting must fail");
                return Task.CompletedTask;
            });

        Console.WriteLine("GREEN: R3 PR1 model-free question context, fenced atomicity, v7 migration, legacy runs, scoped reads, idempotency, immutability and tamper rejection.");
    }
}
