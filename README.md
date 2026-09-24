# ElsaLab

ElsaLab is a separate experimental repository for learning and evaluating Elsa Workflows 3 as natively as possible for future EDMS and FLOREX use. It is not a production application or a custom workflow engine.

The work is incremental. **ELSA-01 — Basic Code-First Execution**, **ELSA-02 — WorkflowBase, Inputs, Outputs and Variables**, **ELSA-03 — Custom Activity + Dependency Injection**, and **ELSA-04 — Flowchart + Conditional Routing** are complete. See [ROADMAP.md](ROADMAP.md) and [docs/LEARNINGS.md](docs/LEARNINGS.md) for their status and observed results.

## ELSA-04: Flowchart + Conditional Routing

`DocumentProcessingWorkflow` uses an Elsa `Flowchart` and `FlowDecision` to route the `RequiresReview` workflow input to either `ReviewDocument` or `AutoAcceptDocument`. Both outcomes connect to a shared completion Sequence. The custom `RegisterDocumentActivity` continues to delegate registration to `IDocumentProcessingService` and its Elsa output is captured by the workflow.

## Run the experiment

From the repository root:

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/ElsaLab.Runner/ElsaLab.Runner.csproj
```

Expected console output:

```text
Completed DPC-10-ME-0001 revision 2: ProcessingStatus=Reviewed, IsValid=True, RegistrationReference=REG-DPC-10-ME-0001-R2, ProcessingMessage=Registered DPC-10-ME-0001, revision 2.
Caller inputs: DocumentNumber=DPC-10-ME-0001, Revision=2, RequiresReview=True
Workflow status: Finished
Workflow substatus: Finished
Typed workflow result: ProcessingStatus=Reviewed
Workflow outputs: ProcessingStatus=Reviewed, RegistrationReference=REG-DPC-10-ME-0001-R2
```

## Structure

```text
ElsaLab.slnx
src/ElsaLab.Runner/       Console runner and code-first workflow
src/ElsaLab.Runner/Activities/  Custom Elsa activities
src/ElsaLab.Runner/Services/   Application service and result
tests/ElsaLab.Tests/      xUnit runtime integration tests
ROADMAP.md                Learning milestones
docs/LEARNINGS.md          Observations from completed experiments
```

## Packages

- `Elsa` **3.8.4**: Elsa's official bundle, which brings in Core, Management, Runtime, and API Common at the aligned 3.8.4 version. This project does not reference `Elsa.Server.Api`, Elsa Studio, or a designer.
- `Microsoft.Extensions.DependencyInjection` **10.0.12**: the DI container used by the console runner and test.
- xUnit **2.9.3** with `xunit.runner.visualstudio` **3.1.4** for the automated test.

The bundle is used because Elsa's official console setup uses `AddElsa()`. No workflow REST endpoints or server host are configured.

## Scope

Use Elsa APIs directly and keep each experiment small enough to understand. Keep business behavior in normal application services and custom Elsa activities thin. Do not add Studio, a designer, persistence, or later roadmap capabilities before their experiments.
