using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CherrySentinel.Dashboard.Services;
using CherrySentinel.Shared.Models;

namespace CherrySentinel.Dashboard;

public partial class MainWindow : Window
{
    private CentralApiClient _api;
    private readonly DispatcherTimer _autoRefresh;
    private bool _usingDemo;
    private List<Incident> _incidents = [];
    private List<ThreatCampaign> _campaigns = [];
    private readonly Dictionary<string, FrameworkElement> _pages;
    private readonly Dictionary<string, Button> _navButtons;

    public ObservableCollection<ResponseActionRow> Actions { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        _api = new CentralApiClient(ServerUrlBox.Text.Trim());
        SettingsUrlBox.Text = ServerUrlBox.Text;
        ActionsGrid.ItemsSource = Actions;

        _pages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Dashboard"] = PageDashboard,
            ["Incidents"] = PageIncidents,
            ["Paths"] = PagePaths,
            ["Agents"] = PageAgents,
            ["Catalog"] = PageCatalog,
            ["Settings"] = PageSettings
        };
        _navButtons = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Dashboard"] = NavDashboard,
            ["Incidents"] = NavIncidents,
            ["Paths"] = NavPaths,
            ["Agents"] = NavEndpoints,
            ["Catalog"] = NavRules,
            ["Settings"] = NavSettings
        };

        _autoRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoRefresh.Tick += async (_, _) => await RefreshAsync(silent: true);
        _autoRefresh.Start();
        Loaded += async (_, _) => await RefreshAsync();
        ShowPage("Dashboard");
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "Detect-only UI: เชื่อมปุ่มนี้กับ Response API ของ Central ได้ในขั้นถัดไป\n" +
            "(ต้องมี approval — ไม่ block/kill อัตโนมัติ)",
            "Cherry Sentinel Agent",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ViewAllIncidents_Click(object sender, MouseButtonEventArgs e) => ShowPage("Incidents");

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag) ShowPage(tag);
    }

    private void ShowPage(string name)
    {
        foreach (var kv in _pages)
            kv.Value.Visibility = kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;

        foreach (var kv in _navButtons)
            kv.Value.Background = kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(0x0F, 0x68, 0xFF))
                : Brushes.Transparent;
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void DemoBtn_Click(object sender, RoutedEventArgs e)
    {
        ApplyDemo();
        SetStatus(false, true);
    }

    private async void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        ServerUrlBox.Text = SettingsUrlBox.Text.Trim();
        await RefreshAsync();
    }

    private async Task RefreshAsync(bool silent = false)
    {
        try
        {
            var url = ServerUrlBox.Text.Trim();
            SettingsUrlBox.Text = url;
            if (!string.Equals(_api.BaseUrl, url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                _api.Dispose();
                _api = new CentralApiClient(url);
            }

            var healthy = await _api.HealthAsync();
            if (!healthy)
            {
                if (!_usingDemo) ApplyDemo();
                SetStatus(false, _usingDemo);
                KpiMode.Text = _usingDemo ? "Mode: Demo data" : "Mode: Server offline";
                ModeLabel.Text = KpiMode.Text;
                return;
            }

            var incidents = await _api.GetIncidentsAsync();
            var threats = await _api.GetThreatsAsync();
            var agentsJson = await _api.GetAgentsAsync();
            var catalog = await _api.GetCatalogAsync();

            if (incidents.Count == 0 && threats.Count == 0)
            {
                ApplyDemo();
                SetStatus(true, true);
                KpiMode.Text = "Mode: Live (empty → demo)";
                ModeLabel.Text = KpiMode.Text;
                return;
            }

            _usingDemo = false;
            BindAll(incidents, threats, agentsJson, catalog);
            SetStatus(true, false);
            KpiMode.Text = "Mode: Live API";
            ModeLabel.Text = "Live · " + DateTime.Now.ToString("HH:mm:ss");
            LastRefreshText.Text = $"Updated {DateTime.Now:HH:mm:ss}";
            SidebarCheckinText.Text = $"Last check-in: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(false, _usingDemo);
        }
    }

    private void ApplyDemo()
    {
        _usingDemo = true;
        BindAll(DemoData.Incidents(), DemoData.Campaigns(), [], null, useDemoAgents: true);
        KpiMode.Text = "Mode: Demo data";
        ModeLabel.Text = "Demo · 10.0.105.35 → .190 → .200";
        LastRefreshText.Text = $"Demo {DateTime.Now:HH:mm:ss}";
        SidebarCheckinText.Text = "Last check-in: 1 min ago";
        SidebarStatusText.Text = "Healthy";
    }

    private void BindAll(
        List<Incident> incidents,
        List<ThreatCampaign> campaigns,
        List<object> agentsJson,
        List<ThreatCatalogEntry>? catalog,
        bool useDemoAgents = false)
    {
        _incidents = incidents.OrderByDescending(i => i.LastSeen ?? i.FirstSeen ?? DateTimeOffset.MinValue).ToList();
        _campaigns = campaigns.OrderByDescending(c => c.LastSeenUtc).ToList();

        IncidentsGrid.ItemsSource = _incidents;
        OverviewIncidentsGrid.ItemsSource = _incidents.Select(i => new IncidentRow(i)).ToList();
        IncidentBadge.Text = _incidents.Count.ToString();

        KpiIncidents.Text = _incidents.Count(i => i.Status is null or "Open").ToString();
        KpiCampaigns.Text = _campaigns.Count.ToString();
        KpiMultiHop.Text = _campaigns.Count(c => c.Hops.Count >= 2).ToString();

        if (useDemoAgents)
        {
            var agents = DemoData.Agents();
            AgentsGrid.ItemsSource = agents;
            KpiAgents.Text = agents.Count.ToString();
        }
        else
        {
            BindAgentsFromJson(agentsJson);
            KpiAgents.Text = Math.Max(agentsJson.Count, 0).ToString();
            if (agentsJson.Count == 0)
            {
                AgentsGrid.ItemsSource = DemoData.Agents();
                KpiAgents.Text = DemoData.Agents().Count.ToString();
            }
        }

        CatalogGrid.ItemsSource = catalog is { Count: > 0 } ? catalog : DemoCatalog();

        // Analytics widgets from incidents
        var topSources = _incidents
            .Where(i => !string.IsNullOrWhiteSpace(i.SourceIp))
            .GroupBy(i => i.SourceIp!)
            .Select(g => new Kv(g.Key, g.Sum(x => Math.Max(1, x.FailedAttempts))))
            .OrderByDescending(x => x.Value).Take(5).ToList();
        TopSourcesList.ItemsSource = topSources;

        var topDest = _incidents
            .Where(i => !string.IsNullOrWhiteSpace(i.DestinationIp))
            .GroupBy(i => i.DestinationIp!)
            .Select(g => new Kv(g.Key, g.Sum(x => Math.Max(1, x.FailedAttempts))))
            .OrderByDescending(x => x.Value).Take(5).ToList();
        TopDestList.ItemsSource = topDest;

        var peakFailed = _incidents.Sum(i => i.FailedAttempts);
        FailedLoginPeak.Text = peakFailed > 0 ? peakFailed.ToString() : _incidents.Count.ToString();
        SprayUsersText.Text = _incidents.Max(i => (int?)i.DistinctUsernames)?.ToString() ?? "0";
        ProcessEventsText.Text = _incidents.Count(i => i.ProcessId is > 0).ToString();

        // Campaigns list
        CampaignsList.ItemsSource = _campaigns.Select(c => new CampaignRow
        {
            Campaign = c,
            Display = $"{c.Title}\n  {c.Severity} · hops={c.Hops.Count} · {string.Join(" -> ", c.InvolvedIps.Take(4))}"
        }).ToList();
        CampaignsList.DisplayMemberPath = "Display";

        // Response actions (derived / demo)
        Actions.Clear();
        if (_incidents.Count > 0)
        {
            var top = _incidents[0];
            Actions.Add(new ResponseActionRow("just now", "LogOnly", top.DestinationIp ?? "-", top.RuleId, "Completed"));
            Actions.Add(new ResponseActionRow("—", "Capture Evidence", top.DestinationIp ?? "-", "Operator", "PendingApproval"));
        }
        else
        {
            foreach (var a in DemoActions()) Actions.Add(a);
        }

        if (_incidents.Count > 0)
        {
            OverviewIncidentsGrid.SelectedIndex = 0;
            IncidentsGrid.SelectedIndex = 0;
            ShowIncident(_incidents[0]);
        }

        if (_campaigns.Count > 0)
        {
            CampaignsList.SelectedIndex = 0;
            ShowCampaign(_campaigns[0]);
        }
    }

    private void BindAgentsFromJson(List<object> agentsJson)
    {
        var rows = new List<AgentRow>();
        foreach (var item in agentsJson)
        {
            if (item is not JsonElement el) continue;
            rows.Add(new AgentRow(
                GetString(el, "agentId", "AgentId") ?? "?",
                GetString(el, "computerName", "ComputerName") ?? "?",
                GetString(el, "hostIp", "HostIp"),
                GetString(el, "agentVersion", "AgentVersion", "Version"),
                GetString(el, "status", "Status"),
                GetTime(el, "lastSeenUtc", "LastSeenUtc") ?? DateTimeOffset.MinValue));
        }

        AgentsGrid.ItemsSource = rows;
    }

    private void SetStatus(bool online, bool demo)
    {
        if (online && !demo)
        {
            ProtectedTitle.Text = "Protected";
            ProtectedSub.Text = "All systems operational · Live";
            SidebarStatusText.Text = "Healthy";
        }
        else if (demo)
        {
            ProtectedTitle.Text = "Protected";
            ProtectedSub.Text = online ? "Live API empty — showing demo" : "Demo mode";
            SidebarStatusText.Text = "Healthy";
        }
        else
        {
            ProtectedTitle.Text = "Offline";
            ProtectedSub.Text = "Central server unreachable";
            SidebarStatusText.Text = "Unreachable";
        }
    }

    private void IncidentsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Incident? incident = null;
        if (sender is DataGrid grid)
            incident = grid.SelectedItem as Incident ?? (grid.SelectedItem as IncidentRow)?.Source;
        if (incident is not null) ShowIncident(incident);
    }

    private void CampaignsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CampaignsList.SelectedItem is CampaignRow row) ShowCampaign(row.Campaign);
    }

    private void ShowIncident(Incident i)
    {
        DetailSeverityText.Text = i.Severity.ToString();
        DetailSeverityBadge.Background = i.Severity switch
        {
            Shared.Enums.Severity.Critical or Shared.Enums.Severity.High =>
                new SolidColorBrush(Color.FromRgb(0xF0, 0x44, 0x44)),
            Shared.Enums.Severity.Medium =>
                new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x1A)),
            _ => new SolidColorBrush(Color.FromRgb(0xFF, 0xE9, 0xBB))
        };
        D_Title.Text = i.Title;
        D_Source.Text = i.SourceIp ?? "—";
        D_Dest.Text = i.DestinationIp ?? i.DestinationHost ?? "—";
        D_Process.Text = i.ProcessName is null ? "—" : $"{i.ProcessName} (PID {i.ProcessId})";
        D_Service.Text = i.Services.Count > 0 ? string.Join(", ", i.Services) : (i.SourceServiceNames ?? "—");
        D_Logon.Text = i.LogonType?.ToString() ?? "—";
        D_Failed.Text = i.FailedAttempts.ToString();
        D_Users.Text = i.DistinctUsernames.ToString();
        D_Success.Text = i.SuccessfulLoginDetected.ToString();
    }

    private void ShowCampaign(ThreatCampaign c)
    {
        var sb = new StringBuilder();
        for (var idx = 0; idx < c.Hops.Count; idx++)
        {
            var h = c.Hops[idx];
            sb.AppendLine($"{idx + 1}. {h.FromHost ?? h.FromIp} --[{h.Technique}:{h.Port}]--> {h.ToHost ?? h.ToIp}");
            if (h.ProcessId is not null)
                sb.AppendLine($"   process {h.ProcessName} PID={h.ProcessId} svc={h.ServiceNames}");
        }

        PathVisualText.Text = sb.Length == 0 ? "(no hops)" : sb.ToString().TrimEnd();
        CampaignDetailText.Text = c.FormatDisplay();
    }

    private static List<ThreatCatalogEntry> DemoCatalog() =>
    [
        new() { Category = "credential_access", Name = "Internal Password Spray", DetectionRuleId = "INTERNAL_PASSWORD_SPRAY", MitreTechnique = "T1110.003", CrossHostTracking = "Source process → dest 4625" },
        new() { Category = "execution", Name = "Process From Temp", DetectionRuleId = "PROCESS_FROM_TEMP_PATH", MitreTechnique = "T1204", CrossHostTracking = "4688" },
        new() { Category = "lateral_movement", Name = "Network Logon Burst", DetectionRuleId = "NETWORK_LOGON_BURST", MitreTechnique = "T1021.002", CrossHostTracking = "Type 3 chain" }
    ];

    private static IEnumerable<ResponseActionRow> DemoActions() =>
    [
        new("2 min ago", "Investigate", "10.0.105.190", "Auto Response (Password Spray)", "Completed"),
        new("3 min ago", "Capture Evidence", "10.0.105.190", "Auto Response (Password Spray)", "Completed"),
        new("5 min ago", "Block Destination", "10.0.105.190", "Pending approval", "PendingApproval")
    ];

    private static string? GetString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
        return null;
    }

    private static DateTimeOffset? GetTime(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(p.GetString(), out var dto))
                return dto;
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoRefresh.Stop();
        _api.Dispose();
        base.OnClosed(e);
    }

    private sealed class IncidentRow
    {
        public Incident Source { get; }
        public string Severity => Source.Severity.ToString();
        public string Title => Source.Title;
        public string Route => $"{Source.SourceIp} → {Source.DestinationIp}";
        public int FailedAttempts => Source.FailedAttempts;
        public IncidentRow(Incident s) => Source = s;
    }

    private sealed class CampaignRow
    {
        public required ThreatCampaign Campaign { get; init; }
        public string Display { get; init; } = "";
    }

    private sealed record Kv(string Key, int Value);

    public sealed class ResponseActionRow
    {
        public ResponseActionRow(string time, string action, string target, string triggeredBy, string status)
        {
            Time = time; Action = action; Target = target; TriggeredBy = triggeredBy; Status = status;
        }
        public string Time { get; }
        public string Action { get; }
        public string Target { get; }
        public string TriggeredBy { get; }
        public string Status { get; }
    }
}
