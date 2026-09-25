# Workflow definition versioning

These rules reflect code-first SQL Server tests against Elsa 3.8.4 in [ELSA-13](../experiments/ELSA-13-workflow-versioning.md). Use the experiment for test methods, full observations, and tagged source links.

## Identity fields

| Field | Meaning observed in Elsa 3.8.4 |
|---|---|
| DefinitionId | Logical workflow identity shared by versions |
| WorkflowDefinition.Id | Identifier for one stored definition version |
| WorkflowDefinition.Version | Integer version under the logical identity |
| WorkflowInstance.Id | One running workflow instance |
| WorkflowInstance.DefinitionVersionId | Version-row ID used by that instance |
| WorkflowInstance.Version | Integer version associated with that instance |
| IsLatest | Marks the latest stored version |
| IsPublished | Marks the version available to Published selection |

A latest draft can be unpublished while an earlier version remains published. Latest and Published are separate selection states.

## Starting new instances

The tested application selection is WorkflowDefinitionHandle.ByDefinitionId(definitionId, VersionOptions.Published). After V2 was published, new starts through this handle selected V2. Tests can select a draft or historical version deliberately through WorkflowDefinitionHandle.ByDefinitionVersionId(versionId).

## Existing instances

The SQL-backed WorkflowInstance retained its DefinitionVersionId when another version was published. LocalWorkflowClient resolves a continuation graph using this stored version ID. In the experiment, a V1 bookmark resumed as V1 after V2 was published, including across a process boundary.

Publishing a new version did not migrate instances automatically. Elsa 3.8.4 has an explicit Elsa.Alterations Migrate operation. Elsa's tagged integration test exercises it with stored JSON definitions; ELSA-13 did not test migration of these typed code-first workflows. Treat migration as an explicit, separate operation that needs its own compatibility test.

## Code and graph compatibility

The code-first definition store populator finds a record by logical DefinitionId and integer Version. Re-registering changed graph code under the same identity updated the serialized definition; a suspended instance then continued with the changed graph. Elsa did not enforce immutability in this path.

Stored typed workflow graph data let a fresh host continue V1 without building the original WorkflowBase type, but it still needed the Activity implementation named in the graph. If an Activity implementation was missing, Elsa substituted Elsa.NotFoundActivity; in the tested bookmark path this faulted the instance and consumed its AutoBurn bookmark.

Use a new version for behavior changes. Keep compatible Activity implementations deployed while active instances can reach them. Do not treat a serialized graph as a replacement for its executable Activity code.

## Retraction and deletion

Retracting a published version removed it from Published selection but preserved its stored version and suspended instance in the tested setup. Deleting a version while an instance was suspended removed the version, dependent instance, and bookmark. Retraction is not cleanup, and deletion is unsafe while active instances reference a version.

## EDMS policy

For engineering reviews, keep the logical definition ID stable and assign each behavior change a new version number and version-row ID. Start new work on the published version. Preserve older compatible Activities until no active instance needs them. Do not mutate or delete a version in place. Keep workflow version distinct from document revision, review cycle, and application release.

## Evidence boundary

ELSA-13 used representative V1/V2 instances, one SQL Server 2022 database per integration case, and a real two-process deployment test. It did not test production-scale instance counts, concurrent publication, clustered caches, or code-first Migrate alterations.
