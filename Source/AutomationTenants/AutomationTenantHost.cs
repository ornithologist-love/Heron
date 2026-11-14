using System.Runtime.CompilerServices;
using System.Text.Json;
using Heron.Pregel.Automation;

namespace Heron.AutomationTenants;

/// <summary>Tenant catalog and calendar state layered over the local automation host.</summary>
public sealed class AutomationTenantHost
{
    private static readonly ConditionalWeakTable<AutomationHost, Lazy<AutomationTenantHost>> States = new();
    private readonly AutomationHost _host;
    private readonly object _gate = new();
    private sealed record Catalog(string Server, RemoteAutomationTaskDefinition[] Tasks);
    private Catalog? _catalog;
    private IReadOnlyDictionary<string, IReadOnlyList<AutomationTrigger>> _schedules =
        new Dictionary<string, IReadOnlyList<AutomationTrigger>>();
    private DateTimeOffset _schedulesAt;
    private string CatalogPath => Path.Combine(_host.StoreRoot, "cloud-tasks.json");

    private AutomationTenantHost(AutomationHost host)
    {
        _host = host;
        try
        {
            if (File.Exists(CatalogPath)) _catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(CatalogPath));
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { Serilog.Log.Warning("[Automation] cloud task catalog unavailable: {ErrorType}", error.GetType().Name); }
        host.ScheduleClaimed = ClaimsSchedule;
    }

    public static AutomationTenantHost For(AutomationHost host) => States.GetValue(host,
        static value => new Lazy<AutomationTenantHost>(() => new(value))).Value;
    public IReadOnlyList<RemoteAutomationTaskDefinition> CloudTasks { get { lock (_gate) return _catalog?.Tasks.ToArray() ?? []; } }
    public string CloudTaskServer
    {
        get
        {
            lock (_gate) return _catalog?.Server
                ?? new Uri(RemoteAutomationEnvironment.Current.OfficialSourceUrl).GetLeftPart(UriPartial.Authority);
        }
    }

    public RemoteAutomationTaskDefinition? CloudTask(AutomationTask task)
    {
        RemoteAutomationTaskDefinition[] catalog;
        lock (_gate) catalog = _catalog?.Tasks ?? [];
        return AutomationCloudTasks.Resolve(catalog, task.Id, _host.TaskDefinitionId(task));
    }

    internal void SetServer(string server)
    {
        lock (_gate) if (_catalog == null || _catalog.Server == server) return;
        UpdateCatalog(server, null);
    }

    internal void UpdateCatalog(string server, RemoteAutomationTaskDefinition[]? catalog)
    {
        var next = new Catalog(server, catalog ?? []);
        using (_host.BeginStoreUpdate())
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(next);
            if (json == JsonSerializer.Serialize(_catalog)) return;
            var temporary = CatalogPath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, CatalogPath, true);
            _catalog = next;
        }
        _host.RaiseChanged();
    }

    internal void UpdateSchedules(IReadOnlyDictionary<string, IReadOnlyList<AutomationTrigger>> schedules)
    {
        static string Signature(IReadOnlyDictionary<string, IReadOnlyList<AutomationTrigger>> value) => string.Join('|',
            value.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + string.Join(',',
                pair.Value.Select(t => $"{t.DailyTime}/{t.Frequency}/{t.WeekDays}/{t.MonthDay}/{t.LastDayOfMonth}"))));
        bool changed;
        lock (_gate)
        {
            changed = Signature(_schedules) != Signature(schedules);
            _schedules = schedules;
            _schedulesAt = new DateTimeOffset(_host.CurrentTime);
        }
        if (changed) _host.RaiseChanged();
    }

    private bool ClaimsSchedule(string taskId, DateTimeOffset occurrence)
    {
        lock (_gate)
            return new DateTimeOffset(_host.CurrentTime) - _schedulesAt <= TimeSpan.FromSeconds(60)
                && _schedules.TryGetValue(taskId, out var triggers) && triggers.Any(trigger =>
                    trigger.Validate() == null && trigger.NextOccurrence(occurrence.AddSeconds(-1)) == occurrence);
    }
}

public static class AutomationTenantHostExtensions
{
    public static AutomationTenantHost Tenants(this AutomationHost host) => AutomationTenantHost.For(host);
    public static RemoteAutomationTaskDefinition? CloudTask(this AutomationHost host, AutomationTask task) => host.Tenants().CloudTask(task);
    public static void SetCloudTaskServer(this AutomationHost host, string server) => host.Tenants().SetServer(server);
    public static void UpdateCloudTaskCatalog(this AutomationHost host, string server, RemoteAutomationTaskDefinition[]? catalog) => host.Tenants().UpdateCatalog(server, catalog);
    public static void UpdateServerSchedules(this AutomationHost host, IReadOnlyDictionary<string, IReadOnlyList<AutomationTrigger>> schedules) => host.Tenants().UpdateSchedules(schedules);
    public static bool TryEnableRemoteResources(this AutomationHost host, string server) => host.TryEnableResources(
        () => RemotePregelResourceCoordinator.IsEnabled(server), () => RemotePregelResourceCoordinator.Enable(server));
    public static bool LocalScheduleRan(this AutomationHost host, string taskId, DateTimeOffset occurrence) =>
        host.HistorySummaries(taskId).Any(record => record is { Trigger: "schedule", RemoteRequestId: null }
            && (record.ScheduledFor == occurrence || record.ScheduledFor == null
                && record.QueuedAt >= occurrence.AddSeconds(-5) && record.QueuedAt < occurrence.AddMinutes(1)));
}

internal static class AutomationCloudTasks
{
    internal static RemoteAutomationTaskDefinition? Resolve(IEnumerable<RemoteAutomationTaskDefinition> catalog,
        string configurationId, string definitionId)
    {
        var entries = catalog as RemoteAutomationTaskDefinition[] ?? catalog.ToArray();
        if (entries.Any(item => item.ExcludedConfigurations.Contains(configurationId, StringComparer.Ordinal))) return null;
        return entries.FirstOrDefault(item => item.Id == configurationId || item.Aliases.Contains(configurationId, StringComparer.Ordinal))
            ?? entries.FirstOrDefault(item => definitionId.Length > 0 && item.DefinitionIds.Contains(definitionId, StringComparer.Ordinal));
    }
}
