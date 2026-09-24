# Guidance for Coding Agents

This repository is an experimental Elsa knowledge base. Keep changes inside `kianlaghaei/elsa-lab`; do not modify the old `workflow-engine` repository.

## Before implementing

1. Read [`docs/INDEX.md`](docs/INDEX.md) first.
2. Read the relevant experiment record and reference document before implementing a capability.
3. Search the existing source and tests for evidence before researching externally.
4. Prefer Elsa-native capabilities over custom infrastructure. Do not build custom workflow infrastructure before testing Elsa's native capability.
5. Verify APIs against the installed/current Elsa package source, tests, samples, or official documentation. Never rely on Elsa 2 assumptions without checking the applicable Elsa 3 version.
6. Do not claim behavior is verified unless there is executable evidence. Keep observed, source-confirmed, and inferred statements distinct.

## While implementing and documenting

- Keep Activities thin when they adapt workflow execution to application services; keep business behavior in normal services.
- Do not keep per-execution mutable values on Activity instances. Respect the Activity reuse evidence in ELSA-03.
- Add new findings to the appropriate experiment document, including the Elsa version, implementation commit, test/source evidence, limitations, and implications.
- Promote repeated verified knowledge into the focused reference documents without replacing experiment-level evidence.
- Keep `README.md`, `ROADMAP.md`, and `docs/INDEX.md` synchronized.
- Do not change completed experiment behavior unnecessarily; preserve its tests and commit history.
- Do not rewrite Git history, squash learning commits, or force-push.
- Do not start later roadmap experiments unless the user asks for them. Do not introduce Studio or Designer unless a future roadmap item explicitly calls for it.
- Do not add persistence, REST APIs, custom DSLs, or generic runtime contracts outside the explicitly requested experiment scope.

## Verification before completion

For an experiment, run:

```text
dotnet restore
dotnet build
dotnet test
```

Also run the console application when the experiment includes a manual runner. Do not mark an experiment DONE until its required verification succeeds. Documentation-only maintenance should still run the test suite when requested by the maintenance task.

## Knowledge boundaries

- The current verified baseline is Elsa 3.8.4 on .NET 10. See [`docs/versions/elsa-3.8.4.md`](docs/versions/elsa-3.8.4.md).
- Findings apply only to the behavior and host path tested. Do not claim forward compatibility after package upgrades.
- A top-level `WorkflowStatus.Finished` is not, by itself, proof of a successful substatus; check the relevant execution state/journal.
