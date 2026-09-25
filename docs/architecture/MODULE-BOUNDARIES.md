# Proposed Modular Monolith Boundaries

The first EDMS release should be a modular monolith with explicit module ownership, not a set of microservices. Modules can share a deployment while keeping domain and database ownership clear.

## Modules

| Module | Owns | Must not own |
|---|---|---|
| Projects | Project lifecycle, project configuration, member links, project codes | User credentials, Elsa runtime state |
| Identity & Access | User identity integration, organization links, roles, teams, project-scoped authorization policies | Document review outcomes or workflow bookmark execution |
| Requirements | Document requirement registers, required deliverables, metadata profiles, required disciplines | Received file bytes or review task execution |
| Documents | Document identity, document numbers, immutable revisions, file metadata, revision status projections | Physical storage provider implementation or Elsa graph |
| Files | Storage provider adapters, immutable keys, hashing, scanning hooks, safe reference/copy/move operations | Review authorization or workflow routing |
| Reviews & Comments | Review cycles, assignments/results, comments/responses, consolidation rules | Elsa persistence or general identity credentials |
| Tasks / Work Items | Assignment, candidates, claim/unclaim, completion, delegation/substitution, due date/status, ownership checks | Elsa as sole task database or unvalidated bookmark resume |
| Workflow Integration | Versioned code-first workflows, custom Activities, workflow start/task link/resume adapter, operation correlation | Generic engine interface or authoritative document/task rules |
| Distribution & Transmittals | Workspace routing records, issue envelopes, item snapshots, numbering policy | Mutating past transmittals from current document state |
| Notifications | Notification intent, templates, providers, delivery and retry ledger | Business task authorization |
| Audit | Append-only business audit events and query projections | Replacing Elsa runtime diagnostics |
| Reporting | Read models, exports, dashboards, operational queries | Owning transactional writes to other modules |

Combine modules or projects where the initial team size requires it, but preserve ownership. For example, Files can begin as an infrastructure adapter under Documents, while retaining an interface boundary and independent tests.

## Dependency direction

    API / UI
       ↓
    Application use cases
       ↓
    Domain modules ←── module-owned interfaces
       ↑
    Infrastructure adapters

    Workflow Integration ── calls ──> Application services
    Application services ── explicit commands/events ──> Workflow Integration

The exact assembly references can vary, but:

- Domain code should not depend on Elsa types.
- Application use cases should not persist Elsa internals as business data beyond explicit linkage IDs.
- Elsa Activities should depend on application-service contracts and be thin orchestration adapters.
- Infrastructure implements module-owned repository/file/notification contracts.
- Avoid cyclic module calls. Use application commands or domain events for cross-module work.
- Do not create a generic IWorkflowEngine interface that hides or reimplements Elsa. A narrow WorkflowLauncher/ReviewWorkflowIntegration service is acceptable when it enforces application authorization, input mapping, correlation, and durable dispatch.

## Transaction ownership

Each module owns its records and transaction boundary. A use case that updates a review result and requests workflow resume should first commit the authorized result and an outbox/integration-operation record. A dispatcher resumes the exact bookmark and records completion. Repeated dispatch must be safe. Elsa SQL persistence is a separate state store; it is not the owner of an EDMS database transaction.

## Proposed solution structure

    src/
      Edms.Api/
      Edms.Application/
      Edms.Domain/
      Edms.Infrastructure/
      Edms.WorkflowIntegration/
      Edms.Modules.Projects/
      Edms.Modules.IdentityAccess/
      Edms.Modules.Requirements/
      Edms.Modules.Documents/
      Edms.Modules.Files/
      Edms.Modules.Reviews/
      Edms.Modules.Tasks/
      Edms.Modules.Distribution/
      Edms.Modules.Transmittals/
      Edms.Modules.Notifications/
      Edms.Modules.Audit/
      Edms.Modules.Reporting/
    tests/
      Edms.UnitTests/
      Edms.IntegrationTests/
      Edms.ArchitectureTests/
      Edms.EndToEndTests/

This is a proposed shape, not a request to create those projects in ElsaLab. The first foundation milestone can begin with fewer assemblies and add project separation as ownership stabilizes.

## Test strategy

- Unit tests for domain invariants: immutable revision identity, assignment ownership, comment scope, transmittal snapshot.
- Integration tests for SQL transaction/concurrency, file provider, idempotent operation journal, outbox and Elsa adapter.
- Architecture tests that Domain does not reference Elsa or infrastructure and that modules do not take forbidden dependencies.
- End-to-end tests for a small number of complete review paths including restart and failure recovery.
- ElsaLab remains the version-pinned compatibility regression suite for Elsa-specific behavior.

Classification: ARCHITECTURAL INFERENCE based on ELSA-03, ELSA-09..13, EDMS-FIT-01, and EDMS-CAPSTONE-01. Module names and assembly split require product-team decisions.
