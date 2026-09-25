# Proposed EDMS Domain Model

This is a discovery blueprint, not a set of production entities. Names and aggregate boundaries must be validated against business rules before schema design.

| Concept | Responsibility | Likely relationship | Must not own |
|---|---|---|---|
| Project | Project-level EDMS scope, codes, lifecycle, and configuration | Owns project membership and project-specific numbering/configuration references | Elsa workflow graph, binary contents, global identity |
| Organization | Company, client, vendor, or other legal/operational party | Participates in projects and transmittals | Project-specific assignment rules by itself |
| ProjectMember | Joins a user/team/organization to a project with scoped roles | References Identity, Project, roles, and possibly discipline/team membership | Authentication credentials or workflow bookmark data |
| User / Team | Actor and organizational grouping | Identity membership and project-scoped participation | Document business state |
| Role / Discipline | Permission and technical review classification | Project configuration maps disciplines to teams/users/requirements | Global authorization solely as a string on a workflow |
| ExternalParty | External recipient/reviewer identity and contact boundary | Organization/person reference on project and distribution | Internal user authentication |
| DocumentRequirement | Defines an expected deliverable, metadata profile, or required discipline set | Belongs to Project; may later be satisfied by a Document | Actual received revision/file |
| Document | Stable identity/number for the engineering deliverable | Parent identity for immutable DocumentRevision records | Mutable representation of a past revision |
| DocumentRevision | Immutable received/issued revision facts and lifecycle projection | Belongs to one Document; has files and ReviewCycles | Replacing the prior revision or owning all comments/tasks |
| DocumentFile | File identity, stable storage key, hash, size, media type, scan state | One or more immutable assets associated with a revision | Workflow-owned physical path semantics |
| ReviewCycle | One review round for an exact DocumentRevision | Belongs to a revision; groups assignments/results/comments | Rewriting previous cycle history |
| ReviewAssignment | Work assigned to a discipline/person/team for one cycle | Belongs to a ReviewCycle and points to candidate/owner | Elsa as the only record of assignment or authorization |
| ReviewResult | Immutable decision, actor, time, and rationale for an assignment | Belongs to an assignment; may include Approved, Commented, Rejected, or business-defined results | File movement or task authorization |
| Comment | A review observation tied to exact revision/cycle/discipline | Belongs to DocumentRevision and ReviewCycle; can be explicitly carried forward | Free-form workflow output as the only durable record |
| CommentResponse | Response and disposition history for a Comment | Child/history of a Comment; can link a later revision or assignment | Deleting the original comment when revision advances |
| DocumentDistribution | Records reference/copy/move routing to a workspace or party | Links an exact revision/file to a target and operation | Canonical document identity |
| Transmittal | Immutable issued communication envelope | Has stable number, parties, purpose/time, and TransmittalItems | Re-reading mutable “current revision” after issue |
| TransmittalItem | Snapshot of DocumentId, RevisionId, file identity/hash and issue metadata | Immutable child of Transmittal | Following the Document’s current pointer dynamically |
| Task / WorkItem | Human action lifecycle: assign, claim, complete, return, delegate, substitute, cancel | Links ReviewAssignment or another business request with optional WorkflowInstanceId/BookmarkId | Treating BookmarkId as permission or business identity |
| Notification | Delivery intent/status across channels | Usually produced from an outbox/domain event and linked to a task/document | Workflow execution as the sole delivery ledger |
| AuditEvent | Immutable business action evidence: actor, target, action, before/after, time, correlation | Append-only record or stream scoped by tenant/project/resource | Replacing Elsa execution diagnostics |
| IntegrationOperation | Idempotent operation identity, command fingerprint, status, result/failure, attempts | Links workflow, Activity execution, document/revision/task, and external effect | Generic duplicate suppression without command conflict checks |

## Aggregate guidance

Start with narrow transactional aggregates:

- Project: project settings and membership references.
- Document: stable document identity and numbering; revisions should be immutable child records or independently addressable records with invariant checks.
- DocumentRevision: immutable received file metadata plus explicit current status projections. A newer revision must not rewrite a prior revision’s comments, file identity, or issued transmittals.
- ReviewCycle: cycle lifecycle and assignments/results for one revision. Whether assignments/results are inside this aggregate or separate tables is a scale/transaction question.
- Comment: preserve text, author, exact revision/cycle scope, responses, and disposition.
- Transmittal: immutable envelope and item snapshots. Reissue/correction creates a new record rather than mutating an issued one.
- Task/WorkItem: separate lifecycle aggregate when claiming, delegating, or competing for work requires concurrency control.

Do not put the whole project, all documents, reviews, comments, and transmittals in one aggregate. Use references and explicit application transactions.

## Revision and validity separation

Document identity, received revision, review cycle, and current-valid revision are independent:

    Document DOC-100
      R2 ── ReviewCycle 1 ── comments retained
      R3 ── ReviewCycle 1 ── new assignments/results

The capstone verified that R3 could become LatestReceived and CurrentValid without erasing the R2 comment. Production rules for validity, supersession, and whether an already-valid revision remains valid after a new revision arrives are BUSINESS DECISION REQUIRED.

## Files and distributions

Represent a DocumentFile with immutable identity and a storage key; hash, size, media type, malware scan result, retention class, and encryption/key metadata are candidates. Keep the canonical asset stable when possible. DocumentDistribution records logical target, mode, operation ID, exact file identity, and completion. A Reference distribution points at the canonical asset; Copy creates a separately verifiable snapshot; Move changes physical placement only where required.

## Workflow linkage

WorkflowInstanceId, DefinitionVersionId, ActivityInstanceId, BookmarkId, CorrelationId, and OperationId are useful technical linkage values. They do not replace DocumentId, DocumentRevisionId, ReviewCycleId, ReviewAssignmentId, or TaskId. Store business IDs in EDMS records and propagate correlation IDs into Elsa inputs/activity logs.

## Evidence and unresolved decisions

- VERIFIED BY EXECUTABLE TEST: the capstone’s small revision/cycle/comment/task/transmittal model preserved a comment on R2 while R3 was received and reviewed.
- ARCHITECTURAL INFERENCE: immutable revisions, comments, and issued transmittal snapshots reduce history loss and make audit/reconciliation practical.
- BUSINESS DECISION REQUIRED: numbering rules, valid-revision policy, comment carry-forward semantics, retention, external-party identity, and legal evidence requirements.
- NOT ESTABLISHED: the proposed production aggregates, EF schema, tenant isolation, and concurrent update rules.

See [EDMS-CAPSTONE-01](../fit-tests/EDMS-CAPSTONE-01.md) for the executable evidence and [OPEN-QUESTIONS](OPEN-QUESTIONS.md) for unresolved business semantics.
