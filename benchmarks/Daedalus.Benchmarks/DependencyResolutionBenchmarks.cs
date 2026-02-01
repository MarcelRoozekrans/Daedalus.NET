namespace Daedalus.Benchmarks;

using ZLinq;

/// <summary>
/// Benchmarks for phase orchestration dependency resolution — the in-memory
/// dependency graph traversal that runs after every task completion.
/// Simulates the core algorithm from PhaseOrchestrator.OnTaskCompletedAsync
/// without the async DB call, isolating the pure compute + allocation cost.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
public class DependencyResolutionBenchmarks
{
    private List<TaskGraph> _smallProject = default!;  // 10 tasks, simple chain
    private List<TaskGraph> _mediumProject = default!; // 50 tasks, complex deps
    private List<TaskGraph> _largeProject = default!;  // 200 tasks, deep dep tree

    [GlobalSetup]
    public void Setup()
    {
        _smallProject = GenerateTaskGraph(10, 2);
        _mediumProject = GenerateTaskGraph(50, 5);
        _largeProject = GenerateTaskGraph(200, 8);
    }

    [Benchmark(Description = "Dep resolution: 10 tasks - ZLinq ToDictionary + scan")]
    public List<string> ResolveSmallZLinq()
    {
        return ResolveUnblockedTasks(_smallProject, "TASK-000");
    }

    [Benchmark(Description = "Dep resolution: 50 tasks - ZLinq ToDictionary + scan")]
    public List<string> ResolveMediumZLinq()
    {
        return ResolveUnblockedTasks(_mediumProject, "TASK-000");
    }

    [Benchmark(Description = "Dep resolution: 200 tasks - ZLinq ToDictionary + scan")]
    public List<string> ResolveLargeZLinq()
    {
        return ResolveUnblockedTasks(_largeProject, "TASK-000");
    }

    [Benchmark(Description = "Dep resolution: 50 tasks - Standard LINQ ToDictionary")]
    public List<string> ResolveMediumStandardLinq()
    {
        return ResolveUnblockedTasksStandardLinq(_mediumProject, "TASK-000");
    }

    [Benchmark(Description = "Dep resolution: 200 tasks - Standard LINQ ToDictionary")]
    public List<string> ResolveLargeStandardLinq()
    {
        return ResolveUnblockedTasksStandardLinq(_largeProject, "TASK-000");
    }

    [Benchmark(Description = "Dictionary<string,Status> build - 50 tasks (ZLinq)")]
    public Dictionary<string, int> BuildLookup50ZLinq()
    {
        return _mediumProject
            .AsValueEnumerable()
            .ToDictionary(t => t.TaskId, t => t.Status, StringComparer.Ordinal);
    }

    [Benchmark(Description = "Dictionary<string,Status> build - 50 tasks (Standard LINQ)")]
    public Dictionary<string, int> BuildLookup50Standard()
    {
        return _mediumProject
            .ToDictionary(t => t.TaskId, t => t.Status, StringComparer.Ordinal);
    }

    [Benchmark(Description = "Dictionary<string,Status> build - 200 tasks (ZLinq)")]
    public Dictionary<string, int> BuildLookup200ZLinq()
    {
        return _largeProject
            .AsValueEnumerable()
            .ToDictionary(t => t.TaskId, t => t.Status, StringComparer.Ordinal);
    }

    [Benchmark(Description = "Dependencies.Contains check - 8 deps (StringComparer.Ordinal)")]
    public bool DependencyContainsCheck()
    {
        // Simulates checking if a task depends on a completed task
        var task = _mediumProject[25]; // Middle of list, has dependencies
        return task.Dependencies.Contains("TASK-000", StringComparer.Ordinal);
    }

    // === Extracted algorithm from PhaseOrchestrator.OnTaskCompletedAsync ===

    private static List<string> ResolveUnblockedTasks(List<TaskGraph> tasks, string completedTaskId)
    {
        // Build lookup (this is the ZLinq path used in actual code)
        var taskStatusLookup = tasks
            .AsValueEnumerable()
            .ToDictionary(t => t.TaskId, t => t.Status, StringComparer.Ordinal);

        var unblockedTaskIds = new List<string>();

        foreach (var candidate in tasks.AsValueEnumerable())
        {
            // Only evaluate Pending tasks (status == 0) that have dependencies
            if (candidate.Status != 0 || candidate.Dependencies.Count == 0)
            {
                continue;
            }

            if (!candidate.Dependencies.Contains(completedTaskId, StringComparer.Ordinal))
            {
                continue;
            }

            var allDependenciesMet = true;
            foreach (var depTaskId in candidate.Dependencies)
            {
                // Status 2 = Completed
                if (!taskStatusLookup.TryGetValue(depTaskId, out var depStatus) || depStatus != 2)
                {
                    allDependenciesMet = false;
                    break;
                }
            }

            if (allDependenciesMet)
            {
                unblockedTaskIds.Add(candidate.TaskId);
            }
        }

        return unblockedTaskIds;
    }

    private static List<string> ResolveUnblockedTasksStandardLinq(List<TaskGraph> tasks, string completedTaskId)
    {
        var taskStatusLookup = tasks
            .ToDictionary(t => t.TaskId, t => t.Status, StringComparer.Ordinal);

        var unblockedTaskIds = new List<string>();

        foreach (var candidate in tasks)
        {
            if (candidate.Status != 0 || candidate.Dependencies.Count == 0)
            {
                continue;
            }

            if (!candidate.Dependencies.Contains(completedTaskId, StringComparer.Ordinal))
            {
                continue;
            }

            var allDependenciesMet = true;
            foreach (var depTaskId in candidate.Dependencies)
            {
                if (!taskStatusLookup.TryGetValue(depTaskId, out var depStatus) || depStatus != 2)
                {
                    allDependenciesMet = false;
                    break;
                }
            }

            if (allDependenciesMet)
            {
                unblockedTaskIds.Add(candidate.TaskId);
            }
        }

        return unblockedTaskIds;
    }

    // === Data generation ===

    private static List<TaskGraph> GenerateTaskGraph(int taskCount, int maxDeps)
    {
        var tasks = new List<TaskGraph>(taskCount);

        for (int i = 0; i < taskCount; i++)
        {
            var deps = new List<string>();
            // Each task depends on up to maxDeps earlier tasks
            for (int d = 0; d < Math.Min(maxDeps, i); d++)
            {
                deps.Add($"TASK-{(i - d - 1):D3}");
            }

            // First task is Completed (status=2), most later tasks are Pending (status=0)
            var status = i == 0 ? 2 : (i < taskCount / 4 ? 2 : 0);

            tasks.Add(new TaskGraph
            {
                TaskId = $"TASK-{i:D3}",
                Status = status,
                Dependencies = deps
            });
        }

        return tasks;
    }

    /// <summary>
    /// Lightweight stand-in for Task entity, used to isolate dependency resolution cost
    /// from entity creation overhead.
    /// </summary>
    public sealed class TaskGraph
    {
        public string TaskId { get; set; } = string.Empty;
        public int Status { get; set; } // 0=Pending, 1=InProgress, 2=Completed
        public List<string> Dependencies { get; set; } = [];
    }
}
