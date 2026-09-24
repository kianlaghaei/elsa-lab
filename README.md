# ElsaLab

ElsaLab is an experimental, code-first repository for learning Elsa Workflows through small implementations and executable tests. It records what was actually observed against a specific Elsa version, with links to source and test evidence.

## What ElsaLab is not

- A production EDMS
- A custom workflow engine
- An Elsa Studio project
- A workflow Designer project

## Current progress

ELSA-01 through ELSA-06 are verified. ELSA-07 and later remain TODO in the [roadmap](ROADMAP.md).

## Knowledge model

```text
Experiment
   ↓
Executable test
   ↓
Observed result
   ↓
Reference knowledge
   ↓
Reusable pattern
```

The experiment pages preserve the version, implementation commit, limitations, and evidence behind each finding. Reference pages synthesize findings without replacing the experiment record.

## Navigation

- [Documentation index](docs/INDEX.md)
- [Roadmap](ROADMAP.md)
- [Concise learnings](docs/LEARNINGS.md)
- [Automated tests](tests/ElsaLab.Tests/DocumentDisciplineReviewWorkflowTests.cs)
- [Current runner](src/ElsaLab.Runner/Program.cs)

## Run the current experiment

From the repository root:

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/ElsaLab.Runner/ElsaLab.Runner.csproj
```

The runner demonstrates Process, Instrument, and Mechanical reviews for document `DPC-10-ME-0001`, followed by a WaitAll join and consolidation. See [ELSA-06](docs/experiments/ELSA-06-parallel-join.md) for the observed fan-out, join, and in-process scheduling behavior.
