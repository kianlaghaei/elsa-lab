# ElsaLab

ElsaLab is a separate experimental repository for learning and evaluating Elsa Workflows 3 as natively as possible for future EDMS and FLOREX use. It is not a production application or a custom workflow engine.

The work is incremental. Only **ELSA-01 — Basic Code-First Execution** is implemented. See [ROADMAP.md](ROADMAP.md) and [docs/LEARNINGS.md](docs/LEARNINGS.md) for its status and observed results.

## ELSA-01: Basic Code-First Execution

`DocumentReceivedWorkflow` derives from Elsa's `WorkflowBase` and sets its root to a `Sequence` containing two native `WriteLine` activities. The runner registers Elsa and the workflow in `Microsoft.Extensions.DependencyInjection`, resolves Elsa's `IWorkflowRunner`, runs the workflow, and inspects `WorkflowState.Status`.

## Run the experiment

From the repository root:

```powershell
dotnet restore
dotnet build
dotnet run --project src/ElsaLab.Runner/ElsaLab.Runner.csproj
dotnet test
```

Expected console output:

```text
Document received
Document processing started
Workflow status: Finished
```

## Structure

```text
ElsaLab.slnx
src/ElsaLab.Runner/       Console runner and code-first workflow
tests/ElsaLab.Tests/      xUnit integration test
ROADMAP.md                Learning milestones
docs/LEARNINGS.md          Observations from completed experiments
```

## Packages

- `Elsa` **3.8.4**: Elsa's official bundle, which brings in Core, Management, Runtime, and API Common at the aligned 3.8.4 version. This project does not reference `Elsa.Server.Api`, Elsa Studio, or a designer.
- `Microsoft.Extensions.DependencyInjection` **10.0.12**: the DI container used by the console runner and test.
- xUnit **2.9.3** with `xunit.runner.visualstudio` **3.1.4** for the automated test.

The bundle is used because Elsa's official console setup uses `AddElsa()`. No workflow REST endpoints or server host are configured.

## Scope

Use Elsa APIs directly and keep each experiment small enough to understand. Add application/domain services only when a later experiment needs real business logic. Do not add Studio, a designer, a custom workflow abstraction, or persistence until the roadmap reaches the relevant experiment.
