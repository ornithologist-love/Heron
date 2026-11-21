using System.Text.Json;
using System.Text.Json.Serialization;
using Heron.Pregel;
using Heron.Pregel.Automation;
using Heron.Pregel.Cli;
using Heron.Pregel.Scripting;
using Heron.Pregel.Tooling;

return await PregelCli.RunAsync(args);

namespace Heron.Pregel.Cli
{
    internal static class PregelCli
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        private static readonly JsonSerializerOptions DecisionJson = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        };

        private const string Usage = """
            heron-pregel - run durable C# graphs written as scripts

            Authoring
              validate <file.csx>                     Compile a graph script
              describe <file.csx>                     Print graph id, fingerprint, nodes and channels as JSON
              api [--output <file.md>]                Export the script API of this version
              skill [--output <directory>]            Write the agent skill (default: ./skills/heron-pregel)

            Tasks and runs
              apply <file.csx> [--input <json>] [--name <name>]    Save a task without running it
              run <file.csx> [--input <json>] [--name <name>]      Save the task if needed, then run it
              run --task <id> [--input <json>]                     Run a saved task
              tasks                                                List tasks
              status --task <id> [--run <id>]                      Show a run, including a pending review
              review --task <id> --run <id> --review-file <json>   Approve, revise or stop a paused run
              stop --task <id> [--run <id>]                        Cancel a run
              serve                                                Keep running and fire scheduled triggers

            Options
              --home <directory>     Data directory (default: HERON_PREGEL_HOME, then ~/.heron-pregel)
              --allow-parallel       Let a task run alongside itself; mutex limits still apply

            Exit codes: 0 succeeded, 2 usage, 3 failed, 4 paused for review, 130 cancelled
            """;

        public static async Task<int> RunAsync(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }
            try
            {
                var options = Options.Parse(args.Skip(1));
                if (options.Value("home") is { Length: > 0 } home)
                    AutomationRuntime.DataRootProvider = () => Path.GetFullPath(home);
                else if (Environment.GetEnvironmentVariable("HERON_PREGEL_HOME") is { Length: > 0 } environmentHome)
                    AutomationRuntime.DataRootProvider = () => Path.GetFullPath(environmentHome);
                return args[0] switch
                {
                    "validate" => await ValidateAsync(options),
                    "describe" => await DescribeAsync(options),
                    "api" => Api(options),
                    "skill" => Skill(options),
                    "apply" or "run" or "tasks" or "status" or "review" or "stop" => await AutomationAsync(args[0], options),
                    "serve" => await ServeAsync(),
                    _ => throw new ArgumentException("Unknown command: " + args[0]),
                };
            }
            catch (ArgumentException error)
            {
                Console.Error.WriteLine(error.Message);
                return 2;
            }
            catch (FileNotFoundException error)
            {
                Console.Error.WriteLine(error.Message);
                return 2;
            }
            catch (Microsoft.CodeAnalysis.Scripting.CompilationErrorException error)
            {
                Console.Error.WriteLine(error.Message);
                return 3;
            }
            catch (InvalidOperationException error)
            {
                Console.Error.WriteLine(error.Message);
                return 3;
            }
        }

        private static async Task<DynamicPregelGraph> LoadGraphAsync(Options options)
        {
            var path = options.Positional ?? throw new ArgumentException("A .csx file is required.");
            if (!File.Exists(path)) throw new FileNotFoundException("Script not found: " + path);
            return await PregelScript.LoadAsync(path, [typeof(AutomationRuntime).Assembly], ["Heron.Pregel.Automation"]);
        }

        private static async Task<int> ValidateAsync(Options options)
        {
            var graph = await LoadGraphAsync(options);
            Console.WriteLine($"OK {graph.Compiled.Definition.GraphId}");
            return 0;
        }

        private static async Task<int> DescribeAsync(Options options)
        {
            var definition = (await LoadGraphAsync(options)).Compiled.Definition;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                graphId = definition.GraphId,
                fingerprint = definition.Fingerprint,
                entryNode = definition.EntryNode.Value,
                nodes = definition.Nodes.Select(node => new
                {
                    id = node.Key.Value,
                    retryMaxAttempts = node.Value.RetryPolicy.MaxAttempts,
                    triggers = node.Value.Triggers.Select(trigger => trigger.Value),
                    readChannels = node.Value.ReadChannels.Select(channel => channel.Value),
                }),
                channels = definition.Channels.Keys.Select(channel => channel.Value),
            }, Json));
            return 0;
        }

        private static int Api(Options options)
        {
            var markdown = ApiExport.Export();
            if (options.Value("output") is not { Length: > 0 } output)
            {
                Console.Write(markdown);
                return 0;
            }
            File.WriteAllText(output, markdown);
            Console.WriteLine("Wrote " + Path.GetFullPath(output));
            return 0;
        }

        private static int Skill(Options options)
        {
            var root = Path.GetFullPath(options.Value("output") is { Length: > 0 } output ? output : Path.Combine("skills", "heron-pregel"));
            var assembly = typeof(PregelCli).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames())
            {
                var name = resource.Replace('\\', '/');
                if (!name.StartsWith("skill/", StringComparison.Ordinal)) continue;
                var target = Path.Combine(root, name["skill/".Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = assembly.GetManifestResourceStream(resource)!;
                using var file = File.Create(target);
                input.CopyTo(file);
            }
            Directory.CreateDirectory(Path.Combine(root, "references"));
            File.WriteAllText(Path.Combine(root, "references", "api.md"), ApiExport.Export());
            Console.WriteLine("Wrote " + root);
            return 0;
        }

        private static AutomationHost CreateHost(AutomationStore store, bool scheduling) =>
            new(store, Path.Combine(AutomationRuntime.DataRoot, "logs"), true, null, null, null, scheduling);

        private static async Task<int> ServeAsync()
        {
            var store = new AutomationStore(AutomationStore.DefaultRoot);
            await using var host = CreateHost(store, scheduling: true);
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.Error.WriteLine($"Scheduling {host.Tasks.Count} task(s) from {AutomationStore.DefaultRoot}. Press Ctrl+C to stop.");
            try { await Task.Delay(Timeout.Infinite, stop.Token); }
            catch (OperationCanceledException) { }
            return 0;
        }

        private static async Task<int> AutomationAsync(string action, Options options)
        {
            var store = new AutomationStore(AutomationStore.DefaultRoot);
            var taskId = options.Value("task") ?? "";
            var runId = options.Value("run") ?? "";
            AutomationReviewDecision? decision = null;
            if (action == "review")
            {
                if (taskId.Length == 0 || runId.Length == 0)
                    throw new ArgumentException("review needs --task, --run and --review-file.");
                var file = options.Value("review-file") ?? throw new ArgumentException("review needs --review-file.");
                decision = JsonSerializer.Deserialize<AutomationReviewDecision>(await File.ReadAllTextAsync(file), DecisionJson)
                    ?? throw new ArgumentException("The review file is empty.");
            }
            using var lease = action is "run" or "review" ? store.TryAcquireExecution()
                ?? throw new InvalidOperationException("This data directory is under maintenance.") : null;
            await using var host = CreateHost(store, scheduling: false);
            host.RefreshExternalRuns();
            if (action == "tasks")
            {
                Console.WriteLine(JsonSerializer.Serialize(host.Tasks.Select(view => new
                {
                    taskId = view.Task.Id,
                    name = view.Task.DisplayName,
                    runId = (view.Current ?? view.Last)?.RunId,
                    status = (view.Current ?? view.Last)?.Status,
                    schedule = view.Task.TriggerSummary,
                    nextRun = view.NextRun,
                }), Json));
                return 0;
            }
            var saved = host.Tasks.FirstOrDefault(view => view.Task.Id == taskId)?.Task;
            if (taskId.Length > 0 && saved == null) throw new ArgumentException("Task not found: " + taskId);
            if (action is "status" or "stop")
            {
                if (saved == null) throw new ArgumentException($"{action} needs --task.");
                var history = host.History(saved.Id);
                var record = (runId.Length > 0 ? history.FirstOrDefault(run => run.RunId == runId) : history.FirstOrDefault())
                    ?? throw new ArgumentException("Run not found.");
                if (action == "stop") host.Cancel(saved.Id, record.RunId);
                Report(record);
                return 0;
            }

            var path = options.Positional;
            if (string.IsNullOrEmpty(path) && saved != null)
                path = host.Scripts.SavedScriptPath(saved.TemplateId) ?? throw new ArgumentException("This task has no script.");
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A .csx file or --task is required.");
            if (!File.Exists(path)) throw new FileNotFoundException("Script not found: " + path);
            path = Path.GetFullPath(path);
            await host.Scripts.OpenDirectoryAsync(Path.GetDirectoryName(path)!);
            var templateId = await host.Scripts.LoadAsync(path);
            if (saved != null && saved.TemplateId != templateId) throw new ArgumentException("The script does not belong to this task.");
            var template = host.Templates.Single(item => item.Id == templateId);

            var inputPath = options.Value("input") ?? "";
            var parameters = new Dictionary<string, string>();
            if (inputPath.Length > 0)
            {
                var input = AutomationScriptInput.Parse(await File.ReadAllTextAsync(inputPath)).Values;
                foreach (var property in input.EnumerateObject())
                    parameters[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()! : property.Value.GetRawText();
                if (template.Fields.Count == 1 && template.Fields[0].Key == "input")
                    parameters = new() { ["input"] = input.GetRawText() };
            }
            if (saved == null)
            {
                var name = options.Value("name") ?? template.Title;
                saved = host.GetTasks(templateId).FirstOrDefault(task => task.Name == name);
                if (saved == null)
                {
                    var values = template.Fields.Where(field => !field.IsReadOnly).ToDictionary(field => field.Key, field => field.DefaultValue);
                    foreach (var pair in parameters) values[pair.Key] = pair.Value;
                    saved = host.GetOrCreateTask(new AutomationTask
                    {
                        TemplateId = templateId,
                        Name = name,
                        Parameters = values,
                        Enabled = false,
                        ScriptInputPath = inputPath.Length > 0 ? Path.GetFullPath(inputPath) : null,
                        AllowParallel = options.Flag("allow-parallel"),
                    });
                }
            }
            if (options.Flag("allow-parallel") && !saved.AllowParallel)
            {
                saved = saved with { AllowParallel = true };
                host.Save(saved);
            }
            if (action == "apply")
            {
                var configured = saved with
                {
                    ScriptInputPath = inputPath.Length > 0 ? Path.GetFullPath(inputPath) : saved.ScriptInputPath,
                    Parameters = saved.Parameters.Concat(parameters).GroupBy(pair => pair.Key)
                        .ToDictionary(group => group.Key, group => group.Last().Value),
                };
                host.Save(host.ResolveTaskConfiguration(configured));
                Console.WriteLine(JsonSerializer.Serialize(new { taskId = saved.Id, name = saved.DisplayName }, Json));
                return 0;
            }
            string? admitted = null;
            ConsoleCancelEventHandler cancel = (_, e) =>
            {
                e.Cancel = true;
                if (admitted != null) host.Cancel(saved.Id, admitted);
            };
            Console.CancelKeyPress += cancel;
            try
            {
                if (decision != null)
                {
                    admitted = runId;
                    host.Review(saved.Id, runId, decision);
                }
                else
                {
                    admitted = host.StartRun(saved.Id, "cli", parameters, null)
                        ?? throw new InvalidOperationException("The task did not start; run status for details.");
                }
                var record = host.History(saved.Id).First(run => run.RunId == admitted);
                string? progress = null;
                while (true)
                {
                    record = host.History(saved.Id).First(run => run.RunId == record.RunId);
                    var running = Flatten(record.Steps).LastOrDefault(step => step.State == AutomationStepState.Running);
                    var next = running == null ? record.Summary : running.Title + ": " + running.Detail;
                    if (next != progress)
                    {
                        Console.Error.WriteLine(next);
                        progress = next;
                    }
                    if (record.IsFinished || record.Status == AutomationRunStatus.AwaitingReview) break;
                    await Task.Delay(250);
                }
                Report(record);
                return record.Status switch
                {
                    AutomationRunStatus.Succeeded => 0,
                    AutomationRunStatus.AwaitingReview => 4,
                    AutomationRunStatus.Cancelled => 130,
                    _ => 3,
                };
            }
            finally { Console.CancelKeyPress -= cancel; }
        }

        private static IEnumerable<AutomationStepRecord> Flatten(IEnumerable<AutomationStepRecord> steps) =>
            steps.SelectMany(step => new[] { step }.Concat(Flatten(step.Children)));

        private static void Report(AutomationRunRecord record) => Console.WriteLine(JsonSerializer.Serialize(new
        {
            taskId = record.TaskId,
            runId = record.RunId,
            status = record.Status,
            summary = record.Summary,
            warning = record.Warning,
            startedAt = record.StartedAt,
            endedAt = record.EndedAt,
            review = record.Status == AutomationRunStatus.AwaitingReview ? record.Review : null,
            steps = record.Steps,
            evidence = record.EvidenceDirectories,
            artifact = record.ArtifactPath,
        }, Json));
    }

    internal sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

        public string? Positional { get; private set; }

        public string? Value(string name) => _values.GetValueOrDefault(name);

        public bool Flag(string name) => _flags.Contains(name);

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            var items = args.ToArray();
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                if (!item.StartsWith("--", StringComparison.Ordinal))
                {
                    if (options.Positional != null) throw new ArgumentException("Unexpected argument: " + item);
                    options.Positional = item;
                }
                else if (item == "--allow-parallel") options._flags.Add(item[2..]);
                else if (index + 1 < items.Length) options._values[item[2..]] = items[++index];
                else throw new ArgumentException("Missing value for " + item);
            }
            return options;
        }
    }
}
