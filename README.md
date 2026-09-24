# ElsaLab

ElsaLab is an experimental, code-first repository for learning Elsa Workflows through small implementations and executable tests. It records what was actually observed against a specific Elsa version, with links to source and test evidence.

## What ElsaLab is not

- A production EDMS
- A custom workflow engine
- An Elsa Studio project
- A workflow Designer project

## Current progress

ELSA-01 through ELSA-05 are verified. ELSA-06 and later remain TODO in the [roadmap](ROADMAP.md).

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
- [Automated tests](tests/ElsaLab.Tests/DocumentRevisionWorkflowTests.cs)
- [Current runner](src/ElsaLab.Runner/Program.cs)

## Run the current experiment

From the repository root:

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/ElsaLab.Runner/ElsaLab.Runner.csproj
```

The runner demonstrates two revision cycles for document `DPC-10-ME-0001`, starting at revision `0` and receiving comments before approval. See [ELSA-05](docs/experiments/ELSA-05-revision-cycles.md) for the observed cycle and journal behavior.
