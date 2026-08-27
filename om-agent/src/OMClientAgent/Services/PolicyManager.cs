using Microsoft.Extensions.Logging;
using OMClientAgent.Core.Models;
using OMClientAgent.Infrastructure;

namespace OMClientAgent.Services;

public sealed class PolicyManager
{
    private readonly ApplicationControlManager _appControl;
    private readonly LocalDatabase _db;
    private readonly ILogger<PolicyManager> _logger;

    public PolicyManager(ApplicationControlManager appControl, LocalDatabase db, ILogger<PolicyManager> logger)
    {
        _appControl = appControl;
        _db = db;
        _logger = logger;
    }

    public async Task<ApplyState> HandlePolicyAsync(AppPolicy policy, CancellationToken ct)
    {
        _logger.LogInformation("Received policy {PolicyId} v{Version} ({Action} {Application}).",
            policy.PolicyId, policy.Version, policy.Action, policy.Application);
        return await _appControl.ApplyAsync(policy, ct).ConfigureAwait(false);
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        var desired = GetLatestPolicies();
        if (desired.Count == 0) return;

        _logger.LogInformation("Reconciling {Count} application-control policies.", desired.Count);
        foreach (var policy in desired)
        {
            var applied = _db.GetSetting($"PolicyApplied:{policy.PolicyId}");
            var matchesAlready = applied is not null && applied == ApplyState.Applied.ToString();
            if (matchesAlready) continue;

            var state = await _appControl.ApplyAsync(policy, ct).ConfigureAwait(false);
            _db.SetSetting($"PolicyApplied:{policy.PolicyId}", state.ToString());
        }
    }

    public List<AppPolicy> GetLatestPolicies()
    {
        var all = _db.GetAppliedPolicies();
        return all
            .GroupBy(p => p.PolicyId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Version).First())
            .ToList();
    }
}
