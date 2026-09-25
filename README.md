# ElsaLab

ElsaLab is an experimental, code-first repository for learning Elsa Workflows through small implementations and executable tests. It records what was actually observed against a specific Elsa version, with links to source and test evidence.

## What ElsaLab is not

- A production EDMS
- A custom workflow engine
- An Elsa Studio project
- A workflow Designer project

## Current progress

ELSA-01 through ELSA-11 are verified against Elsa 3.8.4 and .NET 10. ELSA-12 and later remain TODO in the [roadmap](ROADMAP.md). ELSA-10 verified restart recovery for a committed suspended workflow; ELSA-11 verified SQL-backed Delay restoration after graceful exit and kill-after-commit, including overdue timer recovery in a new process. Interrupted active Activities and clustered scheduling remain untested.

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

## SQL Server integration tests

The ELSA-09, ELSA-10, and ELSA-11 integration tests use a dedicated SQL Server connection from `ELSALAB_SQLSERVER_CONNECTION_STRING`. They create and remove uniquely named `ElsaLab_<guid>` test databases, so the configured test identity must have database create/drop permissions. No connection string or credentials belong in source control. Configure a local or dedicated test-server connection in your shell and run:

```powershell
$env:ELSALAB_SQLSERVER_CONNECTION_STRING = "Server=<sql-server>;Initial Catalog=master;Integrated Security=True;TrustServerCertificate=True;Encrypt=False"
dotnet test tests/ElsaLab.Tests/ElsaLab.Tests.csproj --filter Category=SqlServer
dotnet test tests/ElsaLab.Tests/ElsaLab.Tests.csproj --filter Category=ProcessRestart
dotnet test tests/ElsaLab.Tests/ElsaLab.Tests.csproj --filter Category=SlaTimer
```

The process tests build and launch `tests/ElsaLab.ProcessHarness` as separate OS processes. When the environment variable is absent, SQL integration tests are skipped and the in-memory suite remains runnable. See [ELSA-09](docs/experiments/ELSA-09-sql-server-persistence.md) for SQL provider setup, [ELSA-10](docs/experiments/ELSA-10-process-restart-recovery.md) for suspended-workflow recovery, and [ELSA-11](docs/experiments/ELSA-11-sla-timers-escalation.md) for timer/SLA findings.
