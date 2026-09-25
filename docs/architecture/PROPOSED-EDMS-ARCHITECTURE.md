# Proposed EDMS Architecture

Status: architecture proposal informed by EDMS-CAPSTONE-01 and ELSA-01 through ELSA-13. This is not a production implementation.

## Recommendation

GO WITH CONDITIONS to begin product foundation work. Use an ASP.NET Core Modular Monolith with Elsa 3.8.4 hosted in-process initially, SQL Server for EDMS data and Elsa management/runtime persistence, and a controlled immutable file/object store. Keep Elsa as an explicitly chosen orchestration runtime. Keep document, revision, review, task, comment, authorization, and transmittal truth in EDMS modules.

Evidence: VERIFIED BY EXECUTABLE TEST — Elsa’s code-first Flowchart, conditional routes, cycles, WaitAll, bookmark waits, resume, SQL persistence, process restart for committed suspended state, timers, retry, and version pinning were each tested in ELSA-01..13. EDMS-CAPSTONE-01 combined three reviews, tasks, comments, revision history, a side effect, SLA, cancellation, version coexistence, and one process-boundary review scenario.

Condition: the capstone’s domain stores are mostly in-memory; only Elsa persistence and the application review-task table are SQL-backed in the restart test. Build real EDMS aggregates, transactions, outbox, authorization, audit, and operational migration policy before relying on the product architecture.

## System boundaries

    Client/API
       ↓
    EDMS application use cases and authorization
       ↓
    EDMS domain modules ───── EDMS SQL database
       │                           │
       ├── file port ───────── immutable file/object storage
       │
       └── workflow integration
               ↓
          Elsa definitions and thin Activities
               ↓
          Elsa runtime ───── Elsa management/runtime database

EDMS invokes Elsa deliberately; Elsa does not own the EDMS aggregate model. Activities bind workflow data and call ordinary application services. A business action that must be authorized is authorized in the application before any exact bookmark is resumed.

## Human work

Choose EDMS Task/ReviewAssignment domain + Elsa bookmarks. Elsa 3.8.4’s tagged RunTask mechanism can provide a task wait/result integration point, but the source reviewed for this gate does not provide the full task lifecycle required by the product. Persist task assignment, candidates, claim/ownership, due date, delegation/substitution, outcome, and business audit in EDMS. Store WorkflowInstanceId and BookmarkId as routing/linkage data; never treat BookmarkId as permission.

Evidence: VERIFIED BY EXECUTABLE TEST — the capstone created three app task records from three Elsa RunTask requests, completed them through the app service, and resumed their exact bookmarks. CONFIRMED FROM ELSA 3.8.4 SOURCE — RunTask and dispatcher/reporter contracts exist in the tag. The product task decision is an ARCHITECTURAL INFERENCE from the gap between that primitive and required EDMS semantics.

## Workflow authoring

Choose code-first for the initial release, with carefully bounded configuration for discipline requirements, candidate resolution, and SLA parameters. Keep graph/Activity changes in source control, tests, code review, and versioned deployment. Do not introduce Studio/Designer in the first release. Revisit customer-editable workflow graphs only after defining safe Activity catalogs, validation, ownership, support, migration, and immutable-version rules.

Evidence: VERIFIED BY EXECUTABLE TEST — code-first graph behavior and V1/V2 deployment were tested; same-version mutation is hazardous and running instances can require old Activity code. Configuration can vary business parameters without changing graph identity. Whether customers need visual customization is BUSINESS DECISION REQUIRED.

## Orchestration and domain rules

Elsa should decide the next step and coordinate waits, branches, joins, timers, and version-specific graph behavior. EDMS services should validate whether a revision is receivable, who can review it, what “approved” means, whether comments are carried forward, which file is canonical, and whether publication or transmittal is permitted.

Do not store large or authoritative comments, files, transmittal snapshots, or full aggregate histories only in Elsa workflow state. Pass stable IDs and small decision values; load authoritative records through application services.

## Data, side effects, and consistency

Start with separate EDMS and Elsa databases on the same SQL Server instance. This gives clear ownership, permission, backup, migration, and troubleshooting boundaries. A single SQL Server host is an operational convenience, not a shared transaction. Do not assume an EDMS commit and an Elsa workflow start/resume are atomic across databases.

Use domain events/outbox records to reliably request workflow start/resume and notifications after an EDMS transaction. Use idempotent operation keys for storage moves, publication, transmittals, and external notifications. Add reconciliation for incomplete cross-system work. These are ARCHITECTURAL INFERENCE based on ELSA-12 and EDMS-FIT-01; the capstone does not implement an outbox.

Use immutable canonical revision blobs addressed by stable keys. Model logical distributions independently. Prefer references to the canonical asset, copy only when snapshot isolation is needed, and physically move only when a named requirement demands it.

## Hosting and operations

Start with a single deployable ASP.NET Core Modular Monolith containing the API/application services and Elsa runtime. Configure Elsa management and runtime SQL persistence. Use a separate background worker only when operational evidence requires it; do not begin with microservices or a cluster. ELSA-10/11 prove single-node process restart for committed waits/timers, not distributed ownership or concurrent multi-node resume. Before multiple runtime replicas, test scheduler ownership, duplicate resume, storage locking, idempotency, and SQL contention.

Pin Elsa versions. Treat workflow versions as immutable. New behavior gets a new workflow version; new work selects the intended latest published version; running work remains tied to its version in the tested Elsa path. Keep compatible old Activity implementations deployable while instances depend on them. Never delete an old version with active instances: ELSA-13 observed that deletion removed the referenced instance and bookmark in the tested SQL configuration.

## Product readiness and next milestone

The workflow/platform uncertainty is low enough to begin a real product foundation, but not to skip domain discovery or operational design. The next milestone should be EDMS-00 — Solution Foundation, not a full review workflow:

1. Create the modular-monolith projects and architecture/test rules.
2. Decide project, organization, identity, document-numbering, revision, file-retention, and authorization foundations.
3. Implement one vertical slice: Create Project → Register Document → Receive Revision → Store immutable file → display document/revision.
4. Add a SQL-backed document aggregate, file adapter contract, hash/size/media metadata, idempotent file operation, and integration tests.
5. Add outbox/reconciliation design before workflow-triggered side effects.
6. Introduce Elsa review orchestration only after core revision and permission invariants are explicit.

See [module boundaries](MODULE-BOUNDARIES.md), [domain model](DOMAIN-MODEL.md), [workflow integration](WORKFLOW-INTEGRATION.md), [deployment policy](DEPLOYMENT-POLICY.md), and [open questions](OPEN-QUESTIONS.md).
