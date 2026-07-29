using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CherrySentinel.Dashboard.Services;
using CherrySentinel.Shared;
using CherrySentinel.Shared.Contracts;
using CherrySentinel.Shared.Models;
using System.Windows.Shapes;

namespace CherrySentinel.Dashboard;

public partial class MainWindow : Window
{
    private CentralApiClient _api;
    private readonly DispatcherTimer _autoRefresh;
    private readonly DispatcherTimer _deadlineTimer;
    private List<Incident> _incidents = [];
    private List<ThreatCampaign> _campaigns = [];
    private readonly Dictionary<string, FrameworkElement> _pages;
    private readonly Dictionary<string, Button> _navButtons;
    private readonly ObservableCollection<string> _pendingSubtasks = new();
    private readonly ObservableCollection<DeadlineTaskRow> _deadlineTasks = new();

    public ObservableCollection<ResponseActionRow> Actions { get; } = new();
    public ObservableCollection<ResponseActionRow> FwSessionActions { get; } = new();
    private List<AgentRow> _agents = [];

    public MainWindow()
    {
        InitializeComponent();
        _api = new CentralApiClient(ServerUrlBox.Text.Trim());
        SettingsUrlBox.Text = ServerUrlBox.Text;
        ActionsGrid.ItemsSource = Actions;
        FwActionsGrid.ItemsSource = FwSessionActions;
        PendingSubtasksList.ItemsSource = _pendingSubtasks;
        DeadlineTasksGrid.ItemsSource = _deadlineTasks;
        DeadlineDatePicker.SelectedDate = DateTime.Today;
        UpdatePendingSubtaskHint();
        RefreshDeadlineEmptyState();

        _pages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Dashboard"] = PageDashboard,
            ["Deadline"] = PageDeadline,
            ["Incidents"] = PageIncidents,
            ["Paths"] = PagePaths,
            ["Agents"] = PageAgents,
            ["Catalog"] = PageCatalog,
            ["Firewall"] = PageFirewall,
            ["Settings"] = PageSettings
        };
        _navButtons = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Dashboard"] = NavDashboard,
            ["Deadline"] = NavDeadline,
            ["Incidents"] = NavIncidents,
            ["Paths"] = NavPaths,
            ["Agents"] = NavEndpoints,
            ["Catalog"] = NavRules,
            ["Firewall"] = NavFirewall,
            ["Settings"] = NavSettings
        };

        ClearAll();
        var dashVer = ProductInfo.GetVersion();
        Title = $"Cherry Sentinel Dashboard  v{dashVer}";
        SidebarVersionText.Text = $"Dashboard v{dashVer}";
        SidebarCentralVersionText.Text = "Central v—";
        AboutDashboardVersion.Text = $"Dashboard (this app): v{dashVer}";
        AboutCentralVersion.Text = "Central: (not connected)";
        ModeLabel.Text = "Live only — waiting for Central";
        ProtectedSub.Text = $"Dashboard v{dashVer} · Connecting to Central…";
        SidebarStatusText.Text = "—";
        _autoRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoRefresh.Tick += async (_, _) => await RefreshAsync(silent: true);
        _autoRefresh.Start();
        _deadlineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _deadlineTimer.Tick += (_, _) => UpdateDeadlineCountdowns();
        _deadlineTimer.Start();
        UpdateDeadlineCountdowns();
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
            "Use Firewall panel for live block/open/close port.\nCapture Evidence still requires agent-side packing via approved action.",
            "Cherry Sentinel",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenFirewallPanel_Click(object sender, RoutedEventArgs e) => ShowPage("Firewall");

    private async void QuickBlockDest_Click(object sender, RoutedEventArgs e)
    {
        FwDestIpBox.Text = D_Dest.Text is "—" or "" ? FwDestIpBox.Text : D_Dest.Text;
        ShowPage("Firewall");
        await FwBlockDest_ClickAsync();
    }

    private async void QuickBlockSource_Click(object sender, RoutedEventArgs e)
    {
        FwSourceIpBox.Text = D_Source.Text is "—" or "" ? FwSourceIpBox.Text : D_Source.Text;
        ShowPage("Firewall");
        await FwBlockSource_ClickAsync();
    }

    private async void FwBlockSource_Click(object sender, RoutedEventArgs e) => await FwBlockSource_ClickAsync();
    private async void FwBlockDest_Click(object sender, RoutedEventArgs e) => await FwBlockDest_ClickAsync();
    private async void FwOpenPort_Click(object sender, RoutedEventArgs e) => await FwOpenPort_ClickAsync();
    private async void FwBlockPort_Click(object sender, RoutedEventArgs e) => await FwBlockPort_ClickAsync();
    private async void FwClosePort_Click(object sender, RoutedEventArgs e) => await FwClosePort_ClickAsync();

    private async Task FwBlockSource_ClickAsync()
    {
        var ip = FwSourceIpBox.Text.Trim();
        if (!ConfirmFirewall($"Block SOURCE IP (inbound)\n{ip}")) return;
        await SubmitFirewallActionAsync(new ResponseActionRequest
        {
            ActionType = "BlockSourceIp",
            TargetIp = ip,
            Direction = "in",
            Reason = string.IsNullOrWhiteSpace(FwIpReasonBox.Text) ? "Dashboard block source IP" : FwIpReasonBox.Text.Trim(),
            Approved = true
        });
    }

    private async Task FwBlockDest_ClickAsync()
    {
        var ip = FwDestIpBox.Text.Trim();
        if (!ConfirmFirewall($"Block DESTINATION IP (outbound)\n{ip}")) return;
        await SubmitFirewallActionAsync(new ResponseActionRequest
        {
            ActionType = "BlockDestinationIp",
            TargetIp = ip,
            Direction = "out",
            Reason = string.IsNullOrWhiteSpace(FwIpReasonBox.Text) ? "Dashboard block dest IP" : FwIpReasonBox.Text.Trim(),
            Approved = true
        });
    }

    private async Task FwOpenPort_ClickAsync()
    {
        if (!int.TryParse(FwPortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Port must be 1-65535", "Firewall", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var proto = (FwProtoBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "tcp";
        var dir = (FwDirBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "in";
        if (!ConfirmFirewall($"OPEN (allow) port {port}/{proto} dir={dir}")) return;
        await SubmitFirewallActionAsync(new ResponseActionRequest
        {
            ActionType = "OpenPort",
            TargetPort = port,
            Protocol = proto,
            Direction = dir,
            Reason = "Dashboard open port",
            Approved = true
        });
    }

    private async Task FwBlockPort_ClickAsync()
    {
        if (!int.TryParse(FwPortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Port must be 1-65535", "Firewall", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var proto = (FwProtoBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "tcp";
        var dir = (FwDirBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "in";
        var remote = FwPortRemoteIpBox.Text.Trim();
        if (!ConfirmFirewall($"BLOCK port {port}/{proto} dir={dir}" + (remote == "" ? "" : $" remote={remote}"))) return;
        await SubmitFirewallActionAsync(new ResponseActionRequest
        {
            ActionType = "BlockPort",
            TargetPort = port,
            Protocol = proto,
            Direction = dir,
            TargetIp = string.IsNullOrWhiteSpace(remote) ? null : remote,
            Reason = "Dashboard block port",
            Approved = true
        });
    }

    private async Task FwClosePort_ClickAsync()
    {
        var rule = FwRuleNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rule) || !rule.StartsWith("CSA-", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "Close/Remove needs CSA- rule name (shown in agent result / netsh).\nExample: CSA-Block-Dst-1.2.3.4-abcd1234",
                "Firewall", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!ConfirmFirewall($"CLOSE / remove rule\n{rule}")) return;
        await SubmitFirewallActionAsync(new ResponseActionRequest
        {
            ActionType = "ClosePort",
            RuleName = rule,
            Reason = "Dashboard close/remove firewall rule",
            Approved = true
        });
    }

    private bool ConfirmFirewall(string detail)
    {
        var r = MessageBox.Show(
            this,
            detail + "\n\nThis queues an APPROVED action to the selected Agent via Central.\nAgent will run netsh on next heartbeat.",
            "Confirm firewall action",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return r == MessageBoxResult.Yes;
    }

    private async Task SubmitFirewallActionAsync(ResponseActionRequest req)
    {
        try
        {
            if (FwAgentBox.SelectedItem is not AgentPick pick)
            {
                MessageBox.Show(this, "Select a target Agent first (Endpoints must be online).", "Firewall",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            req.TargetAgentId = pick.AgentId;
            req.RequestId = Guid.NewGuid().ToString("N");
            req.ApprovalId = Guid.NewGuid().ToString("N");
            req.Approved = true;
            req.Requester = "dashboard-operator";

            var (ok, msg, saved) = await _api.PostActionAsync(req);
            var target = $"{req.ActionType} {req.TargetIp ?? ""} {req.TargetPort?.ToString() ?? req.RuleName ?? ""}".Trim();
            FwSessionActions.Insert(0, new ResponseActionRow(
                DateTime.Now.ToString("HH:mm:ss"),
                req.ActionType,
                target,
                pick.ComputerName + " / " + pick.AgentId,
                ok ? "Queued→Agent" : "Failed"));
            Actions.Insert(0, new ResponseActionRow(
                DateTime.Now.ToString("HH:mm:ss"),
                req.ActionType,
                target,
                "Dashboard",
                ok ? "Queued" : "Failed"));

            FwStatusText.Text = ok
                ? $"OK: {msg}. RequestId={saved?.RequestId ?? req.RequestId}. Agent applies on next heartbeat."
                : $"FAILED: {msg}";
            if (!ok)
            {
                MessageBox.Show(this, msg, "Firewall action failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            FwStatusText.Text = "Error: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Firewall", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddSubtask_Click(object sender, RoutedEventArgs e)
    {
        var text = DeadlineSubtaskBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "กรุณากรอกชื่อ subtask ก่อนเพิ่ม", "Deadline Countdown",
                MessageBoxButton.OK, MessageBoxImage.Information);
            DeadlineSubtaskBox.Focus();
            return;
        }

        _pendingSubtasks.Add(text);
        DeadlineSubtaskBox.Clear();
        UpdatePendingSubtaskHint();
        DeadlineSubtaskBox.Focus();
    }

    private void RemoveSubtask_Click(object sender, RoutedEventArgs e)
    {
        if (PendingSubtasksList.SelectedItem is not string selected) return;
        _pendingSubtasks.Remove(selected);
        UpdatePendingSubtaskHint();
    }

    private void AddDeadlineTask_Click(object sender, RoutedEventArgs e)
    {
        var taskName = DeadlineTaskNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(taskName))
        {
            MessageBox.Show(this, "กรุณากรอกชื่องาน", "Deadline Countdown",
                MessageBoxButton.OK, MessageBoxImage.Information);
            DeadlineTaskNameBox.Focus();
            return;
        }

        if (_pendingSubtasks.Count == 0)
        {
            MessageBox.Show(this, "กรุณาเพิ่ม subtask อย่างน้อย 1 รายการ", "Deadline Countdown",
                MessageBoxButton.OK, MessageBoxImage.Information);
            DeadlineSubtaskBox.Focus();
            return;
        }

        if (!TryReadDeadline(out var deadline, out var error))
        {
            MessageBox.Show(this, error, "Deadline Countdown",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            DeadlineTimeBox.Focus();
            return;
        }

        var row = new DeadlineTaskRow(taskName, deadline, _pendingSubtasks.ToList());
        _deadlineTasks.Add(row);
        DeadlineTasksGrid.SelectedItem = row;
        _pendingSubtasks.Clear();
        DeadlineTaskNameBox.Clear();
        DeadlineSubtaskBox.Clear();
        UpdatePendingSubtaskHint();
        UpdateDeadlineCountdowns();
    }

    private void RemoveDeadlineTask_Click(object sender, RoutedEventArgs e)
    {
        if (DeadlineTasksGrid.SelectedItem is not DeadlineTaskRow selected) return;
        _deadlineTasks.Remove(selected);
        RefreshDeadlineEmptyState();
    }

    private bool TryReadDeadline(out DateTimeOffset deadline, out string error)
    {
        deadline = default;
        error = "";
        if (DeadlineDatePicker.SelectedDate is not DateTime date)
        {
            error = "กรุณาเลือกวัน deadline";
            return false;
        }

        var timeText = DeadlineTimeBox.Text.Trim();
        var formats = new[] { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss" };
        if (!DateTime.TryParseExact(timeText, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
        {
            error = "เวลาไม่ถูกต้อง ใช้รูปแบบ HH:mm หรือ HH:mm:ss เช่น 18:30:00";
            return false;
        }

        var localDateTime = DateTime.SpecifyKind(date.Date.Add(time.TimeOfDay), DateTimeKind.Local);
        deadline = new DateTimeOffset(localDateTime);
        if (deadline <= DateTimeOffset.Now)
        {
            error = "deadline ต้องอยู่ในอนาคต";
            return false;
        }

        return true;
    }

    private void UpdatePendingSubtaskHint()
    {
        PendingSubtaskHint.Text = _pendingSubtasks.Count == 0
            ? "ยังไม่มี subtask"
            : $"พร้อมเพิ่ม {_pendingSubtasks.Count} รายการ";
    }

    private void RefreshDeadlineEmptyState()
    {
        DeadlineEmptyText.Visibility = _deadlineTasks.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDeadlineCountdowns()
    {
        var now = DateTimeOffset.Now;
        DeadlineClockText.Text = $"เวลาเครื่อง: {now:HH:mm:ss}";
        foreach (var task in _deadlineTasks)
            task.UpdateCountdown(now);

        DeadlineTasksGrid.Items.Refresh();
        RefreshDeadlineEmptyState();
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
        _api.SetApiKey(SettingsApiKeyBox.Text.Trim());
        await RefreshAsync();
    }

    private async Task RefreshAsync(bool silent = false)
    {
        try
        {
            var url = ServerUrlBox.Text.Trim();
            SettingsUrlBox.Text = url;
            var apiKey = SettingsApiKeyBox.Text.Trim();
            if (!string.Equals(_api.BaseUrl, url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_api.ApiKey ?? "", apiKey, StringComparison.Ordinal))
            {
                _api.Dispose();
                _api = new CentralApiClient(url, apiKey);
            }
            else
            {
                _api.SetApiKey(apiKey);
            }

            var health = await _api.GetHealthInfoAsync();
            var dashVer = ProductInfo.GetVersion();
            if (!health.Ok)
            {
                ClearAll();
                SetStatus(online: false);
                KpiMode.Text = "Mode: Server offline";
                ModeLabel.Text = "Offline — no data";
                SidebarStatusText.Text = "Unreachable";
                SidebarCheckinText.Text = "Last check-in: —";
                SidebarCentralVersionText.Text = "Central v— (offline)";
                AboutCentralVersion.Text = "Central: offline / unreachable";
                if (!silent)
                {
                    ProtectedSub.Text = $"Dashboard v{dashVer} · Cannot reach Central";
                }

                return;
            }

            var centralVer = health.Version ?? "?";
            SidebarCentralVersionText.Text = $"Central v{centralVer}";
            AboutDashboardVersion.Text = $"Dashboard (this app): v{dashVer}";
            AboutCentralVersion.Text = $"Central: v{centralVer}" +
                                       (string.IsNullOrWhiteSpace(health.Product) ? "" : $" ({health.Product})");

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
            ProtectedSub.Text = $"Dashboard v{dashVer} · Central v{centralVer}";
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
        ClearAgentDetail();
        AgKpiTotal.Text = "0";
        AgKpiOnline.Text = "0";
        AgKpiOffline.Text = "0 offline";
        AgKpiWin.Text = "0";
        AgKpiLinux.Text = "0";
        AgKpiAvg.Text = "— / —";
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
        FailedLoginPeakBadge.Visibility = Visibility.Collapsed;
        FailedLoginLine.Points = new PointCollection();
        FailedLoginFill.Points = new PointCollection();
        FailedLoginCanvas.Visibility = Visibility.Collapsed;
        FailedLoginEmptyHint.Visibility = Visibility.Visible;
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
        _agents = agents;
        AgentsGrid.ItemsSource = agents;
        KpiAgents.Text = agents.Count.ToString();
        BindAgentsFleetKpis(agents);
        RefreshFirewallAgentList(agents);
        if (agents.Count > 0)
        {
            AgentsGrid.SelectedIndex = 0;
            _ = ShowAgentDetailAsync(agents[0]);
        }
        else
        {
            ClearAgentDetail();
        }

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
        UpdateFailedLoginChart(_incidents);
        SprayUsersText.Text = (_incidents.Count == 0 ? 0 : _incidents.Max(i => i.DistinctUsernames)).ToString();
        ProcessEventsText.Text = _incidents.Count(i => i.ProcessId is > 0).ToString();

        CampaignsList.ItemsSource = _campaigns.Select(c => new CampaignRow
        {
            Campaign = c,
            Display = $"{c.Title}\n  {c.Severity} · hops={c.Hops.Count} · {string.Join(" -> ", c.InvolvedIps.Take(4))}"
        }).ToList();
        CampaignsList.DisplayMemberPath = "Display";

        // Response actions: only show when Central returned incidents (detect-only log rows).
        // Never invent synthetic PendingApproval / Capture rows.
        Actions.Clear();
        foreach (var top in _incidents.Where(i => i.FailedAttempts > 0 || !string.IsNullOrWhiteSpace(i.RuleId)).Take(8))
        {
            Actions.Add(new ResponseActionRow(
                RelTime(top.LastSeen ?? top.FirstSeen),
                "DetectOnly / LogOnly",
                top.DestinationIp ?? top.DestinationHost ?? "-",
                string.IsNullOrWhiteSpace(top.RuleId) ? (top.Title ?? "-") : top.RuleId,
                "Logged"));
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
            var lastSeen = GetTime(el, "lastSeenUtc", "LastSeenUtc") ?? DateTimeOffset.MinValue;
            var online = GetBool(el, "online", "Online")
                         ?? (lastSeen > DateTimeOffset.UtcNow.AddMinutes(-2));
            var status = GetString(el, "status", "Status") ?? (online ? "Healthy" : "Offline");
            if (!online && !string.Equals(status, "Offline", StringComparison.OrdinalIgnoreCase))
                status = "Offline";
            rows.Add(AgentRow.FromJson(el, status, lastSeen, online));
        }

        return rows.OrderByDescending(a => a.Online).ThenByDescending(a => a.LastSeenUtc).ToList();
    }

    private void BindAgentsFleetKpis(List<AgentRow> agents)
    {
        var online = agents.Count(a => a.Online);
        AgKpiTotal.Text = agents.Count.ToString();
        AgKpiOnline.Text = online.ToString();
        AgKpiOffline.Text = $"{agents.Count - online} offline";
        AgKpiWin.Text = agents.Count(a =>
            string.Equals(a.Platform, "windows", StringComparison.OrdinalIgnoreCase)).ToString();
        AgKpiLinux.Text = agents.Count(a =>
            string.Equals(a.Platform, "linux", StringComparison.OrdinalIgnoreCase)).ToString();
        var withCpu = agents.Where(a => a.Online && a.CpuPercent is not null).ToList();
        var withMem = agents.Where(a => a.Online && a.MemUsedPercent is not null).ToList();
        var avgCpu = withCpu.Count > 0 ? withCpu.Average(a => a.CpuPercent!.Value) : (double?)null;
        var avgMem = withMem.Count > 0 ? withMem.Average(a => a.MemUsedPercent!.Value) : (double?)null;
        AgKpiAvg.Text = $"{(avgCpu is double c ? $"{c:F0}%" : "—")} / {(avgMem is double m ? $"{m:F0}%" : "—")}";
        AgentsFleetHint.Text = agents.Count == 0 ? "No agents yet" : "Select a host for full metrics →";
    }

    private async void AgentsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentsGrid.SelectedItem is AgentRow row)
            await ShowAgentDetailAsync(row);
    }

    private void ClearAgentDetail()
    {
        AgDetailHost.Text = "Select an agent";
        AgDetailSub.Text = "Live inventory from Central heartbeats";
        AgDetailOnlineText.Text = "—";
        AgTileCpu.Text = AgTileMem.Text = AgTileDisk.Text = AgTileLoad.Text = "—";
        AgTileNetRx.Text = AgTileNetTx.Text = AgTileIo.Text = AgTileWs.Text = "—";
        AgDetailIdentity.Text = "—";
        AgDetailIntegrity.Text = "—";
        AgDetailMetricsLine.Text = "—";
        AgDetailError.Text = "";
        AgCpuChart.Children.Clear();
    }

    private async Task ShowAgentDetailAsync(AgentRow row)
    {
        // Immediate paint from list row
        PaintAgentDetail(row, history: null);

        try
        {
            var detail = await _api.GetAgentDetailAsync(row.AgentId, metricsTake: 90);
            if (detail is null) return;
            var enriched = AgentRow.FromInventory(detail);
            // Keep selection stable
            if (AgentsGrid.SelectedItem is AgentRow cur && cur.AgentId != enriched.AgentId)
                return;
            PaintAgentDetail(enriched, detail.MetricsHistory);
        }
        catch
        {
            // keep list-row paint
        }
    }

    private void PaintAgentDetail(AgentRow a, List<AgentMetricsSample>? history)
    {
        AgDetailHost.Text = a.ComputerName;
        AgDetailSub.Text = $"{a.Platform?.ToUpperInvariant() ?? "?"} · {a.HostIp ?? "no-ip"} · {a.AgentId}";
        AgDetailOnlineText.Text = a.Online ? "ONLINE" : "OFFLINE";
        AgDetailOnlineBadge.Background = new SolidColorBrush(
            a.Online ? Color.FromRgb(0x06, 0x4E, 0x3B) : Color.FromRgb(0x7F, 0x1D, 0x1D));
        AgDetailOnlineText.Foreground = new SolidColorBrush(
            a.Online ? Color.FromRgb(0x6E, 0xE7, 0xB7) : Color.FromRgb(0xFC, 0xA5, 0xA5));

        AgTileCpu.Text = a.CpuPercent is double c ? $"{c:F1}%" : "—";
        AgTileMem.Text = a.MemUsedPercent is double m ? $"{m:F1}%" : "—";
        AgTileDisk.Text = a.DiskUsedPercent is double d ? $"{d:F1}%" : "—";
        AgTileLoad.Text = a.LoadAverage1 is double l
            ? $"{l:F2} / q={a.QueueDepth}"
            : $"q={a.QueueDepth}";
        AgTileNetRx.Text = FormatRate(a.NetworkRxBytesPerSec);
        AgTileNetTx.Text = FormatRate(a.NetworkTxBytesPerSec);
        AgTileIo.Text = $"{FormatRate(a.DiskReadBytesPerSec)} · {FormatRate(a.DiskWriteBytesPerSec)}";
        AgTileWs.Text = a.WorkingSetBytes > 0 ? FormatBytes(a.WorkingSetBytes) : "—";

        var offline = a.Online ? "live" : $"offline {a.OfflineSeconds}s";
        AgDetailIdentity.Text =
            $"AgentId     {a.AgentId}\n" +
            $"Host        {a.ComputerName}\n" +
            $"IP          {a.HostIp ?? "—"}\n" +
            $"Platform    {a.Platform ?? "—"}\n" +
            $"OS          {a.OsVersion ?? "—"}\n" +
            $"Version     {a.Version ?? "—"}\n" +
            $"LastSeen    {a.LastSeenUtc:yyyy-MM-dd HH:mm:ss} UTC ({offline})\n" +
            $"DB size     {FormatBytes(a.DatabaseSizeBytes)}\n" +
            $"Clock skew  {a.ClockSkewSeconds:F2}s\n" +
            $"Host RAM    {(a.HostMemUsedBytes is long u && a.HostMemTotalBytes is long t ? $"{FormatBytes(u)} / {FormatBytes(t)}" : "—")}";

        var sha = string.IsNullOrWhiteSpace(a.BinarySha256) ? "—" :
            (a.BinarySha256.Length > 24 ? a.BinarySha256[..24] + "…" : a.BinarySha256);
        AgDetailIntegrity.Text =
            $"SHA-256     {sha}\n" +
            $"Signed      {(a.IsBinarySigned is true ? "yes" : a.IsBinarySigned is false ? "no" : "—")}\n" +
            $"Policy ver  {a.PolicyVersion?.ToString() ?? "—"}\n" +
            $"Central URL {a.CentralUrl ?? "—"}";

        AgDetailMetricsLine.Text = string.IsNullOrWhiteSpace(a.MetricsSummary)
            ? (a.Status ?? "—")
            : a.MetricsSummary;
        AgDetailError.Text = string.IsNullOrWhiteSpace(a.LastError) ? "" : "Last error: " + a.LastError;

        DrawCpuHistory(history ?? a.History);
    }

    private void DrawCpuHistory(List<AgentMetricsSample>? samples)
    {
        AgCpuChart.Children.Clear();
        if (samples is null || samples.Count < 2)
        {
            AgCpuChart.Children.Add(new TextBlock
            {
                Text = "Waiting for metrics history (heartbeats)…",
                Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                FontSize = 11,
                Margin = new Thickness(8)
            });
            return;
        }

        // samples newest-first → reverse for time axis
        var pts = samples.Where(s => s.CpuPercent is not null).Reverse().Take(90).ToList();
        if (pts.Count < 2)
        {
            // fall back to mem if no cpu
            pts = samples.Where(s => s.MemUsedPercent is not null).Reverse().Take(90)
                .Select(s => new AgentMetricsSample { CpuPercent = s.MemUsedPercent, TimestampUtc = s.TimestampUtc }).ToList();
        }

        if (pts.Count < 2) return;

        AgCpuChart.UpdateLayout();
        var w = Math.Max(40, AgCpuChart.ActualWidth > 10 ? AgCpuChart.ActualWidth : 320);
        var h = Math.Max(40, AgCpuChart.ActualHeight > 10 ? AgCpuChart.ActualHeight : 74);
        var poly = new System.Windows.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x38, 0xBD, 0xF8))
        };
        var n = pts.Count;
        for (var i = 0; i < n; i++)
        {
            var v = Math.Clamp(pts[i].CpuPercent ?? 0, 0, 100);
            var x = n == 1 ? 0 : i * (w - 4) / (n - 1);
            var y = h - 4 - (v / 100.0) * (h - 8);
            poly.Points.Add(new Point(x, y));
        }

        // close fill to baseline
        poly.Points.Add(new Point(w - 4, h - 2));
        poly.Points.Add(new Point(0, h - 2));
        AgCpuChart.Children.Add(poly);
    }

    private static string FormatRate(double? bps)
    {
        if (bps is null) return "—";
        var v = bps.Value;
        if (v >= 1_048_576) return $"{v / 1_048_576:F2} MB/s";
        if (v >= 1024) return $"{v / 1024:F1} KB/s";
        return $"{v:F0} B/s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }

    private static double? GetDouble(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d))
                return d;
        }

        return null;
    }

    private static long GetLong(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var l))
                return l;
        }

        return 0;
    }

    private static int? GetInt(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i))
                return i;
        }

        return null;
    }

    private static bool? GetBool(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (el.TryGetProperty(n, out var p))
            {
                if (p.ValueKind == JsonValueKind.True) return true;
                if (p.ValueKind == JsonValueKind.False) return false;
            }
        }

        return null;
    }

    private void SetStatus(bool online)
    {
        if (online)
        {
            ProtectedTitle.Text = "Protected";
            ProtectedSub.Text = "Live API only — no mock/demo data";
        }
        else
        {
            ProtectedTitle.Text = "Offline";
            ProtectedSub.Text = "Central unreachable — UI empty (no mock)";
        }
    }

    /// <summary>Build chart purely from live incident FailedAttempts; hide when empty.</summary>
    private void UpdateFailedLoginChart(List<Incident> incidents)
    {
        var series = incidents
            .Where(i => i.FailedAttempts > 0)
            .OrderBy(i => i.LastSeen ?? i.FirstSeen ?? DateTimeOffset.MinValue)
            .Select(i => (double)Math.Max(1, i.FailedAttempts))
            .TakeLast(14)
            .ToList();

        if (series.Count < 2)
        {
            FailedLoginCanvas.Visibility = Visibility.Collapsed;
            FailedLoginEmptyHint.Visibility = Visibility.Visible;
            FailedLoginEmptyHint.Text = series.Count == 0
                ? "No failed-login spike data yet (live only)"
                : "Need at least 2 failed-login incidents for a trend line";
            FailedLoginLine.Points = new PointCollection();
            FailedLoginFill.Points = new PointCollection();
            FailedLoginPeakBadge.Visibility = Visibility.Collapsed;
            return;
        }

        FailedLoginEmptyHint.Visibility = Visibility.Collapsed;
        FailedLoginCanvas.Visibility = Visibility.Visible;
        FailedLoginPeakBadge.Visibility = Visibility.Visible;

        const double left = 30, top = 12, bottom = 150, right = 380;
        var max = series.Max();
        if (max < 1) max = 1;
        var step = (right - left) / Math.Max(1, series.Count - 1);
        var pts = new PointCollection();
        for (var i = 0; i < series.Count; i++)
        {
            var x = left + i * step;
            var y = bottom - (series[i] / max) * (bottom - top);
            pts.Add(new Point(x, y));
        }

        FailedLoginLine.Points = pts;
        var fill = new PointCollection(pts) { new Point(right, bottom), new Point(left, bottom) };
        FailedLoginFill.Points = fill;
        FailedLoginPeak.Text = ((int)max).ToString();
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
        _deadlineTimer.Stop();
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

    private sealed class AgentRow
    {
        public string AgentId { get; init; } = "?";
        public string ComputerName { get; init; } = "?";
        public string? HostIp { get; init; }
        public string? Version { get; init; }
        public string? Status { get; init; }
        public DateTimeOffset LastSeenUtc { get; init; }
        public bool Online { get; init; } = true;
        public string? Platform { get; init; }
        public string? CentralUrl { get; init; }
        public string? LastError { get; init; }
        public string? OsVersion { get; init; }
        public double? CpuPercent { get; init; }
        public double? MemUsedPercent { get; init; }
        public double? DiskUsedPercent { get; init; }
        public double? NetworkRxBytesPerSec { get; init; }
        public double? NetworkTxBytesPerSec { get; init; }
        public double? DiskReadBytesPerSec { get; init; }
        public double? DiskWriteBytesPerSec { get; init; }
        public double? LoadAverage1 { get; init; }
        public long QueueDepth { get; init; }
        public long WorkingSetBytes { get; init; }
        public long DatabaseSizeBytes { get; init; }
        public double ClockSkewSeconds { get; init; }
        public string? BinarySha256 { get; init; }
        public bool? IsBinarySigned { get; init; }
        public int? PolicyVersion { get; init; }
        public long? HostMemUsedBytes { get; init; }
        public long? HostMemTotalBytes { get; init; }
        public string? MetricsSummary { get; init; }
        public int OfflineSeconds { get; init; }
        public List<AgentMetricsSample> History { get; init; } = [];

        public string OnlineGlyph => Online ? "●" : "○";
        public string CpuText => CpuPercent is double c ? $"{c:F0}%" : "—";
        public string MemText => MemUsedPercent is double m ? $"{m:F0}%" : "—";
        public string DiskText => DiskUsedPercent is double d ? $"{d:F0}%" : "—";
        public string StatusShort
        {
            get
            {
                var s = Status ?? "";
                if (s.Length > 40) return s[..40] + "…";
                return s;
            }
        }

        public static AgentRow FromJson(JsonElement el, string status, DateTimeOffset lastSeen, bool online) => new()
        {
            AgentId = GetString(el, "agentId", "AgentId") ?? "?",
            ComputerName = GetString(el, "computerName", "ComputerName") ?? "?",
            HostIp = GetString(el, "hostIp", "HostIp"),
            Version = GetString(el, "agentVersion", "AgentVersion", "Version"),
            Status = status,
            LastSeenUtc = lastSeen,
            Online = online,
            Platform = GetString(el, "platform", "Platform"),
            CentralUrl = GetString(el, "centralUrl", "CentralUrl"),
            LastError = GetString(el, "lastError", "LastError"),
            OsVersion = GetString(el, "osVersion", "OsVersion"),
            CpuPercent = GetDouble(el, "cpuPercent", "CpuPercent"),
            MemUsedPercent = GetDouble(el, "memUsedPercent", "MemUsedPercent"),
            DiskUsedPercent = GetDouble(el, "diskUsedPercent", "DiskUsedPercent"),
            NetworkRxBytesPerSec = GetDouble(el, "networkRxBytesPerSec", "NetworkRxBytesPerSec"),
            NetworkTxBytesPerSec = GetDouble(el, "networkTxBytesPerSec", "NetworkTxBytesPerSec"),
            DiskReadBytesPerSec = GetDouble(el, "diskReadBytesPerSec", "DiskReadBytesPerSec"),
            DiskWriteBytesPerSec = GetDouble(el, "diskWriteBytesPerSec", "DiskWriteBytesPerSec"),
            LoadAverage1 = GetDouble(el, "loadAverage1", "LoadAverage1"),
            QueueDepth = GetLong(el, "queueDepth", "QueueDepth"),
            WorkingSetBytes = GetLong(el, "workingSetBytes", "WorkingSetBytes"),
            DatabaseSizeBytes = GetLong(el, "databaseSizeBytes", "DatabaseSizeBytes"),
            ClockSkewSeconds = GetDouble(el, "clockSkewSeconds", "ClockSkewSeconds") ?? 0,
            BinarySha256 = GetString(el, "binarySha256", "BinarySha256"),
            IsBinarySigned = GetBool(el, "isBinarySigned", "IsBinarySigned"),
            PolicyVersion = GetInt(el, "policyVersion", "PolicyVersion"),
            HostMemUsedBytes = GetLong(el, "hostMemUsedBytes", "HostMemUsedBytes") is long hu and > 0 ? hu : null,
            HostMemTotalBytes = GetLong(el, "hostMemTotalBytes", "HostMemTotalBytes") is long ht and > 0 ? ht : null,
            MetricsSummary = GetString(el, "metricsSummary", "MetricsSummary"),
            OfflineSeconds = GetInt(el, "offlineSeconds", "OfflineSeconds") ?? 0
        };

        public static AgentRow FromInventory(AgentInventoryItem i) => new()
        {
            AgentId = i.AgentId,
            ComputerName = i.ComputerName,
            HostIp = i.HostIp,
            Version = i.AgentVersion,
            Status = i.Status,
            LastSeenUtc = i.LastSeenUtc,
            Online = i.Online,
            Platform = i.Platform,
            CentralUrl = i.CentralUrl,
            LastError = i.LastError,
            OsVersion = i.OsVersion,
            CpuPercent = i.CpuPercent,
            MemUsedPercent = i.MemUsedPercent,
            DiskUsedPercent = i.DiskUsedPercent,
            NetworkRxBytesPerSec = i.NetworkRxBytesPerSec,
            NetworkTxBytesPerSec = i.NetworkTxBytesPerSec,
            DiskReadBytesPerSec = i.DiskReadBytesPerSec,
            DiskWriteBytesPerSec = i.DiskWriteBytesPerSec,
            LoadAverage1 = i.LoadAverage1,
            QueueDepth = i.QueueDepth,
            WorkingSetBytes = i.WorkingSetBytes,
            DatabaseSizeBytes = i.DatabaseSizeBytes,
            ClockSkewSeconds = i.ClockSkewSeconds,
            BinarySha256 = i.BinarySha256,
            IsBinarySigned = i.IsBinarySigned,
            PolicyVersion = i.PolicyVersion,
            HostMemUsedBytes = i.HostMemUsedBytes,
            HostMemTotalBytes = i.HostMemTotalBytes,
            MetricsSummary = i.MetricsSummary,
            OfflineSeconds = i.OfflineSeconds,
            History = i.MetricsHistory ?? []
        };
    }

    private sealed class DeadlineTaskRow
    {
        private readonly IReadOnlyList<string> _subtasks;

        public DeadlineTaskRow(string taskName, DateTimeOffset deadline, IReadOnlyList<string> subtasks)
        {
            TaskName = taskName;
            Deadline = deadline;
            _subtasks = subtasks;
            DeadlineText = deadline.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
            SubtasksText = string.Join(Environment.NewLine,
                _subtasks.Select((item, index) => $"{index + 1}. {item}"));
            UpdateCountdown(DateTimeOffset.Now);
        }

        public string TaskName { get; }
        public DateTimeOffset Deadline { get; }
        public string DeadlineText { get; }
        public string SubtasksText { get; }
        public string CountdownText { get; private set; } = "";
        public string StatusText { get; private set; } = "";

        public void UpdateCountdown(DateTimeOffset now)
        {
            var remaining = Deadline - now;
            if (remaining <= TimeSpan.Zero)
            {
                var overdue = TimeSpan.FromSeconds(Math.Floor(Math.Abs(remaining.TotalSeconds)));
                CountdownText = $"เลยกำหนด {overdue.Days} วัน {overdue.Hours:00}:{overdue.Minutes:00}:{overdue.Seconds:00}";
                StatusText = "หมดเวลา";
                return;
            }

            var countdown = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
            CountdownText = $"{countdown.Days} วัน {countdown.Hours:00}:{countdown.Minutes:00}:{countdown.Seconds:00}";
            StatusText = "กำลังนับถอยหลัง";
        }
    }

    private sealed class AgentPick
    {
        public required string AgentId { get; init; }
        public required string ComputerName { get; init; }
        public string Display => $"{ComputerName}  ({AgentId})";
    }

    private void RefreshFirewallAgentList(List<AgentRow> agents)
    {
        var prev = (FwAgentBox.SelectedItem as AgentPick)?.AgentId;
        var picks = agents.Select(a => new AgentPick { AgentId = a.AgentId, ComputerName = a.ComputerName }).ToList();
        FwAgentBox.ItemsSource = picks;
        if (picks.Count == 0)
        {
            FwAgentBox.SelectedIndex = -1;
            return;
        }

        var match = picks.FindIndex(p => p.AgentId == prev);
        FwAgentBox.SelectedIndex = match >= 0 ? match : 0;
    }

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
