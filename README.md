# ElsaLab

ElsaLab is an experimental, code-first repository for learning Elsa Workflows through small implementations and executable tests. It records what was actually observed against a specific Elsa version, with links to source and test evidence.

## What ElsaLab is not

- A production EDMS
- A custom workflow engine
- An Elsa Studio project
- A workflow Designer project

## Current progress

ELSA-01 through ELSA-08 are verified against Elsa 3.8.4 and .NET 10. ELSA-09 and later remain TODO in the [roadmap](ROADMAP.md).

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
- [EDMS storage fit test](docs/fit-tests/EDMS-FIT-01-document-storage.md)
- [Automated tests](tests/ElsaLab.Tests/)
- [Current runner](src/ElsaLab.Runner/Program.cs)

## Run the current experiment

From the repository root:

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/ElsaLab.Runner/ElsaLab.Runner.csproj
```

The runner demonstrates EDMS-FIT-01: an Elsa `FlowDecision` selects an approved document route, and a thin Activity calls the storage service to move a temporary sample file and update metadata. It then starts the ELSA-08 review workflow, shows its suspended bookmark, resumes that exact bookmark through `IWorkflowResumer`, and prints the resulting workflow state. The storage example creates and cleans its data under the system temporary directory. See [EDMS-FIT-01](docs/fit-tests/EDMS-FIT-01-document-storage.md) and [ELSA-08](docs/experiments/ELSA-08-external-resume.md) for evidence and limitations.
