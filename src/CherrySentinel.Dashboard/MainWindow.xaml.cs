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

        ClearAll();
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
            "Detect-only: response actions require Central approval API.\nNo mock execution.",
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
                ClearAll();
                SetStatus(online: false);
                KpiMode.Text = "Mode: Server offline";
                ModeLabel.Text = "Offline — no data";
                SidebarStatusText.Text = "Unreachable";
                SidebarCheckinText.Text = "Last check-in: —";
                if (!silent)
                {
                    ProtectedSub.Text = "Cannot reach Central API — showing empty (no mock data)";
                }

                return;
            }

            var incidents = await _api.GetIncidentsAsync();
            var threats = await _api.GetThreatsAsync();
            var agentsJson = await _api.GetAgentsAsync();
            var catalog = await _api.GetCatalogAsync();

            BindAll(incidents, threats, agentsJson, catalog);
            SetStatus(online: true);
            KpiMode.Text = "Mode: Live API";
            ModeLabel.Text = "Live · " + DateTime.Now.ToString("HH:mm:ss");
            LastRefreshText.Text = $"Updated {DateTime.Now:HH:mm:ss}";
            SidebarCheckinText.Text = $"Last check-in: {DateTime.Now:HH:mm:ss}";
            SidebarStatusText.Text = "Healthy";
        }
        catch (Exception ex)
        {
            ClearAll();
            SetStatus(online: false);
            KpiMode.Text = "Mode: Error";
            ModeLabel.Text = "Error";
            if (!silent)
                MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearAll()
    {
        _incidents = [];
        _campaigns = [];
        IncidentsGrid.ItemsSource = null;
        OverviewIncidentsGrid.ItemsSource = null;
        AgentsGrid.ItemsSource = null;
        CatalogGrid.ItemsSource = null;
        CampaignsList.ItemsSource = null;
        TopSourcesList.ItemsSource = null;
        TopDestList.ItemsSource = null;
        Actions.Clear();
        IncidentBadge.Text = "0";
        KpiIncidents.Text = "0";
        KpiCampaigns.Text = "0";
        KpiMultiHop.Text = "0";
        KpiAgents.Text = "0";
        FailedLoginPeak.Text = "0";
        SprayUsersText.Text = "0";
        ProcessEventsText.Text = "0";
        PathVisualText.Text = "No data";
        CampaignDetailText.Text = "Connect to Central API and wait for agent telemetry.";
        ClearIncidentDetail();
    }

    private void ClearIncidentDetail()
    {
        DetailSeverityText.Text = "—";
        D_Title.Text = "—";
        D_Source.Text = "—";
        D_Dest.Text = "—";
        D_Process.Text = "—";
        D_Service.Text = "—";
        D_Logon.Text = "—";
        D_Failed.Text = "—";
        D_Users.Text = "—";
        D_Success.Text = "—";
    }

    private void BindAll(
        List<Incident> incidents,
        List<ThreatCampaign> campaigns,
        List<object> agentsJson,
        List<ThreatCatalogEntry> catalog)
    {
        _incidents = incidents
            .OrderByDescending(i => i.LastSeen ?? i.FirstSeen ?? DateTimeOffset.MinValue)
            .ToList();
        _campaigns = campaigns.OrderByDescending(c => c.LastSeenUtc).ToList();

        IncidentsGrid.ItemsSource = _incidents;
        OverviewIncidentsGrid.ItemsSource = _incidents.Select(i => new IncidentRow(i)).ToList();
        IncidentBadge.Text = _incidents.Count.ToString();

        KpiIncidents.Text = _incidents.Count(i => i.Status is null or "Open").ToString();
        KpiCampaigns.Text = _campaigns.Count.ToString();
        KpiMultiHop.Text = _campaigns.Count(c => c.Hops.Count >= 2).ToString();

        var agents = ParseAgents(agentsJson);
        AgentsGrid.ItemsSource = agents;
        KpiAgents.Text = agents.Count.ToString();

        CatalogGrid.ItemsSource = catalog;

        TopSourcesList.ItemsSource = _incidents
            .Where(i => !string.IsNullOrWhiteSpace(i.SourceIp))
            .GroupBy(i => i.SourceIp!)
            .Select(g => new Kv(g.Key, g.Sum(x => Math.Max(1, x.FailedAttempts))))
            .OrderByDescending(x => x.Value)
            .Take(5)
            .ToList();

        TopDestList.ItemsSource = _incidents
            .Where(i => !string.IsNullOrWhiteSpace(i.DestinationIp))
            .GroupBy(i => i.DestinationIp!)
            .Select(g => new Kv(g.Key, g.Sum(x => Math.Max(1, x.FailedAttempts))))
            .OrderByDescending(x => x.Value)
            .Take(5)
            .ToList();

        var peakFailed = _incidents.Sum(i => i.FailedAttempts);
        FailedLoginPeak.Text = peakFailed.ToString();
        SprayUsersText.Text = (_incidents.Count == 0 ? 0 : _incidents.Max(i => i.DistinctUsernames)).ToString();
        ProcessEventsText.Text = _incidents.Count(i => i.ProcessId is > 0).ToString();

        CampaignsList.ItemsSource = _campaigns.Select(c => new CampaignRow
        {
            Campaign = c,
            Display = $"{c.Title}\n  {c.Severity} · hops={c.Hops.Count} · {string.Join(" -> ", c.InvolvedIps.Take(4))}"
        }).ToList();
        CampaignsList.DisplayMemberPath = "Display";

        Actions.Clear();
        // Only real-ish derived rows from live incidents (log-only), never fake history
        foreach (var top in _incidents.Take(5))
        {
            Actions.Add(new ResponseActionRow(
                RelTime(top.LastSeen ?? top.FirstSeen),
                "LogOnly",
                top.DestinationIp ?? top.DestinationHost ?? "-",
                top.RuleId,
                "Completed"));
        }

        if (_incidents.Count > 0)
        {
            OverviewIncidentsGrid.SelectedIndex = 0;
            IncidentsGrid.SelectedIndex = 0;
            ShowIncident(_incidents[0]);
        }
        else
        {
            ClearIncidentDetail();
        }

        if (_campaigns.Count > 0)
        {
            CampaignsList.SelectedIndex = 0;
            ShowCampaign(_campaigns[0]);
        }
        else
        {
            PathVisualText.Text = "No lateral paths";
            CampaignDetailText.Text = "No threat campaigns from Central yet.";
        }
    }

    private static List<AgentRow> ParseAgents(List<object> agentsJson)
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

        return rows;
    }

    private void SetStatus(bool online)
    {
        if (online)
        {
            ProtectedTitle.Text = "Protected";
            ProtectedSub.Text = "Connected to Central API";
        }
        else
        {
            ProtectedTitle.Text = "Offline";
            ProtectedSub.Text = "Central server unreachable";
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
            _ => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
        };
        D_Title.Text = string.IsNullOrWhiteSpace(i.Title) ? "—" : i.Title;
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

    private static string RelTime(DateTimeOffset? ts)
    {
        if (ts is null) return "—";
        var d = DateTimeOffset.UtcNow - ts.Value;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} hr ago";
        return ts.Value.ToString("g");
    }

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

    private sealed record AgentRow(
        string AgentId,
        string ComputerName,
        string? HostIp,
        string? Version,
        string? Status,
        DateTimeOffset LastSeenUtc);

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
