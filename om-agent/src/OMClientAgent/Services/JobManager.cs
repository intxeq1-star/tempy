using Microsoft.Extensions.Logging;
using OMClientAgent.Core;
using OMClientAgent.Core.Models;
using OMClientAgent.Infrastructure;

namespace OMClientAgent.Services;

public sealed class JobManager
{
    private readonly LocalDatabase _db;
    private readonly CommandExecutor _executor;
    private readonly SoftwareManager _software;
    private readonly PolicyManager _policies;
    private readonly DnsManager _dns;
    private readonly IResultReporter _reporter;
    private readonly AgentConfigService _config;
    private readonly OmEvents _events;
    private readonly ILogger<JobManager> _logger;

    public JobManager(LocalDatabase db, CommandExecutor executor, SoftwareManager software, PolicyManager policies,
        DnsManager dns, IResultReporter reporter, AgentConfigService config, OmEvents events, ILogger<JobManager> logger)
    {
        _db = db;
        _executor = executor;
        _software = software;
        _policies = policies;
        _dns = dns;
        _reporter = reporter;
        _config = config;
        _events = events;
        _logger = logger;
        _events.JobReceived += HandleJobReceivedAsync;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var poll = TimeSpan.FromSeconds(_config.Current.JobPollIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            await ProcessQueueAsync(ct).ConfigureAwait(false);
            await Task.Delay(poll, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleJobReceivedAsync(Job job)
    {
        var ct = CancellationToken.None;
        if (_db.HasCompletedJob(job.JobId))
        {
            _logger.LogInformation("Job {JobId} already completed; re-sending stored result.", job.JobId);
            var existing = _db.GetCompletedJobResult(job.JobId);
            if (existing is not null)
                await _reporter.SendJobResultAsync(ToResult(existing), ct).ConfigureAwait(false);
            return;
        }

        _db.UpsertJob(job);
        _logger.LogInformation("Received job {JobId} ({Type}, {Mode}).", job.JobId, job.Type, job.ExecutionMode);
        await DispatchAsync(job, ct).ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        var pending = _db.GetPendingJobs();
        foreach (var job in pending)
        {
            if (ct.IsCancellationRequested) return;
            await DispatchAsync(job, ct).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(Job job, CancellationToken ct)
    {
        if (_db.HasCompletedJob(job.JobId))
        {
            var existing = _db.GetCompletedJobResult(job.JobId);
            if (existing is not null) await _reporter.SendJobResultAsync(ToResult(existing), ct).ConfigureAwait(false);
            return;
        }

        _db.UpdateJobStatus(job.JobId, JobStatus.Acknowledged);
        await _reporter.SendJobAcknowledgedAsync(new JobAckMessage { JobId = job.JobId, MachineId = _config.Current.MachineId, Status = "ACKNOWLEDGED" }, ct).ConfigureAwait(false);

        var started = DateTime.UtcNow;
        _db.UpdateJobStatus(job.JobId, JobStatus.Running, startedAtUtc: started);
        _db.SetSetting("RunningJob", job.JobId);
        await _reporter.SendJobStartedAsync(new JobStartedMessage { JobId = job.JobId, MachineId = _config.Current.MachineId, Timestamp = ToUnix(started) }, ct).ConfigureAwait(false);

        CommandResult result;
        try { result = await ExecuteAsync(job, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} threw while executing.", job.JobId);
            result = new CommandResult { Status = JobStatus.Failed, ExitCode = 1, StandardError = ex.Message };
        }

        if (result.NeedsInteractiveUser)
        {
            _db.UpdateJobStatus(job.JobId, JobStatus.WaitingForUser);
            _logger.LogInformation("Job {JobId} is holding as WAITING_FOR_USER.", job.JobId);
            return;
        }

        var completed = DateTime.UtcNow;
        _db.UpdateJobStatus(job.JobId, result.Status, result.ExitCode, result.StandardOutput, result.StandardError, started, completed);
        _db.SetSetting("RunningJob", string.Empty);

        await _reporter.SendJobResultAsync(ToResult(job, result, started, completed), ct).ConfigureAwait(false);
        _logger.LogInformation("Job {JobId} finished with status {Status}.", job.JobId, result.Status);
    }

    private async Task<CommandResult> ExecuteAsync(Job job, CancellationToken ct)
    {
        if (job.Type == JobType.Software || job.Type == JobType.SoftwareInstall || job.Type == JobType.SoftwareUninstall || job.Type == JobType.SoftwareUpdate)
            return await ExecuteSoftwareAsync(job, ct).ConfigureAwait(false);

        return job.Type switch
        {
            JobType.Dns => await _dns.ApplyDnsAsync(string.IsNullOrWhiteSpace(job.Action) ? "192.168.1.100" : job.Action!, ct).ConfigureAwait(false),
            JobType.Policy => await ExecutePolicyAsync(job, ct).ConfigureAwait(false),
            _ => await _executor.ExecuteAsync(job, ct).ConfigureAwait(false)
        };
    }

    private async Task<CommandResult> ExecuteSoftwareAsync(Job job, CancellationToken ct)
    {
        var command = job.Command ?? string.Empty;
        if (command.StartsWith("INSTALL ", StringComparison.OrdinalIgnoreCase))
        {
            var name = command["INSTALL ".Length..].Trim();
            return await _software.InstallPackageAsync(name, job.TimeoutSeconds, ct).ConfigureAwait(false);
        }
        if (command.StartsWith("REMOVE ", StringComparison.OrdinalIgnoreCase))
        {
            var name = command["REMOVE ".Length..].Trim();
            return await _software.UninstallPackageAsync(name, job.TimeoutSeconds, ct).ConfigureAwait(false);
        }
        return await _software.InstallPackageAsync(command.Trim(), job.TimeoutSeconds, ct).ConfigureAwait(false);
    }

    private async Task<CommandResult> ExecutePolicyAsync(Job job, CancellationToken ct)
    {
        var policy = new AppPolicy
        {
            PolicyId = job.PolicyId ?? job.JobId,
            Version = job.Priority == JobPriority.High ? 2 : 1,
            Application = job.Command ?? string.Empty,
            Action = job.Action?.Equals("UNBLOCK", StringComparison.OrdinalIgnoreCase) == true ? ApplicationAction.Unblock : ApplicationAction.Block,
            Target = job.Arguments ?? string.Empty,
            CreatedAtUtc = job.CreatedAtUtc,
            CreatedBy = job.CreatedBy ?? string.Empty
        };
        var state = await _policies.HandlePolicyAsync(policy, ct).ConfigureAwait(false);
        var ok = state == ApplyState.Applied;
        return new CommandResult
        {
            Status = ok ? JobStatus.Success : JobStatus.Failed,
            ExitCode = ok ? 0 : 1,
            StandardOutput = $"Policy {policy.PolicyId} v{policy.Version} {policy.Action} applied.",
            StandardError = ok ? string.Empty : "Policy could not be verified as applied."
        };
    }

    private static JobResultMessage ToResult(Job job) => new()
    {
        JobId = job.JobId,
        MachineId = job.TargetMachineId,
        Status = job.Status,
        ExitCode = job.ExitCode,
        Output = Combine(job.StandardOutput, job.StandardError),
        StartedAt = ToUnix(job.StartedAtUtc ?? job.CreatedAtUtc),
        CompletedAt = ToUnix(job.CompletedAtUtc ?? DateTime.UtcNow)
    };

    private static JobResultMessage ToResult(Job job, CommandResult result, DateTime started, DateTime completed) => new()
    {
        JobId = job.JobId,
        MachineId = job.TargetMachineId,
        Status = result.Status,
        ExitCode = result.ExitCode,
        Output = Truncate(Combine(result.StandardOutput, result.StandardError)),
        StartedAt = ToUnix(started),
        CompletedAt = ToUnix(completed)
    };

    private static string Combine(string? stdout, string? stderr)
        => string.IsNullOrWhiteSpace(stdout) && string.IsNullOrWhiteSpace(stderr) ? string.Empty
           : (stdout ?? "") + (string.IsNullOrWhiteSpace(stderr) ? "" : Environment.NewLine + stderr);

    private static long ToUnix(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalSeconds;

    private static string Truncate(string value, int max = 64 * 1024) =>
        value is null ? string.Empty : value.Length <= max ? value : value[..max];
}
