using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CherrySentinel.Response.Firewall;

/// <summary>
/// Windows Firewall blocking compatible with Server 2012 via netsh advfirewall fallback.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FirewallBlocker
{
    private readonly ILogger<FirewallBlocker> _logger;

    public FirewallBlocker(ILogger<FirewallBlocker> logger)
    {
        _logger = logger;
    }

    public FirewallChangeResult BlockIp(string ip, string direction, string ruleName, string reason)
    {
        ValidateIp(ip);
        ValidateDirection(direction);
        ValidateRuleName(ruleName);

        var dir = direction.Equals("out", StringComparison.OrdinalIgnoreCase) ? "out" : "in";
        var remote = dir == "in" ? $"remoteip={ip}" : $"remoteip={ip}";
        var args =
            $"advfirewall firewall add rule name=\"{ruleName}\" dir={dir} action=block {remote} enable=yes protocol=any";

        var before = GetRule(ruleName);
        var (code, output) = RunNetsh(args);
        var after = GetRule(ruleName);

        return new FirewallChangeResult
        {
            Success = code == 0,
            BeforeState = before ?? "absent",
            Result = code == 0 ? $"blocked {ip} dir={dir}" : output,
            RollbackCommand = $"netsh advfirewall firewall delete rule name=\"{ruleName}\"",
            Error = code == 0 ? null : output
        };
    }

    public FirewallChangeResult RemoveRule(string ruleName)
    {
        ValidateRuleName(ruleName);
        var before = GetRule(ruleName) ?? "absent";
        var (code, output) = RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
        return new FirewallChangeResult
        {
            Success = code == 0 || output.Contains("No rules match", StringComparison.OrdinalIgnoreCase),
            BeforeState = before,
            Result = code == 0 ? "removed" : output,
            RollbackCommand = before != "absent"
                ? $"# re-add previous rule manually: {before}"
                : string.Empty,
            Error = code == 0 ? null : output
        };
    }

    public string CaptureFirewallStatus()
    {
        var (code, output) = RunNetsh("advfirewall show allprofiles");
        return code == 0 ? output : $"netsh failed: {output}";
    }

    private string? GetRule(string ruleName)
    {
        var (code, output) = RunNetsh($"advfirewall firewall show rule name=\"{ruleName}\"");
        if (code != 0 || output.Contains("No rules match", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return output.Length > 2000 ? output[..2000] : output;
    }

    private (int ExitCode, string Output) RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start netsh");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            var combined = (stdout + Environment.NewLine + stderr).Trim();
            _logger.LogDebug("netsh {Args} => {Code}", args, p.ExitCode);
            return (p.ExitCode, combined);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "netsh failed");
            return (-1, ex.Message);
        }
    }

    private static void ValidateIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip.Contains(' ') || ip.Contains('"') || ip.Contains(';') || ip.Contains('&'))
        {
            throw new ArgumentException("Invalid IP for firewall rule.", nameof(ip));
        }

        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            throw new ArgumentException("IP parse failed.", nameof(ip));
        }
    }

    private static void ValidateDirection(string direction)
    {
        if (!direction.Equals("in", StringComparison.OrdinalIgnoreCase) &&
            !direction.Equals("out", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Direction must be in or out.");
        }
    }

    private static void ValidateRuleName(string ruleName)
    {
        if (string.IsNullOrWhiteSpace(ruleName) || ruleName.Length > 128 ||
            ruleName.IndexOfAny(['"', ';', '&', '|', '\n', '\r']) >= 0)
        {
            throw new ArgumentException("Invalid rule name.");
        }

        if (!ruleName.StartsWith("CSA-", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Rule name must start with CSA- prefix.");
        }
    }
}

public sealed class FirewallChangeResult
{
    public bool Success { get; init; }
    public string BeforeState { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public string RollbackCommand { get; init; } = string.Empty;
    public string? Error { get; init; }
}
