using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeArcheologist.Api.Investigations;
using Microsoft.Data.Sqlite;

namespace CodeArcheologist.EvaluationLab.V2;

/// <summary>Disposable model-free DB fixtures; never touch the user's live DB.</summary>
public static class V2Pr5aSelfTests
{
    public static int Run()
    {
        var pass = 0;
        var fail = 0;
        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS: PR5a " + name); pass++; }
            catch (Exception e)
            {
                Console.Error.WriteLine("FAIL: PR5a " + name + ": " + e.Message);
                fail++;
            }
        }
        static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
        }
        static string Sha(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        static string ShaFile(string file) => Sha(File.ReadAllBytes(file));
        var root = Path.Combine(Path.GetTempPath(),
            "code-archeologist-v2-pr5a-" + Guid.NewGuid().ToString("N"));
        var exportsRoot = Path.Combine(Path.GetTempPath(),
            "code-archeologist-v2-pr5a-exports-" + Guid.NewGuid().ToString("N"));
        var dbFile = Path.Combine(root, "disposable.sqlite3");
        var run = Guid.NewGuid();
        var ws = Guid.NewGuid();
        var repo = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        var fingerprint = new string('a', 64);
        var manifest = new string('b', 64);
        var outputNum = 0;
        string Output() => Path.Combine(exportsRoot, "output-" + (++outputNum));
        try
        {
            Directory.CreateDirectory(root);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbFile, Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE repository_snapshots (
                        id TEXT,workspace_id TEXT,repository_id TEXT,fingerprint_sha256 TEXT,
                        manifest_sha256 TEXT,policy_version TEXT);
                    CREATE TABLE investigation_reports (
                        run_id TEXT,workspace_id TEXT,repository_id TEXT,snapshot_id TEXT,
                        fingerprint_sha256 TEXT,entry_point_id TEXT,evidence_contract_version TEXT,
                        report_contract_version TEXT,status TEXT,diagnostic TEXT,report_json TEXT);
                    CREATE TABLE investigation_attempts (
                        attempt_id TEXT,run_id TEXT,attempt_number INTEGER,status TEXT,
                        terminal_cause TEXT,continues_attempt_id TEXT,ownership_epoch INTEGER);
                    CREATE TABLE attempt_checkpoint_lineage (
                        attempt_id TEXT,checkpoint_id TEXT,relation TEXT);
                    CREATE TABLE investigation_checkpoints (
                        checkpoint_id TEXT,run_id TEXT,attempt_id TEXT,role TEXT,
                        checkpoint_contract_version TEXT,workspace_id TEXT,repository_id TEXT,
                        snapshot_id TEXT,fingerprint_sha256 TEXT,entry_point_id TEXT,
                        evidence_contract_version TEXT,capability_contract_version TEXT,
                        analyzer_version TEXT,model_id TEXT,validator_version TEXT,
                        assessment_contract_version TEXT,assessment_json TEXT,
                        evidence_links_json TEXT,validation_json TEXT,depends_on_json TEXT,
                        content_sha256 TEXT,accepted_at_unix_ms INTEGER,
                        accepted_ownership_epoch INTEGER);
                    CREATE TABLE v2_handoff_optins (run_id TEXT,contract_version TEXT);
                    CREATE TABLE v2_handoff_events (
                        event_id TEXT,call_id TEXT,run_id TEXT,attempt_id TEXT,role TEXT,
                        call_ordinal INTEGER,stage TEXT,event_kind TEXT,prompt_sha256 TEXT,
                        evidence_links_json TEXT,receipt_ids_json TEXT,
                        snapshot_fingerprint TEXT,ownership_epoch INTEGER,
                        created_at_unix_ms INTEGER,content_sha256 TEXT);
                    PRAGMA user_version = 9;
                    """;
                cmd.ExecuteNonQuery();
                void Exec(string sql, params (string Name, object Value)[] values)
                {
                    using var c = db.CreateCommand();
                    c.CommandText = sql;
                    foreach (var (name,value) in values)
                        c.Parameters.AddWithValue(name,value);
                    c.ExecuteNonQuery();
                }
                Exec("""
                    INSERT INTO repository_snapshots
                    VALUES($id,$ws,$repo,$fp,$manifest,'test-copy-v1');
                    """,("$id",snapshot.ToString("D")),("$ws",ws.ToString("D")),
                    ("$repo",repo.ToString("D")),("$fp",fingerprint),("$manifest",manifest));
                Exec("""
                    INSERT INTO investigation_reports
                    VALUES($run,$ws,$repo,$snapshot,$fp,'entry-1','test-evidence-v1',
                           $ver,'incomplete','host-cancelled',NULL);
                    """,("$run",run.ToString("D")),("$ws",ws.ToString("D")),
                    ("$repo",repo.ToString("D")),("$snapshot",snapshot.ToString("D")),
                    ("$fp",fingerprint),("$ver",GateC5ReportContract.Version));
                Exec("""
                    INSERT INTO investigation_attempts VALUES
                    ($attempt,$run,1,'failed','host-failure',NULL,1);
                    """,("$attempt",attempt.ToString("D")),("$run",run.ToString("D")));
            }
            var first = Output();
            var initialHash = ShaFile(dbFile);
            Check("read-only export captures missing report as explicit blocker", () =>
            {
                using var log = new StringWriter();
                Require(V2Pr5aHostBridge.Export(dbFile,run,first,log)==0,
                    "First read-only export failed: "+log);
                var receipt = V2Pr4bValidation.Read<V2Pr5aHostReceipt>(
                    Path.Combine(first,V2Pr5aContract.ProvenanceFile));
                Require(!receipt.HasHostStoredReport &&
                    receipt.OriginalReportSha256 is null &&
                    receipt.Blockers.Contains("no-host-stored-report") &&
                    receipt.Blockers.Contains("no-opt-in-model-adapter-observation") &&
                    !File.Exists(Path.Combine(first,V2Pr5aContract.ReportFile)) &&
                    receipt.Qualification.Contains("not-scoreable",StringComparison.Ordinal),
                    "A missing report or handoff was silently presented as scoreable.");
                Require(ShaFile(dbFile)==initialHash,
                    "Read-only operation modified the input SQLite file.");
            });
            Check("never overwrite an accepted first export", () =>
                Require(V2Pr5aHostBridge.Export(dbFile,run,first,TextWriter.Null)!=0,
                    "Existing provenance export was overwritten."));
            Check("reject absent/foreign run", () =>
                Require(V2Pr5aHostBridge.Export(dbFile,Guid.NewGuid(),Output(),
                    TextWriter.Null)!=0,"Foreign run produced provenance."));
            Check("reject writing exports inside host DB directory", () =>
                Require(V2Pr5aHostBridge.Export(dbFile,run,
                    Path.Combine(root,"unsafe"),TextWriter.Null)!=0,
                    "Data-directory export was allowed to contaminate a host DB."));
            // Output located next to (not beneath) the database input data
            // directory, so it cannot accidentally modify production data.
            var safe = Output();
            Check("accept exact host-serialized incomplete C5 report bytes", () =>
            {
                var scope=new GateCScope(ws,repo,snapshot,fingerprint,
                    "entry-1","test-evidence-v1");
                var progress=GateCRunProgress.New(run) with
                { Status=GateCRunStatus.Incomplete };
                var investigator=new GateC3RoleOutcome(
                    GateCRole.Investigator,GateCRoleStatus.Failed,
                    "model-incomplete",null,null,0);
                var skeptic=new GateC3RoleOutcome(
                    GateCRole.Skeptic,GateCRoleStatus.NotStarted,
                    "not-started",null,null,0);
                var reviewer=new GateC4ReviewerOutcome(
                    GateCRoleStatus.NotStarted,"not-started",null,null);
                var original=new GateC5Report(GateC5ReportContract.Version,run,
                    scope,"local-model","local-model","local-model",[],
                    progress,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,
                    investigator,skeptic,reviewer,[],"investigation-incomplete");
                var raw=JsonSerializer.Serialize(original);
                using(var db=new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource=dbFile,Mode=SqliteOpenMode.ReadWrite,Pooling=false
                }.ToString()))
                {
                    db.Open();
                    using var cmd=db.CreateCommand();
                    cmd.CommandText="UPDATE investigation_reports SET report_json=$json WHERE run_id=$run;";
                    cmd.Parameters.AddWithValue("$json",raw);
                    cmd.Parameters.AddWithValue("$run",run.ToString("D"));
                    Require(cmd.ExecuteNonQuery()==1,"Fixture source report not stored.");
                }
                using var log=new StringWriter();
                Require(V2Pr5aHostBridge.Export(dbFile,run,safe,log)==0,
                    "Valid host-stored report was rejected: "+log);
                var exported=File.ReadAllText(Path.Combine(safe,V2Pr5aContract.ReportFile));
                var receipt=V2Pr4bValidation.Read<V2Pr5aHostReceipt>(
                    Path.Combine(safe,V2Pr5aContract.ProvenanceFile));
                Require(exported==raw &&
                    receipt.OriginalReportSha256==Sha(Encoding.UTF8.GetBytes(raw)) &&
                    receipt.HasHostStoredReport &&
                    receipt.Blockers.Contains("run-not-completed:incomplete") &&
                    receipt.Qualification.Contains("not-scoreable",StringComparison.Ordinal),
                    "Exact raw host report bytes or failed outcome were not preserved.");
            });
            Check("reject fabricated report-to-run identity", () =>
            {
                using var db=new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource=dbFile,Mode=SqliteOpenMode.ReadWrite,Pooling=false
                }.ToString());
                db.Open();
                using var read=db.CreateCommand();
                read.CommandText="SELECT report_json FROM investigation_reports WHERE run_id=$run;";
                read.Parameters.AddWithValue("$run",run.ToString("D"));
                var original=(string)read.ExecuteScalar()!;
                var corrupted=original.Replace(run.ToString("D"),
                    Guid.NewGuid().ToString("D"),StringComparison.OrdinalIgnoreCase);
                using(var update=db.CreateCommand())
                {
                    update.CommandText="UPDATE investigation_reports SET report_json=$value WHERE run_id=$run;";
                    update.Parameters.AddWithValue("$value",corrupted);
                    update.Parameters.AddWithValue("$run",run.ToString("D"));
                    update.ExecuteNonQuery();
                }
                try
                {
                    Require(V2Pr5aHostBridge.Export(dbFile,run,
                        Output(),TextWriter.Null)!=0,
                        "Foreign report RunId passed host identity checks.");
                }
                finally
                {
                    using var restore=db.CreateCommand();
                    restore.CommandText="UPDATE investigation_reports SET report_json=$value WHERE run_id=$run;";
                    restore.Parameters.AddWithValue("$value",original);
                    restore.Parameters.AddWithValue("$run",run.ToString("D"));
                    restore.ExecuteNonQuery();
                }
            });
        }
        catch(Exception e)
        {
            Console.Error.WriteLine("FAIL: PR5a fixture setup: "+e);
            fail++;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { Directory.Delete(exportsRoot, true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine(fail==0
            ? $"GREEN: {pass} V2 PR5a host-report provenance tests; NOT READY FOR SCORING."
            : $"RED: {fail} PR5a tests failed; NOT READY FOR SCORING.");
        return fail==0?0:1;
    }
}
