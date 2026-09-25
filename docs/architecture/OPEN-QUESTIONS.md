# Open EDMS Business Questions

These questions require product, contract, engineering, legal, or operations decisions. Elsa experiments cannot supply the answers. Resolve the first group before committing to database constraints and aggregate semantics; resolve the second before MVP scope freeze where possible.

## Must answer before database design

- What is the canonical document identity and numbering scheme? Are numbers project-scoped, discipline-prefixed, externally assigned, or mutable?
- How are revision labels ordered and validated: numeric, alphabetic, preliminary/IFC/As-Built codes, customer-defined, or mixed?
- Can multiple received revisions be under review concurrently? What makes a revision current, superseded, valid, approved, or issued?
- Does receiving a new revision invalidate an older approved/valid revision immediately, only after approval, or under a contractual rule?
- What is the exact relationship among DocumentRequirement, Document, DocumentRevision, and a deliverable package?
- Which organization is sender/recipient for each workflow/transmittal, and can a transmittal include files from multiple projects?
- What information must be retained as an immutable legal/business audit event, and for how long?
- What file retention, legal hold, deletion, encryption, malware scan, checksum, and data-residency rules apply?
- How are tenants/customers isolated in database, file storage, keys, backups, and operational access?
- What is the source of identity and organization membership? How are external users authenticated and disabled?
- What authorization matrix applies across project, organization, discipline, team, role, resource, task ownership, and action?
- What SQL Server edition/hosting constraints, backup objectives, and recovery objectives apply?

## Must answer before MVP scope freeze

- Which document requirement templates and metadata fields are required for the first customer/project?
- Which disciplines are mandatory, optional, conditional, or configurable per document/revision?
- Can review assignments be to a user, team, discipline pool, organization, or external party?
- Are reviews parallel, sequential, quorum-based, or conditional? Can reviewers revise their decision?
- Can a reviewer claim a task from a candidate pool? What happens to unclaimed work and conflicting claims?
- Which actions exist in MVP: assign, claim, unclaim, complete, return, reassign, delegate, substitute, cancel, escalate?
- What is the exact meaning of Approved, Approved with Comments, Rejected, Revise and Resubmit, and For Information?
- How are comments categorized, answered, closed, carried forward, and linked to later revisions?
- What does a transmittal represent contractually? Which number format, sender/recipient fields, purpose codes, delivery receipt, signatures, and correction/reissue rules apply?
- Which exchange channels are in scope: email, portals, client systems, vendor systems, or file exchange?
- What are working hours, weekends, holidays, time zones, pause rules, reminder intervals, escalation chains, and SLA calendars?
- Which notifications are mandatory and what delivery evidence is required?
- What bulk actions, import/export, migration from other systems, and reporting are MVP requirements?
- What is the expected user count, concurrent workflow count, daily revision volume, file-size distribution, and peak review completion pattern?
- Which workflow changes require customer approval, and who may authorize publication?

## Can defer until after initial vertical slice

- Customer-authored visual workflows or a workflow marketplace.
- Advanced CRS analytics, discipline workload balancing, and predictive SLA reporting.
- Multiple object-storage providers beyond the first selected provider.
- Distributed/multi-node Elsa runtime deployment.
- Automated migration of already-running workflows between definitions.
- Advanced workflow replay and support tooling.
- Complex state-machine use cases, if any appear.
- Deep data warehouse/reporting model and cross-customer benchmarking.
- Long-term compatibility with future Elsa versions beyond a deliberate upgrade cycle.

## Decision ownership

Each answer should have an accountable product/business owner, a decision date, affected domain concepts, and whether it changes data retention or contract commitments. Record decisions in the production product repository once it exists; do not silently turn assumptions in this Lab into business rules.
