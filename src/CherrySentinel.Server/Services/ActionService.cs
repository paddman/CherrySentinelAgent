using System.Collections.Concurrent;
using CherrySentinel.Shared.Models;

namespace CherrySentinel.Server.Services;

/// <summary>
/// In-memory + DB-backed pending actions queue for agents (approval workflow).
/// </summary>
public sealed class ActionService
{
    private readonly ConcurrentDictionary<string, ResponseActionRequest> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<string>> _byAgent = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ActionService> _logger;

    public ActionService(ILogger<ActionService> logger)
    {
        _logger = logger;
    }

    public Task<ResponseActionRequest> EnqueueAsync(ResponseActionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            request.RequestId = Guid.NewGuid().ToString("N");
        }

        // Destructive actions must be marked approved by operator/API caller.
        if (!request.Approved && NeedsApproval(request.ActionType))
        {
            _logger.LogWarning("Action {Id} type {Type} enqueued without approval flag", request.RequestId, request.ActionType);
        }

        _byId[request.RequestId] = request;
        // Target agent encoded in Requester field as agent:<id> or IncidentId agent mapping — use Requester when form agent:ID
        var agentId = request.Requester.StartsWith("agent:", StringComparison.OrdinalIgnoreCase)
            ? request.Requester["agent:".Length..]
            : "broadcast";
        _byAgent.AddOrUpdate(agentId,
            _ => [request.RequestId],
            (_, list) =>
            {
                lock (list) { list.Add(request.RequestId); }
                return list;
            });

        return Task.FromResult(request);
    }

    public Task<ResponseActionRequest?> GetAsync(string id)
    {
        _byId.TryGetValue(id, out var req);
        return Task.FromResult(req);
    }

    public Task<List<ResponseActionRequest>> GetPendingForAgentAsync(string agentId)
    {
        var result = new List<ResponseActionRequest>();
        if (_byAgent.TryGetValue(agentId, out var ids))
        {
            lock (ids)
            {
                foreach (var id in ids.ToList())
                {
                    if (_byId.TryGetValue(id, out var req))
                    {
                        result.Add(req);
                        ids.Remove(id);
                    }
                }
            }
        }

        if (_byAgent.TryGetValue("broadcast", out var b))
        {
            lock (b)
            {
                foreach (var id in b.ToList())
                {
                    if (_byId.TryGetValue(id, out var req))
                    {
                        result.Add(req);
                    }
                }
            }
        }

        return Task.FromResult(result);
    }

    private static bool NeedsApproval(string actionType) =>
        actionType is not ("LogOnly" or "ExportEvidence");
}
