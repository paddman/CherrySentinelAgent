using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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

    public MainWindow()
    {
        InitializeComponent();
        _api = new CentralApiClient(ServerUrlBox.Text.Trim());
        SettingsUrlBox.Text = ServerUrlBox.Text;

        _pages = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dashboard"] = PageDashboard,
            ["Incidents"] = PageIncidents,
            ["Paths"] = PagePaths,
            ["Agents"] = PageAgents,
            ["Catalog"] = PageCatalog,
            ["Settings"] = PageSettings
        };
        _navButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dashboard"] = NavDashboard,
            ["Incidents"] = NavIncidents,
            ["Paths"] = NavPaths,
            ["Agents"] = NavAgents,
            ["Catalog"] = NavCatalog,
            ["Settings"] = NavSettings
        };

        _autoRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoRefresh.Tick += async (_, _) => await RefreshAsync(silent: true);
        _autoRefresh.Start();
        Loaded += async (_, _) => await RefreshAsync();
        ShowPage("Dashboard");
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            ShowPage(tag);
        }
    }

    private void ShowPage(string name)
    {
        foreach (var kv in _pages)
        {
            kv.Value.Visibility = kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        foreach (var kv in _navButtons)
        {
            kv.Value.Style = (Style)FindResource(
                kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ? "NavBtnActive" : "NavBtn");
        }

        PageTitle.Text = name switch
        {
            "Paths" => "Lateral Paths",
            "Agents" => "Endpoints",
            "Catalog" => "Detections / Rules",
            _ => name
        };
        PageSubtitle.Text = name switch
        {
            "Dashboard" => "Security overview · detect & track",
            "Incidents" => "Correlated security incidents",
            "Paths" => "Cross-host threat campaigns",
            "Agents" => "Registered endpoints",
            "Catalog" => "Detection rule coverage",
            "Settings" => "Server connection & mode",
            _ => ""
        };
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void DemoBtn_Click(object sender, RoutedEventArgs e)
    {
        ApplyDemo();
        SetStatus(online: false, demo: true);
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
            RefreshBtn.IsEnabled = false;
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
                if (!_usingDemo)
                {
                    ApplyDemo();
                }

                SetStatus(online: false, demo: _usingDemo);
                KpiMode.Text = _usingDemo ? "Mode: Demo data" : "Mode: Server offline";
                return;
            }

            _usingDemo = false;
            var incidents = await _api.GetIncidentsAsync();
            var threats = await _api.GetThreatsAsync();
            var agentsJson = await _api.GetAgentsAsync();
            var catalog = await _api.GetCatalogAsync();

            if (incidents.Count == 0 && threats.Count == 0)
            {
                ApplyDemo();
                SetStatus(online: true, demo: true);
                KpiMode.Text = "Mode: Live (empty → demo)";
                return;
            }

            BindIncidents(incidents);
            BindCampaigns(threats);
            BindAgentsFromJson(agentsJson);
            CatalogGrid.ItemsSource = catalog.Count > 0 ? catalog : DemoCatalog();
            UpdateKpis(incidents, threats, CountAgents(agentsJson));
            SetStatus(online: true, demo: false);
            KpiMode.Text = "Mode: Live API";
            LastRefreshText.Text = $"Updated {DateTime.Now:HH:mm:ss}";
            SidebarCheckinText.Text = $"Last check-in: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            SetStatus(online: false, demo: _usingDemo);
        }
        finally
        {
            RefreshBtn.IsEnabled = true;
        }
    }

    private void ApplyDemo()
    {
        _usingDemo = true;
        var incidents = DemoData.Incidents();
        var campaigns = DemoData.Campaigns();
        var agents = DemoData.Agents();
        BindIncidents(incidents);
        BindCampaigns(campaigns);
        AgentsGrid.ItemsSource = agents;
        CatalogGrid.ItemsSource = DemoCatalog();
        UpdateKpis(incidents, campaigns, agents.Count);
        KpiMode.Text = "Mode: Demo data";
        LastRefreshText.Text = $"Demo {DateTime.Now:HH:mm:ss}";
        SidebarStatusText.Text = "Healthy (demo)";
        SidebarCheckinText.Text = "Last check-in: 1 min ago";
        PageSubtitle.Text = "Demo · 10.0.105.35 → .190 → .200";
    }

    private void BindIncidents(List<Incident> incidents)
    {
        _incidents = incidents
            .OrderByDescending(i => i.LastSeen ?? i.FirstSeen ?? DateTimeOffset.MinValue)
            .ToList();

        var rows = _incidents.Select(i => new IncidentRow(i)).ToList();
        IncidentsGrid.ItemsSource = _incidents;
        OverviewIncidentsGrid.ItemsSource = rows;

        if (_incidents.Count > 0)
        {
            IncidentsGrid.SelectedIndex = 0;
            OverviewIncidentsGrid.SelectedIndex = 0;
            ShowIncident(_incidents[0]);
        }
        else
        {
            SetDetail("No incidents.");
        }
    }

    private void BindCampaigns(List<ThreatCampaign> campaigns)
    {
        _campaigns = campaigns.OrderByDescending(c => c.LastSeenUtc).ToList();
        var rows = _campaigns.Select(c => new CampaignRow
        {
            Campaign = c,
            Title = c.Title,
            Severity = c.Severity.ToString(),
            HopCount = c.Hops.Count,
            PathSummary = string.Join(" -> ", c.Hops.Select(h => (h.FromIp ?? "?") + ">>" + (h.ToIp ?? "?")))
        }).ToList();
        CampaignsList.ItemsSource = rows;
        if (rows.Count > 0)
        {
            CampaignsList.SelectedIndex = 0;
            ShowCampaign(rows[0].Campaign);
        }
        else
        {
            PathVisualText.Text = "No lateral paths yet.";
            CampaignDetailText.Text = "Install agents on source and destination hosts.";
            PathPreviewText.Text = "No path loaded";
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
                GetTime(el, "lastSeenUtc", "LastSeenUtc") ?? DateTimeOffset.MinValue
            ));
        }

        AgentsGrid.ItemsSource = rows.Count > 0 ? rows : DemoData.Agents();
    }

    private static int CountAgents(List<object> agentsJson) =>
        agentsJson.Count > 0 ? agentsJson.Count : DemoData.Agents().Count;

    private void UpdateKpis(List<Incident> incidents, List<ThreatCampaign> campaigns, int agentCount)
    {
        KpiIncidents.Text = incidents.Count(i => i.Status is null or "Open").ToString();
        KpiCampaigns.Text = campaigns.Count.ToString();
        KpiMultiHop.Text = campaigns.Count(c => c.Hops.Count >= 2).ToString();
        KpiAgents.Text = agentCount.ToString();
    }

    private void SetStatus(bool online, bool demo)
    {
        if (online && !demo)
        {
            ProtectedBadge.Text = "Protected · Online";
            SidebarStatusText.Text = "Healthy";
            ProtectedBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0x65, 0x34));
        }
        else if (demo)
        {
            ProtectedBadge.Text = online ? "Protected · Demo" : "Demo Mode";
            SidebarStatusText.Text = "Healthy (demo)";
        }
        else
        {
            ProtectedBadge.Text = "Offline";
            SidebarStatusText.Text = "Unreachable";
        }
    }

    private void IncidentsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Incident? incident = null;
        if (sender is DataGrid grid)
        {
            incident = grid.SelectedItem as Incident
                       ?? (grid.SelectedItem as IncidentRow)?.Source;
        }

        if (incident is not null)
        {
            ShowIncident(incident);
        }
    }

    private void CampaignsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CampaignsList.SelectedItem is CampaignRow row)
        {
            ShowCampaign(row.Campaign);
        }
    }

    private void ShowIncident(Incident i)
    {
        SetDetail(i.FormatDisplay());
    }

    private void SetDetail(string text)
    {
        IncidentDetailText.Text = text;
        IncidentDetailText2.Text = text;
    }

    private void ShowCampaign(ThreatCampaign c)
    {
        var path = new StringBuilder();
        if (c.Hops.Count == 0)
        {
            path.AppendLine("(no hops)");
        }
        else
        {
            for (var idx = 0; idx < c.Hops.Count; idx++)
            {
                var h = c.Hops[idx];
                path.AppendLine($"{idx + 1}. {h.FromHost ?? h.FromIp}  ──[{h.Technique} :{h.Port}]──►  {h.ToHost ?? h.ToIp}");
                if (h.ProcessId is not null || !string.IsNullOrEmpty(h.ProcessName))
                {
                    path.AppendLine($"     process: {h.ProcessName} PID={h.ProcessId}  services: {h.ServiceNames}");
                }

                if (!string.IsNullOrEmpty(h.Username))
                {
                    path.AppendLine($"     user: {h.Username}  logonType: {h.LogonType}");
                }

                path.AppendLine();
            }
        }

        PathVisualText.Text = path.ToString().TrimEnd();
        CampaignDetailText.Text = c.FormatDisplay();
        PathPreviewText.Text = path.ToString().TrimEnd();
    }

    private static List<ThreatCatalogEntry> DemoCatalog() =>
    [
        new() { Category = "credential_access", Name = "Internal Password Spray", DetectionRuleId = "INTERNAL_PASSWORD_SPRAY", MitreTechnique = "T1110.003", CrossHostTracking = "Source process/service → dest 4625" },
        new() { Category = "execution", Name = "Process From Temp Path", DetectionRuleId = "PROCESS_FROM_TEMP_PATH", MitreTechnique = "T1204", CrossHostTracking = "4688 path anomaly" },
        new() { Category = "lateral_movement", Name = "Network Logon Burst", DetectionRuleId = "NETWORK_LOGON_BURST", MitreTechnique = "T1021.002", CrossHostTracking = "Type 3 success chain" },
        new() { Category = "persistence", Name = "New Service", DetectionRuleId = "NEW_SERVICE_INSTALLED", MitreTechnique = "T1543.003", CrossHostTracking = "After pivot foothold" }
    ];

    private static string? GetString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
        }

        return null;
    }

    private static DateTimeOffset? GetTime(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(p.GetString(), out var dto))
                return dto;
        }

        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoRefresh.Stop();
        _api.Dispose();
        base.OnClosed(e);
    }

    private sealed class CampaignRow
    {
        public required ThreatCampaign Campaign { get; init; }
        public string Title { get; init; } = "";
        public string Severity { get; init; } = "";
        public int HopCount { get; init; }
        public string PathSummary { get; init; } = "";
    }

    private sealed class IncidentRow
    {
        public Incident Source { get; }
        public string Severity => Source.Severity.ToString();
        public string Title => Source.Title;
        public string Route => $"{Source.SourceIp} → {Source.DestinationIp}";
        public int FailedAttempts => Source.FailedAttempts;
        public IncidentRow(Incident source) => Source = source;
    }
}
