using Elsa.Common.Models;
using Elsa.Common;
using Elsa.Extensions;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Elsa.Workflows.Runtime.Tasks;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ElsaLab.Tests;

public sealed class WorkflowDefinitionVersioningTests
{
    private const string SqlServerConnectionVariable = "ELSALAB_SQLSERVER_CONNECTION_STRING";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "WorkflowVersioning")]
    public async Task DraftAndPublishedSelections_AreDistinct_AndExistingV1ResumesAfterV2Publication()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        await using var host = await VersioningTestHost.CreateAsync(database.ConnectionString);
        var services = host.Services;

        var workflowV1 = await BuildAndRegisterAsync<DocumentReviewVersion1Workflow>(services);
        var v1 = await FindVersionAsync(services, 1);
        Assert.Equal(DocumentReviewDefinitionIdentity.DefinitionId, v1.DefinitionId);
        Assert.Equal(DocumentReviewDefinitionIdentity.Version1Id, v1.Id);
        Assert.Equal(1, v1.Version);
        Assert.True(v1.IsLatest);
        Assert.True(v1.IsPublished);

        var firstV1 = await StartAsync(services, WorkflowDefinitionHandle.ByDefinitionVersionId(v1.Id), "version-v1");
        var v1Instance = await FindInstanceAsync(services, firstV1.WorkflowInstanceId);
        var v1Bookmark = Assert.Single(v1Instance.WorkflowState.Bookmarks);
        var bookmarkStore = services.GetRequiredService<IBookmarkStore>();
        var v1StoredBookmark = await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = v1Bookmark.Id });
        Assert.NotNull(v1StoredBookmark);
        Assert.Equal(v1Instance.Id, v1StoredBookmark.WorkflowInstanceId);
        Assert.Equal(v1Instance.DefinitionVersionId, workflowV1.Identity.Id);
        Assert.Equal(WorkflowStatus.Running, v1Instance.Status);
        Assert.Equal(WorkflowSubStatus.Suspended, v1Instance.SubStatus);

        var workflowV2 = await BuildAndRegisterAsync<DocumentReviewVersion2Workflow>(
            services,
            new WorkflowPublication(IsLatest: true, IsPublished: false));
        var v2Draft = await FindVersionAsync(services, 2);
        Assert.Equal(DocumentReviewDefinitionIdentity.DefinitionId, v2Draft.DefinitionId);
        Assert.Equal(DocumentReviewDefinitionIdentity.Version2Id, v2Draft.Id);
        Assert.Equal(2, v2Draft.Version);
        Assert.True(v2Draft.IsLatest);
        Assert.False(v2Draft.IsPublished);
        var stillPublishedV1 = await FindByVersionStateAsync(services, VersionOptions.Published);
        Assert.Equal(v1.Id, stillPublishedV1.Id);
        var latestIsDraftV2 = await FindByVersionStateAsync(services, VersionOptions.Latest);
        Assert.Equal(v2Draft.Id, latestIsDraftV2.Id);

        var publishedSelectionDuringDraft = await StartAsync(services,
            WorkflowDefinitionHandle.ByDefinitionId(DocumentReviewDefinitionIdentity.DefinitionId, VersionOptions.Published),
            "published-v1-while-v2-draft");
        var publishedSelectionDuringDraftInstance = await FindInstanceAsync(services, publishedSelectionDuringDraft.WorkflowInstanceId);
        Assert.Equal(v1.Id, publishedSelectionDuringDraftInstance.DefinitionVersionId);
        Assert.Equal(1, publishedSelectionDuringDraftInstance.Version);

        // An exact version ID can be selected for a controlled draft/test run.
        var draftRun = await StartAsync(services, WorkflowDefinitionHandle.ByDefinitionVersionId(v2Draft.Id), "version-v2-draft");
        var draftInstance = await FindInstanceAsync(services, draftRun.WorkflowInstanceId);
        Assert.Equal(v2Draft.Id, draftInstance.DefinitionVersionId);
        Assert.Equal(WorkflowSubStatus.Suspended, draftInstance.SubStatus);

        var publication = await services.GetRequiredService<IWorkflowDefinitionPublisher>().PublishAsync(v2Draft);
        Assert.True(publication.Succeeded, string.Join("; ", publication.ValidationErrors.Select(error => error.Message)));
        var publishedV1 = await FindVersionAsync(services, 1);
        var publishedV2 = await FindVersionAsync(services, 2);
        Assert.False(publishedV1.IsLatest);
        Assert.False(publishedV1.IsPublished);
        Assert.True(publishedV2.IsLatest);
        Assert.True(publishedV2.IsPublished);
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<IWorkflowDefinitionPublisher>()
            .RetractAsync(publishedV1, CancellationToken.None));

        var sqlManagementFactory = services.GetRequiredService<IDbContextFactory<ManagementElsaDbContext>>();
        await using (var managementDb = await sqlManagementFactory.CreateDbContextAsync())
        {
            var sqlVersions = await managementDb.WorkflowDefinitions.AsNoTracking()
                .Where(definition => definition.DefinitionId == DocumentReviewDefinitionIdentity.DefinitionId)
                .OrderBy(definition => definition.Version)
                .ToListAsync();
            Assert.Collection(sqlVersions,
                version =>
                {
                    Assert.Equal(v1.Id, version.Id);
                    Assert.Equal(1, version.Version);
                    Assert.False(version.IsLatest);
                    Assert.False(version.IsPublished);
                },
                version =>
                {
                    Assert.Equal(v2Draft.Id, version.Id);
                    Assert.Equal(2, version.Version);
                    Assert.True(version.IsLatest);
                    Assert.True(version.IsPublished);
                });

            var sqlInstance = await managementDb.WorkflowInstances.AsNoTracking()
                .SingleAsync(instance => instance.Id == v1Instance.Id);
            Assert.Equal(DocumentReviewDefinitionIdentity.DefinitionId, sqlInstance.DefinitionId);
            Assert.Equal(v1.Id, sqlInstance.DefinitionVersionId);
            Assert.Equal(1, sqlInstance.Version);
        }

        var bookmarkAfterDeployment = await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = v1Bookmark.Id });
        Assert.NotNull(bookmarkAfterDeployment);
        Assert.Equal(v1StoredBookmark.Hash, bookmarkAfterDeployment.Hash);
        Assert.Equal(v1StoredBookmark.ActivityInstanceId, bookmarkAfterDeployment.ActivityInstanceId);
        Assert.Equal(v1Instance.Id, bookmarkAfterDeployment.WorkflowInstanceId);

        // Resolve a new start by logical ID and Published; the in-process definition cache must see V2.
        var newV2Run = await StartAsync(services,
            WorkflowDefinitionHandle.ByDefinitionId(DocumentReviewDefinitionIdentity.DefinitionId, VersionOptions.Published),
            "version-v2-published");
        var newV2Instance = await FindInstanceAsync(services, newV2Run.WorkflowInstanceId);
        Assert.Equal(v2Draft.Id, newV2Instance.DefinitionVersionId);
        Assert.Equal(2, newV2Instance.Version);
        Assert.Equal(WorkflowSubStatus.Suspended, newV2Instance.SubStatus);

        var v1Response = await services.GetRequiredService<IWorkflowResumer>().ResumeAsync(
            v1Bookmark.Id,
            new Dictionary<string, object>(),
            CancellationToken.None);
        AssertFinished(v1Response, v1Instance.Id);
        var v1Finished = await FindInstanceAsync(services, v1Instance.Id);
        Assert.Equal(v1.Id, v1Finished.DefinitionVersionId);
        Assert.Equal(1, v1Finished.Version);
        Assert.Equal("V1", v1Finished.WorkflowState.Output["DefinitionMarker"]);
        Assert.Equal(false, v1Finished.WorkflowState.Output["CoordinatorExecuted"]);
        Assert.DoesNotContain(v1Finished.WorkflowState.ActivityExecutionContexts,
            context => context.ScheduledActivityNodeId.Contains("CoordinatorCheck", StringComparison.Ordinal));
        Assert.Null(await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = v1Bookmark.Id }));

        var v2Bookmark = Assert.Single(newV2Instance.WorkflowState.Bookmarks);
        var v2Response = await services.GetRequiredService<IWorkflowResumer>().ResumeAsync(
            v2Bookmark.Id,
            new Dictionary<string, object>(),
            CancellationToken.None);
        AssertFinished(v2Response, newV2Instance.Id);
        var v2Finished = await FindInstanceAsync(services, newV2Instance.Id);
        Assert.Equal(v2Draft.Id, v2Finished.DefinitionVersionId);
        Assert.Equal("V2", v2Finished.WorkflowState.Output["DefinitionMarker"]);
        Assert.Equal(true, v2Finished.WorkflowState.Output["CoordinatorExecuted"]);

        var publishedV1Bookmark = Assert.Single(publishedSelectionDuringDraftInstance.WorkflowState.Bookmarks);
        var publishedV1Response = await services.GetRequiredService<IWorkflowResumer>().ResumeAsync(
            publishedV1Bookmark.Id, new Dictionary<string, object>(), CancellationToken.None);
        AssertFinished(publishedV1Response, publishedSelectionDuringDraftInstance.Id);
        var publishedV1Finished = await FindInstanceAsync(services, publishedSelectionDuringDraftInstance.Id);
        Assert.Equal("V1", publishedV1Finished.WorkflowState.Output["DefinitionMarker"]);

        await using var completedManagementDb = await sqlManagementFactory.CreateDbContextAsync();
        var completedV1Sql = await completedManagementDb.WorkflowInstances.AsNoTracking()
            .SingleAsync(instance => instance.Id == v1Instance.Id);
        Assert.Equal(v1.Id, completedV1Sql.DefinitionVersionId);
        Assert.Equal(1, completedV1Sql.Version);
        Assert.Equal(WorkflowStatus.Finished, completedV1Sql.Status);
        Assert.Equal(WorkflowSubStatus.Finished, completedV1Sql.SubStatus);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "WorkflowVersioning")]
    public async Task TwoVersionsCanRemainActiveAndResumeInMixedOrder()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        await using var host = await VersioningTestHost.CreateAsync(database.ConnectionString);
        var services = host.Services;

        await BuildAndRegisterAsync<DocumentReviewVersion1Workflow>(services);
        var v1 = await FindVersionAsync(services, 1);
        var v1A = await StartAsync(services, WorkflowDefinitionHandle.ByDefinitionVersionId(v1.Id), "v1-a");
        var v1B = await StartAsync(services, WorkflowDefinitionHandle.ByDefinitionVersionId(v1.Id), "v1-b");
        var v1AInstance = await FindInstanceAsync(services, v1A.WorkflowInstanceId);
        var v1BInstance = await FindInstanceAsync(services, v1B.WorkflowInstanceId);

        await BuildAndRegisterAsync<DocumentReviewVersion2Workflow>(services);
        var v2 = await FindVersionAsync(services, 2);
        var v2A = await StartAsync(services,
            WorkflowDefinitionHandle.ByDefinitionId(DocumentReviewDefinitionIdentity.DefinitionId, VersionOptions.Published), "v2-a");
        var v2B = await StartAsync(services,
            WorkflowDefinitionHandle.ByDefinitionId(DocumentReviewDefinitionIdentity.DefinitionId, VersionOptions.Published), "v2-b");
        var v2AInstance = await FindInstanceAsync(services, v2A.WorkflowInstanceId);
        var v2BInstance = await FindInstanceAsync(services, v2B.WorkflowInstanceId);

        Assert.All(new[] { v1AInstance, v1BInstance }, instance =>
        {
            Assert.Equal(v1.Id, instance.DefinitionVersionId);
            Assert.Equal(1, instance.Version);
            Assert.Equal(WorkflowSubStatus.Suspended, instance.SubStatus);
        });
        Assert.All(new[] { v2AInstance, v2BInstance }, instance =>
        {
            Assert.Equal(v2.Id, instance.DefinitionVersionId);
            Assert.Equal(2, instance.Version);
            Assert.Equal(WorkflowSubStatus.Suspended, instance.SubStatus);
        });

        var bookmarkStore = services.GetRequiredService<IBookmarkStore>();
        var resumer = services.GetRequiredService<IWorkflowResumer>();
        foreach (var item in new[] { (v2BInstance, "V2"), (v1AInstance, "V1"), (v2AInstance, "V2"), (v1BInstance, "V1") })
        {
            var bookmark = Assert.Single(item.Item1.WorkflowState.Bookmarks);
            var response = await resumer.ResumeAsync(bookmark.Id, new Dictionary<string, object>(), CancellationToken.None);
            AssertFinished(response, item.Item1.Id);
            var completed = await FindInstanceAsync(services, item.Item1.Id);
            Assert.Equal(item.Item2, completed.WorkflowState.Output["DefinitionMarker"]);
            Assert.Equal(item.Item2 == "V2", completed.WorkflowState.Output["CoordinatorExecuted"]);
            Assert.Null(await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmark.Id }));
        }

        Assert.Equal(WorkflowStatus.Finished, (await FindInstanceAsync(services, v1AInstance.Id)).Status);
        Assert.Equal(WorkflowStatus.Finished, (await FindInstanceAsync(services, v1BInstance.Id)).Status);
        Assert.Equal(WorkflowStatus.Finished, (await FindInstanceAsync(services, v2AInstance.Id)).Status);
        Assert.Equal(WorkflowStatus.Finished, (await FindInstanceAsync(services, v2BInstance.Id)).Status);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "WorkflowVersioning")]
    public async Task RetractionRemovesPublishedSelectionButDoesNotDeleteOrUnpinSuspendedV1()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        await using var host = await VersioningTestHost.CreateAsync(database.ConnectionString);
        var services = host.Services;

        await BuildAndRegisterAsync<DocumentReviewVersion1Workflow>(services);
        var v1 = await FindVersionAsync(services, 1);
        var run = await StartAsync(services, WorkflowDefinitionHandle.ByDefinitionVersionId(v1.Id), "retract-v1");
        var instance = await FindInstanceAsync(services, run.WorkflowInstanceId);
        var bookmark = Assert.Single(instance.WorkflowState.Bookmarks);

        await BuildAndRegisterAsync<DocumentReviewVersion2Workflow>(
            services,
            new WorkflowPublication(IsLatest: true, IsPublished: false));
        var beforeRetractV1 = await FindVersionAsync(services, 1);
        var retracted = await services.GetRequiredService<IWorkflowDefinitionPublisher>().RetractAsync(beforeRetractV1);
        Assert.False(retracted.IsPublished);
        Assert.False(retracted.IsLatest);
        var afterRetractV1 = await FindVersionAsync(services, 1);
        Assert.False(afterRetractV1.IsPublished);
        Assert.Equal(v1.Id, afterRetractV1.Id);
        Assert.Null(await services.GetRequiredService<IWorkflowDefinitionStore>().FindAsync(
            new WorkflowDefinitionFilter
            {
                DefinitionId = DocumentReviewDefinitionIdentity.DefinitionId,
                VersionOptions = VersionOptions.Published
            }));
        Assert.NotNull(await services.GetRequiredService<IBookmarkStore>()
            .FindAsync(new BookmarkFilter { BookmarkId = bookmark.Id }));

        var resumed = await services.GetRequiredService<IWorkflowResumer>().ResumeAsync(
            bookmark.Id, new Dictionary<string, object>(), CancellationToken.None);
        AssertFinished(resumed, instance.Id);
        var completed = await FindInstanceAsync(services, instance.Id);
        Assert.Equal(v1.Id, completed.DefinitionVersionId);
        Assert.Equal("V1", completed.WorkflowState.Output["DefinitionMarker"]);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "WorkflowVersioning")]
    public async Task DeletingDefinitionVersionWithActiveInstanceDeletesThatInstanceAndItsBookmark()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        await using var host = await VersioningTestHost.CreateAsync(database.ConnectionString);
        var services = host.Services;

        await BuildAndRegisterAsync<DocumentReviewVersion1Workflow>(services);
        var v1 = await FindVersionAsync(services, 1);
        var run = await StartAsync(services, WorkflowDefinitionHandle.ByDefinitionVersionId(v1.Id), "delete-v1");
        var instance = await FindInstanceAsync(services, run.WorkflowInstanceId);
        var bookmark = Assert.Single(instance.WorkflowState.Bookmarks);
        await BuildAndRegisterAsync<DocumentReviewVersion2Workflow>(services);

        var deleted = await services.GetRequiredService<IWorkflowDefinitionManager>()
            .DeleteVersionAsync(DocumentReviewDefinitionIdentity.DefinitionId, 1, CancellationToken.None);

        Assert.True(deleted);
        Assert.Null(await services.GetRequiredService<IWorkflowDefinitionStore>().FindAsync(
            new WorkflowDefinitionFilter { Id = v1.Id }));
        Assert.Null(await services.GetRequiredService<IWorkflowInstanceStore>().FindAsync(
            new WorkflowInstanceFilter { Id = instance.Id }));
        Assert.Null(await services.GetRequiredService<IBookmarkStore>().FindAsync(
            new BookmarkFilter { BookmarkId = bookmark.Id }));
        Assert.Equal(DocumentReviewDefinitionIdentity.Version2Id,
            (await FindVersionAsync(services, 2)).Id);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "WorkflowVersioning")]
    public async Task RegisteringChangedGraphWithSameDefinitionAndVersionOverwritesTheStoredV1Graph()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        string instanceId;
        string bookmarkId;
        string originalDefinitionData;

        await using (var hostA = await VersioningTestHost.CreateAsync(database.ConnectionString))
        {
            var servicesA = hostA.Services;
            await BuildAndRegisterAsync<DocumentReviewVersion1Workflow>(servicesA);
            var v1 = await FindVersionAsync(servicesA, 1);
            originalDefinitionData = v1.StringData!;
            var run = await StartAsync(servicesA, WorkflowDefinitionHandle.ByDefinitionVersionId(v1.Id), "same-version-original");
            instanceId = run.WorkflowInstanceId;
            bookmarkId = Assert.Single((await FindInstanceAsync(servicesA, instanceId)).WorkflowState.Bookmarks).Id;
        }

        await using var hostB = await VersioningTestHost.CreateAsync(database.ConnectionString, includeV1Activity: true);
        var servicesB = hostB.Services;
        var mutated = await BuildAndRegisterAsync<DocumentReviewVersion1MutationWorkflow>(servicesB);
        Assert.Equal(DocumentReviewDefinitionIdentity.DefinitionId, mutated.Identity.DefinitionId);
        Assert.Equal(DocumentReviewDefinitionIdentity.Version1Id, mutated.Identity.Id);
        Assert.Equal(1, mutated.Identity.Version);
        var storedV1 = await FindVersionAsync(servicesB, 1);
        Assert.NotEqual(originalDefinitionData, storedV1.StringData);

        var response = await servicesB.GetRequiredService<IWorkflowResumer>().ResumeAsync(
            bookmarkId, new Dictionary<string, object>(), CancellationToken.None);
        AssertFinished(response, instanceId);
        var completed = await FindInstanceAsync(servicesB, instanceId);
        Assert.Equal(DocumentReviewDefinitionIdentity.Version1Id, completed.DefinitionVersionId);
        Assert.Equal("V1-MUTATED", completed.WorkflowState.Output["DefinitionMarker"]);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "WorkflowVersioning")]
    public async Task MissingOldActivityFaultsPinnedInstanceWhileRestoredActivityCanResumeAnotherStoredV1Graph()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        string missingActivityInstanceId;
        string missingActivityBookmarkId;
        string compatibleInstanceId;
        string compatibleBookmarkId;

        await using (var hostA = await VersioningTestHost.CreateAsync(database.ConnectionString))
        {
            await BuildAndRegisterAsync<DocumentReviewVersion1Workflow>(hostA.Services);
            var v1 = await FindVersionAsync(hostA.Services, 1);
            var incompatibleRun = await StartAsync(hostA.Services, WorkflowDefinitionHandle.ByDefinitionVersionId(v1.Id), "old-v1-code-missing");
            var compatibleRun = await StartAsync(hostA.Services, WorkflowDefinitionHandle.ByDefinitionVersionId(v1.Id), "old-v1-code-present");
            missingActivityInstanceId = incompatibleRun.WorkflowInstanceId;
            missingActivityBookmarkId = Assert.Single((await FindInstanceAsync(hostA.Services, missingActivityInstanceId)).WorkflowState.Bookmarks).Id;
            compatibleInstanceId = compatibleRun.WorkflowInstanceId;
            compatibleBookmarkId = Assert.Single((await FindInstanceAsync(hostA.Services, compatibleInstanceId)).WorkflowState.Bookmarks).Id;
        }

        await using (var hostB = await VersioningTestHost.CreateAsync(
                         database.ConnectionString,
                         includeV1Activity: false,
                         includeV2Activities: true))
        {
            await BuildAndRegisterAsync<DocumentReviewVersion2Workflow>(hostB.Services);
            var failedResume = await hostB.Services.GetRequiredService<IWorkflowResumer>()
                .ResumeAsync(missingActivityBookmarkId, new Dictionary<string, object>(), CancellationToken.None);
            Assert.NotNull(failedResume);
            Assert.Equal(WorkflowStatus.Finished, failedResume.Status);
            Assert.Equal(WorkflowSubStatus.Faulted, failedResume.SubStatus);
            var failedInstance = await FindInstanceAsync(hostB.Services, missingActivityInstanceId);
            Assert.Equal(WorkflowStatus.Finished, failedInstance.Status);
            Assert.Equal(WorkflowSubStatus.Faulted, failedInstance.SubStatus);
            var incident = Assert.Single(failedInstance.WorkflowState.Incidents);
            Assert.Equal("Elsa.NotFoundActivity", incident.ActivityType);
            Assert.Contains(nameof(FinalizeVersion1Activity), incident.Message, StringComparison.Ordinal);
            Assert.Null(await hostB.Services.GetRequiredService<IBookmarkStore>()
                .FindAsync(new BookmarkFilter { BookmarkId = missingActivityBookmarkId }));

            var untouchedInstance = await FindInstanceAsync(hostB.Services, compatibleInstanceId);
            Assert.Equal(WorkflowStatus.Running, untouchedInstance.Status);
            Assert.Equal(WorkflowSubStatus.Suspended, untouchedInstance.SubStatus);
            Assert.NotNull(await hostB.Services.GetRequiredService<IBookmarkStore>()
                .FindAsync(new BookmarkFilter { BookmarkId = compatibleBookmarkId }));
        }

        // The fresh host has the old Activity implementation, but deliberately does not build/register
        // DocumentReviewVersion1Workflow. Elsa materializes the second stored Typed definition from SQL.
        await using var hostC = await VersioningTestHost.CreateAsync(
            database.ConnectionString,
            includeV1Activity: true,
            includeV2Activities: false);
        await hostC.Services.GetRequiredService<IActivityRegistry>()
            .RegisterAsync([typeof(FinalizeVersion1Activity)], CancellationToken.None);
        var response = await hostC.Services.GetRequiredService<IWorkflowResumer>()
            .ResumeAsync(compatibleBookmarkId, new Dictionary<string, object>(), CancellationToken.None);
        AssertFinished(response, compatibleInstanceId);
        var completed = await FindInstanceAsync(hostC.Services, compatibleInstanceId);
        Assert.Equal("V1", completed.WorkflowState.Output["DefinitionMarker"]);
        Assert.Null(await hostC.Services.GetRequiredService<IBookmarkStore>()
            .FindAsync(new BookmarkFilter { BookmarkId = compatibleBookmarkId }));

        // Once the missing Activity was replaced by Elsa's NotFoundActivity and faulted, restoring its
        // implementation alone cannot resume it: the original AutoBurn bookmark is already gone.
        Assert.Null(await hostC.Services.GetRequiredService<IWorkflowResumer>()
            .ResumeAsync(missingActivityBookmarkId, new Dictionary<string, object>(), CancellationToken.None));
        var stillFaulted = await FindInstanceAsync(hostC.Services, missingActivityInstanceId);
        Assert.Equal(WorkflowSubStatus.Faulted, stillFaulted.SubStatus);
    }

    private static string GetConnectionString() => Environment.GetEnvironmentVariable(SqlServerConnectionVariable)
        ?? throw new InvalidOperationException($"{SqlServerConnectionVariable} is required for workflow-versioning SQL tests.");

    private static async Task<Workflow> BuildAndRegisterAsync<TWorkflow>(
        IServiceProvider services,
        WorkflowPublication? publication = null)
        where TWorkflow : WorkflowBase, new()
    {
        var workflow = await services.GetRequiredService<IWorkflowBuilderFactory>()
            .CreateBuilder()
            .BuildWorkflowAsync<TWorkflow>();
        if (publication != null)
            workflow.Publication = publication;
        await services.GetRequiredService<IWorkflowRegistry>().RegisterAsync(workflow);
        return workflow;
    }

    private static async Task<WorkflowDefinition> FindVersionAsync(IServiceProvider services, int version) =>
        await services.GetRequiredService<IWorkflowDefinitionStore>().FindAsync(new WorkflowDefinitionFilter
        {
            DefinitionId = DocumentReviewDefinitionIdentity.DefinitionId,
            VersionOptions = VersionOptions.SpecificVersion(version)
        }) ?? throw new Xunit.Sdk.XunitException($"Definition version {version} was not found.");

    private static async Task<WorkflowDefinition> FindByVersionStateAsync(IServiceProvider services, VersionOptions options) =>
        await services.GetRequiredService<IWorkflowDefinitionStore>().FindAsync(new WorkflowDefinitionFilter
        {
            DefinitionId = DocumentReviewDefinitionIdentity.DefinitionId,
            VersionOptions = options
        }) ?? throw new Xunit.Sdk.XunitException($"No {options} definition was found.");

    private static async Task<RunWorkflowInstanceResponse> StartAsync(
        IServiceProvider services,
        WorkflowDefinitionHandle handle,
        string reviewKey)
    {
        var client = await services.GetRequiredService<IWorkflowRuntime>().CreateClientAsync();
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = handle,
            Input = new Dictionary<string, object>
            {
                ["DocumentNumber"] = "DPC-10-ME-0001",
                ["Revision"] = 2,
                ["ReviewKey"] = reviewKey
            },
            IncludeWorkflowOutput = true
        });
        Assert.True(response.Status == WorkflowStatus.Running,
            $"Expected a suspended versioning workflow, got {response.Status}/{response.SubStatus}; " +
            $"bookmarks={response.Bookmarks.Count}; incidents={string.Join(" | ", response.Incidents.Select(DescribeIncident))}; " +
            $"outputs={string.Join(", ", response.Output ?? new Dictionary<string, object>())}");
        Assert.Equal(WorkflowSubStatus.Suspended, response.SubStatus);
        Assert.Single(response.Bookmarks);
        Assert.Empty(response.Incidents);
        return response;
    }

    private static async Task<WorkflowInstance> FindInstanceAsync(IServiceProvider services, string instanceId) =>
        await services.GetRequiredService<IWorkflowInstanceStore>()
            .FindAsync(new WorkflowInstanceFilter { Id = instanceId })
        ?? throw new Xunit.Sdk.XunitException($"Workflow instance '{instanceId}' was not found.");

    private static void AssertFinished(RunWorkflowInstanceResponse? response, string expectedInstanceId)
    {
        var actual = response ?? throw new Xunit.Sdk.XunitException("The resumer did not return a workflow response.");
        Assert.Equal(expectedInstanceId, actual.WorkflowInstanceId);
        Assert.Equal(WorkflowStatus.Finished, actual.Status);
        Assert.Equal(WorkflowSubStatus.Finished, actual.SubStatus);
        Assert.Empty(actual.Bookmarks);
        Assert.Empty(actual.Incidents);
    }

    private static string DescribeIncident(object incident) => string.Join(", ", incident.GetType().GetProperties().Select(property =>
    {
        var value = property.GetValue(incident);
        return $"{property.Name}={(value is Exception exception ? $"{exception.GetType().Name}: {exception.Message}" : value)}";
    }));
}

internal sealed class VersioningTestHost : IAsyncDisposable
{
    private VersioningTestHost(ServiceProvider services) => Services = services;

    public ServiceProvider Services { get; }

    public static async Task<VersioningTestHost> CreateAsync(
        string connectionString,
        bool includeV1Activity = true,
        bool includeV2Activities = true)
    {
        var collection = new ServiceCollection();
        collection.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        collection.AddElsa(elsa =>
        {
            elsa.UseWorkflowManagement(management => management.UseEntityFrameworkCore(ef =>
            {
                ef.UseSqlServer(connectionString);
                ef.RunMigrations = true;
            }));
            elsa.UseWorkflowRuntime(runtime => runtime.UseEntityFrameworkCore(ef =>
            {
                ef.UseSqlServer(connectionString);
                ef.RunMigrations = true;
            }));
            elsa.AddActivity<WaitForDocumentReviewActivity>();
            if (includeV1Activity)
                elsa.AddActivity<FinalizeVersion1Activity>();
            if (includeV2Activities)
            {
                elsa.AddActivity<CoordinatorCheckActivity>();
                elsa.AddActivity<FinalizeVersion2Activity>();
            }
        });

        var provider = collection.BuildServiceProvider();
        try
        {
            using var scope = provider.CreateScope();
            var startupTasks = scope.ServiceProvider.GetServices<IStartupTask>().ToList();
            var managementMigration = startupTasks.OfType<RunMigrationsStartupTask<ManagementElsaDbContext>>().First();
            var runtimeMigration = startupTasks.OfType<RunMigrationsStartupTask<RuntimeElsaDbContext>>().First();
            await managementMigration.ExecuteAsync(CancellationToken.None);
            await runtimeMigration.ExecuteAsync(CancellationToken.None);
            var populateRegistries = startupTasks.OfType<PopulateRegistriesStartupTask>().FirstOrDefault();
            if (populateRegistries != null)
                await populateRegistries.ExecuteAsync(CancellationToken.None);
            return new(provider);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
