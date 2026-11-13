# Evaluation Laboratory V2

**PR1 status: structural contracts only; NOT READY FOR SCORING.**

This directory is a separate V2 input namespace. Nothing here replaces the
historical `eval/cases`, `eval/lab-config.json`, C6c/C7 labels, or frozen PR4
reports and judgments.

Proposed V2 case layout (future source-first validation in PR2):

```text
eval/v2/
  README.md
  cases/
    V2-example-case/
      case.json
      ground-truth.json
```

PR1 deliberately supplies **no real case records**. The validator accepts a
missing/empty `cases` directory, prints `0 cases`, and always reports
`NOT READY FOR SCORING`.

From the repository root:

```powershell
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-self-test
dotnet run --project .\tests\evaluation-lab\EvaluationLab.csproj --configuration Release -- --v2-validate --root .\eval\v2
```

`--v2-validate` checks the structure of each case/ground-truth pair: schema
identities, required fields, source hash shapes, safe relative paths,
criteria identity, task compatibility and source-proof reference consistency.
It does **not** establish source truth or independently validate label meaning.

The tests use disposable synthetic sources and execute no imported project.

See `docs/architecture/v2-evaluation-laboratory.md` for the approved D1–D4
architecture, future judging and evidence requirements, and the PR1 boundary.
