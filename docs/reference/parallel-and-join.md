# Parallel Execution and Join

This reference synthesizes the fixed three-discipline graph tested in [ELSA-06](../experiments/ELSA-06-parallel-join.md). It does not describe durable or human review behavior.

## Two Elsa models

Elsa 3.8.4 provides a structured `Parallel` composite and graph-based fan-out using `Flowchart` and `FlowFork`. The structured composite schedules its children and completes after their completion callbacks decrement its child count. The graph form emits named outcomes from `FlowFork`, maps those outcomes to named edges, and can converge at `FlowJoin` or a merge-configured target.

ELSA-06 selected `Flowchart` + `FlowFork` + explicit `FlowJoin(WaitAll)`. The distinct discipline nodes and graph edges fit the tested EDMS review shape and make their scheduling relationships visible in the journal. See the experiment for code and boundaries.

## WaitAll in the tested graph

The workflow selected `.WithTokenBasedFlowchart()` because Elsa's tagged token-based Flowchart integration tests directly cover fan-out and converge, while tagged FlowJoin tests cover fork/join with both modes. `FlowJoin.Mode=WaitAll` mapped to token `Merge` behavior, which schedules the join after all forward inbound branch tokens arrive. With all three branches active, the join ran once after Process, Instrument, and Mechanical completed. This mode choice does not mean token-based execution is required. `WaitAny` is a separate race behavior and was not tested.

Token-based and counter-based Flowcharts implement join modes differently. In counter mode, `WaitAll` tracks whether every inbound connection was visited and followed; `WaitAllActive` waits for all connections to be visited but allows un-followed routes. In token mode, the legacy `FlowJoin` maps `WaitAll` to `Merge`; `WaitAny` maps to `Race`, and `WaitAllActive` also falls through to `Merge` in the inspected 3.8.4 source. The ELSA-06 runtime test used only `WaitAll` in token mode.

## Scheduling and concurrency scope

The `FlowFork` result schedules each branch as separate graph work. In the default in-process `IWorkflowRunner` path tested here, the FIFO scheduler and workflow middleware await one scheduled activity invocation before taking the next work item. The controlled service test observed start order Process, Instrument, Mechanical and a maximum of one active review call; the join still withheld consolidation until the blocked Mechanical call completed.

This does not establish behavior for an external dispatcher, another host, or multiple workflow instances. “Parallel” in this experiment describes branch/fan-out semantics; the measured runner did not overlap these awaited branch calls on Tasks.

## Results and journal

Each discipline was a different Activity object and graph node, so each had its own `Activity.Id` and `NodeId`; each runtime visit also had a distinct `ActivityExecutionContext.Id`. Branch contexts named the FlowFork context as their scheduling predecessor. The WaitAll join context was scheduled after the final arriving branch, and downstream consolidation followed the join.

The FlowJoin context showed its `Mode` input but had an empty `JournalData` dictionary; it had no join-arrival entries. The Flowchart context held an internal token list that was empty after successful completion. The normal journal exposed the actual branch executions and scheduling IDs, not separate connection executions or a durable record of each join arrival.

## Result aggregation

Each review Activity writes a separate `Output<string>`. After the join, `ConsolidateReviewsActivity` reads the three node-specific outputs with `GetOutput<T>`, invokes the normal application service, and produces the consolidated status. The workflow then publishes named outputs. No branch writes to a shared mutable collection.

## Evidence

- Observed behavior and limitations: [ELSA-06](../experiments/ELSA-06-parallel-join.md).
- Elsa 3.8.4 source: [`Parallel`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Parallel.cs), [`FlowFork`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/FlowFork.cs), [`FlowJoin`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/FlowJoin.cs), [`Flowchart.Tokens.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/Flowchart.Tokens.cs), [`DefaultActivitySchedulerMiddleware`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Middleware/Workflows/DefaultActivitySchedulerMiddleware.cs), and [`QueueBasedActivityScheduler`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/QueueBasedActivityScheduler.cs).
