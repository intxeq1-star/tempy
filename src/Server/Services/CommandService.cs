using System.Text.Json;
using LanManagement.Server.Data;
using LanManagement.Server.Domain;
using LanManagement.Server.Protocol;
using Microsoft.EntityFrameworkCore;

namespace LanManagement.Server.Services;

public interface ICommandService
{
    Task<CreateCommandResult> CreateAsync(CreateCommandRequest request, string actor, string? remoteIp, CancellationToken cancellationToken);
    Task<CommandOperationProgress?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CommandOperationProgress>> GetRecentOperationsAsync(int take, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid commandId, string actor, string? remoteIp, CancellationToken cancellationToken);
    Task<bool> ReturnUnacknowledgedToPendingAsync(string deviceId, string reason, CancellationToken cancellationToken);
    Task<CommandReceiptOutcome> RecordReceiptAsync(CommandReceivedMessage message, CancellationToken cancellationToken);
    Task<CommandResultOutcome> RecordResultAsync(CommandResultMessage message, CancellationToken cancellationToken);
    Task<int> RecoverAfterServerStartAsync(CancellationToken cancellationToken);
    Task<int> MarkExpiredExecutionsAsync(CancellationToken cancellationToken);
}

public sealed record CommandReceiptOutcome(bool Accepted, string? ErrorCode = null);
public sealed record CommandResultOutcome(bool Accepted, string? ErrorCode = null);

/// <summary>
/// The command service is the durable source of truth. It creates one database job per device before
/// the dispatcher sees a connection, so an offline device never loses administrator intent.
/// </summary>
public sealed class CommandService(
    IDbContextFactory<ManagementDbContext> contextFactory,
    IClock clock,
    IDashboardEventBus dashboardEvents,
    ICommandWorkSignal commandSignal,
    IOptions<LanManagement.Server.Options.ServerOptions> serverOptions,
    ILogger<CommandService> logger) : ICommandService
{
    private static readonly CommandStatus[] ActiveStatuses =
    {
        CommandStatus.Pending, CommandStatus.Sent, CommandStatus.Received, CommandStatus.Running
    };

    public async Task<CreateCommandResult> CreateAsync(CreateCommandRequest request, string actor, string? remoteIp, CancellationToken cancellationToken)
    {
        var commandType = request.CommandType?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CommandTypes.IsSupported(commandType))
        {
            throw new ArgumentException("Unsupported command type.", nameof(request));
        }
        if (request.Payload.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("Command payload must be a JSON object.", nameof(request));
        }
        if (System.Text.Encoding.UTF8.GetByteCount(request.Payload.GetRawText()) > serverOptions.Value.MaximumMessageBytes)
        {
            throw new ArgumentException("Command payload exceeds the configured protocol message limit.", nameof(request));
        }
        ValidatePayload(commandType, request.Payload);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var targets = await ResolveTargetsAsync(db, request.Target, cancellationToken);
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("The selected target contains no devices.");
        }

        var now = clock.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var operation = new CommandOperation
        {
            OperationId = Guid.NewGuid(),
            CommandType = commandType,
            PayloadJson = request.Payload.GetRawText(),
            TargetKind = request.Target.Kind,
            TargetDescription = DescribeTarget(request.Target, targets),
            CreatedBy = actor,
            CreatedAt = now,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : Truncate(request.DisplayName, 256)
        };
        db.CommandOperations.Add(operation);

        var commandIds = new List<Guid>(targets.Count);
        foreach (var target in targets)
        {
            var command = new CommandJob
            {
                CommandId = Guid.NewGuid(),
                OperationId = operation.OperationId,
                DeviceId = target.DeviceId,
                CommandType = commandType,
                PayloadJson = operation.PayloadJson,
                CreatedBy = actor,
                CreatedAt = now,
                Status = CommandStatus.Pending,
                NextAttemptAt = now
            };
            commandIds.Add(command.CommandId);
            db.CommandJobs.Add(command);
        }

        db.AuditLogs.Add(AuditLogFactory.Create(now, actor, commandType, "CommandOperation", operation.OperationId.ToString(), new
        {
            operation_id = operation.OperationId,
            command_type = commandType,
            target_kind = request.Target.Kind.ToString().ToUpperInvariant(),
            target_count = targets.Count,
            target_devices = targets.Select(x => x.DeviceId).ToArray(),
            payload = request.Payload
        }, remoteIp));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        commandSignal.Pulse();
        await dashboardEvents.PublishAsync(new DashboardEvent("command-created", now, null, operation.OperationId), cancellationToken);
        logger.LogInformation("Operation {OperationId} created {Count} durable {CommandType} command job(s).", operation.OperationId, commandIds.Count, commandType);
        return new(operation.OperationId, commandIds, commandIds.Count);
    }

    public async Task<CommandOperationProgress?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.CommandOperations.AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken);
        if (operation is null)
        {
            return null;
        }
        return await BuildProgressAsync(db, operation, cancellationToken);
    }

    public async Task<IReadOnlyList<CommandOperationProgress>> GetRecentOperationsAsync(int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 100);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var operations = await db.CommandOperations.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(cancellationToken);
        var results = new List<CommandOperationProgress>(operations.Count);
        foreach (var operation in operations)
        {
            results.Add(await BuildProgressAsync(db, operation, cancellationToken));
        }
        return results;
    }

    public async Task<bool> CancelAsync(Guid commandId, string actor, string? remoteIp, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var command = await db.CommandJobs.SingleOrDefaultAsync(x => x.CommandId == commandId, cancellationToken);
        if (command is null || command.Status is not (CommandStatus.Pending or CommandStatus.Sent))
        {
            return false;
        }

        command.Status = CommandStatus.Cancelled;
        command.CompletedAt = clock.UtcNow;
        command.Error = "Cancelled by administrator before execution acknowledgement.";
        db.AuditLogs.Add(AuditLogFactory.Create(clock.UtcNow, actor, "COMMAND_CANCELLED", "CommandJob", command.CommandId.ToString(), new
        {
            command_id = command.CommandId,
            device_id = command.DeviceId,
            command_type = command.CommandType
        }, remoteIp));
        await db.SaveChangesAsync(cancellationToken);
        await dashboardEvents.PublishAsync(new DashboardEvent("command-cancelled", clock.UtcNow, command.DeviceId, command.OperationId), cancellationToken);
        return true;
    }

    public async Task<bool> ReturnUnacknowledgedToPendingAsync(string deviceId, string reason, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.UtcNow;
        var sent = await db.CommandJobs.Where(x => x.DeviceId == deviceId && x.Status == CommandStatus.Sent).ToListAsync(cancellationToken);
        if (sent.Count == 0)
        {
            return false;
        }

        foreach (var command in sent)
        {
            command.Status = CommandStatus.Pending;
            command.NextAttemptAt = now;
            command.LastDeliveryError = Truncate(reason, 2048);
        }
        await db.SaveChangesAsync(cancellationToken);
        commandSignal.Pulse();
        return true;
    }

    public async Task<CommandReceiptOutcome> RecordReceiptAsync(CommandReceivedMessage message, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await IsProcessedAsync(db, message.DeviceId, message.MessageId, cancellationToken))
        {
            return new(true);
        }

        var command = await db.CommandJobs.SingleOrDefaultAsync(x => x.CommandId == message.CommandId, cancellationToken);
        if (command is null || !string.Equals(command.DeviceId, message.DeviceId, StringComparison.Ordinal))
        {
            return new(false, "UNKNOWN_COMMAND");
        }

        var now = clock.UtcNow;
        if (!command.Status.IsTerminal())
        {
            var requestedRunning = string.Equals(message.Status, "RUNNING", StringComparison.Ordinal);
            command.Status = requestedRunning ? CommandStatus.Running : CommandStatus.Received;
            command.ReceivedAt ??= message.ReceivedAt;
            if (requestedRunning)
            {
                command.StartedAt ??= message.StartedAt ?? now;
            }
            command.LastDeliveryError = null;
        }

        db.ProcessedAgentMessages.Add(new ProcessedAgentMessage
        {
            DeviceId = message.DeviceId,
            MessageId = message.MessageId,
            MessageType = ProtocolConstants.CommandReceived,
            ProcessedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await dashboardEvents.PublishAsync(new DashboardEvent("command-received", now, message.DeviceId, command.OperationId), cancellationToken);
        return new(true);
    }

    public async Task<CommandResultOutcome> RecordResultAsync(CommandResultMessage message, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await IsProcessedAsync(db, message.DeviceId, message.MessageId, cancellationToken))
        {
            return new(true);
        }

        var command = await db.CommandJobs.SingleOrDefaultAsync(x => x.CommandId == message.CommandId, cancellationToken);
        if (command is null || !string.Equals(command.DeviceId, message.DeviceId, StringComparison.Ordinal))
        {
            return new(false, "UNKNOWN_COMMAND");
        }

        if (!CommandStatusExtensions.TryParseProtocolValue(message.Status, out var resultStatus) ||
            resultStatus is not (CommandStatus.Success or CommandStatus.Failed or CommandStatus.Cancelled))
        {
            return new(false, "INVALID_RESULT_STATUS");
        }

        var now = clock.UtcNow;
        if (command.Status.IsTerminal() && command.Status != CommandStatus.Timeout)
        {
            // A terminal result is immutable. Do not let a malicious or broken peer rewrite audit history.
            db.ProcessedAgentMessages.Add(new ProcessedAgentMessage
            {
                DeviceId = message.DeviceId,
                MessageId = message.MessageId,
                MessageType = ProtocolConstants.CommandResult,
                ProcessedAt = now
            });
            await db.SaveChangesAsync(cancellationToken);
            return command.Status == resultStatus ? new(true) : new(false, "CONFLICTING_RESULT");
        }

        var output = Truncate(message.Output, serverOptions.Value.MaximumCommandOutputBytes);
        var error = Truncate(message.Error, serverOptions.Value.MaximumCommandErrorBytes);
        var resultData = ToJsonOrNull(message.ResultData);
        command.Status = resultStatus;
        command.ReceivedAt ??= now;
        command.StartedAt ??= message.StartedAt;
        command.CompletedAt = message.CompletedAt;
        command.ExitCode = message.ExitCode;
        command.Output = output;
        command.Error = error;
        command.ResultDataJson = resultData;
        command.LastDeliveryError = null;
        db.CommandResults.Add(new CommandResult
        {
            CommandId = command.CommandId,
            MessageId = message.MessageId,
            Status = resultStatus,
            ReceivedByServerAt = now,
            StartedAt = message.StartedAt,
            CompletedAt = message.CompletedAt,
            ExitCode = message.ExitCode,
            Output = output,
            Error = error,
            ResultDataJson = resultData
        });
        db.ProcessedAgentMessages.Add(new ProcessedAgentMessage
        {
            DeviceId = message.DeviceId,
            MessageId = message.MessageId,
            MessageType = ProtocolConstants.CommandResult,
            ProcessedAt = now
        });

        var device = await db.Devices.SingleOrDefaultAsync(x => x.DeviceId == message.DeviceId, cancellationToken);
        if (device is not null)
        {
            device.LastCommandId = command.CommandId;
            device.LastCommandType = command.CommandType;
            device.LastCommandResult = resultStatus.ToProtocolValue();
            device.UpdatedAt = now;
        }
        db.AuditLogs.Add(AuditLogFactory.Create(now, command.CreatedBy, "COMMAND_RESULT_RECORDED", "CommandJob", command.CommandId.ToString(), new
        {
            command_id = command.CommandId,
            operation_id = command.OperationId,
            device_id = command.DeviceId,
            command_type = command.CommandType,
            status = resultStatus.ToProtocolValue(),
            exit_code = message.ExitCode,
            output,
            error
        }, succeeded: resultStatus == CommandStatus.Success));

        await UpdateReportedApplicationsAsync(db, command, resultStatus, resultData, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await dashboardEvents.PublishAsync(new DashboardEvent("command-result", now, message.DeviceId, command.OperationId), cancellationToken);
        return new(true);
    }

    public async Task<int> RecoverAfterServerStartAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.UtcNow;
        var sent = await db.CommandJobs.Where(x => x.Status == CommandStatus.Sent).ToListAsync(cancellationToken);
        foreach (var command in sent)
        {
            // A server cannot know whether a packet arrived before a restart. Returning to PENDING
            // causes duplicate-safe redelivery with the same immutable command_id.
            command.Status = CommandStatus.Pending;
            command.NextAttemptAt = now;
            command.LastDeliveryError = "SERVER_RESTART_RECONCILIATION";
        }
        var online = await db.Devices.Where(x => x.ConnectionState == DeviceConnectionState.Online).ToListAsync(cancellationToken);
        foreach (var device in online)
        {
            device.ConnectionState = DeviceConnectionState.Offline;
            device.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        commandSignal.Pulse();
        return sent.Count;
    }

    public async Task<int> MarkExpiredExecutionsAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddMinutes(-serverOptions.Value.CommandExecutionTimeoutMinutes);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var commands = await db.CommandJobs.Where(x =>
            (x.Status == CommandStatus.Received || x.Status == CommandStatus.Running) &&
            x.ReceivedAt != null && x.ReceivedAt < cutoff).ToListAsync(cancellationToken);
        foreach (var command in commands)
        {
            command.Status = CommandStatus.Timeout;
            command.CompletedAt = clock.UtcNow;
            command.Error = "The server did not receive a final result before the execution timeout. A later agent result may reconcile this state.";
        }
        await db.SaveChangesAsync(cancellationToken);
        return commands.Count;
    }

    private static void ValidatePayload(string commandType, JsonElement payload)
    {
        static bool HasNonEmptyString(JsonElement root, string name) => root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());

        if (commandType == CommandTypes.RunAdminCommand)
        {
            if (!HasNonEmptyString(payload, "command"))
            {
                throw new ArgumentException("RUN_ADMIN_COMMAND payload requires a non-empty command string.");
            }
            if (payload.TryGetProperty("timeout_seconds", out var timeout) &&
                (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetInt32(out var seconds) || seconds is < 1 or > 3600))
            {
                throw new ArgumentException("RUN_ADMIN_COMMAND timeout_seconds must be an integer between 1 and 3600.");
            }
        }
        if ((commandType is CommandTypes.InstallApp or CommandTypes.UninstallApp or CommandTypes.UpdateApp or CommandTypes.CheckApp) &&
            !HasNonEmptyString(payload, "package_id"))
        {
            throw new ArgumentException($"{commandType} payload requires a non-empty package_id string.");
        }
        if (commandType == CommandTypes.ApplyDns)
        {
            if (!HasNonEmptyString(payload, "server") ||
                !System.Net.IPAddress.TryParse(payload.GetProperty("server").GetString(), out _))
            {
                throw new ArgumentException("APPLY_DNS payload requires a valid server IP address.");
            }
        }
        if ((commandType is CommandTypes.ApplyBrowserPolicy or CommandTypes.ApplyAppPolicy) &&
            (!payload.TryGetProperty("desired_policy", out var desired) || desired.ValueKind != JsonValueKind.Object))
        {
            throw new ArgumentException($"{commandType} payload requires a desired_policy object.");
        }
    }

    private static async Task<List<Device>> ResolveTargetsAsync(ManagementDbContext db, CommandTarget target, CancellationToken cancellationToken)
    {
        IQueryable<Device> query = db.Devices.Where(x => !x.IsDisabled);
        switch (target.Kind)
        {
            case TargetKind.All:
                return await query.OrderBy(x => x.DeviceId).ToListAsync(cancellationToken);
            case TargetKind.Device:
            case TargetKind.Devices:
                var ids = target.DeviceIds?.Where(ProtocolParser.IsValidDeviceId).Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
                if (ids.Length == 0)
                {
                    throw new ArgumentException("At least one device ID is required.");
                }
                var selected = await query.Where(x => ids.Contains(x.DeviceId)).OrderBy(x => x.DeviceId).ToListAsync(cancellationToken);
                if (selected.Count != ids.Length)
                {
                    throw new ArgumentException("One or more selected device IDs are unknown.");
                }
                return selected;
            case TargetKind.Group:
                if (!target.GroupId.HasValue)
                {
                    throw new ArgumentException("A group ID is required.");
                }
                return await (from device in db.Devices
                              join membership in db.DeviceGroupMemberships on device.DeviceId equals membership.DeviceId
                              where membership.DeviceGroupId == target.GroupId.Value && !device.IsDisabled
                              orderby device.DeviceId
                              select device).ToListAsync(cancellationToken);
            default:
                throw new ArgumentOutOfRangeException(nameof(target.Kind));
        }
    }

    private static string DescribeTarget(CommandTarget target, IReadOnlyCollection<Device> devices) => target.Kind switch
    {
        TargetKind.All => $"ALL DEVICES ({devices.Count})",
        TargetKind.Group => $"GROUP {target.GroupId} ({devices.Count})",
        TargetKind.Device => devices.Count == 1 ? $"DEVICE {devices.First().DeviceId}" : $"SELECTED DEVICES ({devices.Count})",
        TargetKind.Devices => $"SELECTED DEVICES ({devices.Count})",
        _ => $"DEVICES ({devices.Count})"
    };

    private static async Task<CommandOperationProgress> BuildProgressAsync(ManagementDbContext db, CommandOperation operation, CancellationToken cancellationToken)
    {
        var jobs = await db.CommandJobs.AsNoTracking().Where(x => x.OperationId == operation.OperationId)
            .OrderBy(x => x.DeviceId).ToListAsync(cancellationToken);
        var deviceIds = jobs.Select(x => x.DeviceId).Distinct().ToArray();
        var hostnames = await db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.DeviceId))
            .ToDictionaryAsync(x => x.DeviceId, x => x.Hostname, cancellationToken);
        var summary = jobs.GroupBy(x => x.Status.ToProtocolValue()).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        foreach (var knownStatus in new[] { "PENDING", "SENT", "RECEIVED", "RUNNING", "SUCCESS", "FAILED", "TIMEOUT", "CANCELLED" })
        {
            summary.TryAdd(knownStatus, 0);
        }
        return new CommandOperationProgress(operation.OperationId, operation.CommandType, operation.TargetDescription,
            operation.CreatedAt, operation.CreatedBy, summary,
            jobs.Select(x => new CommandJobView(x.CommandId, x.DeviceId, hostnames.GetValueOrDefault(x.DeviceId, x.DeviceId),
                x.Status.ToProtocolValue(), x.AttemptCount, x.CreatedAt, x.LastAttemptAt, x.ReceivedAt, x.StartedAt,
                x.CompletedAt, x.ExitCode, x.Output, x.Error)).ToList());
    }

    private static async Task<bool> IsProcessedAsync(ManagementDbContext db, string deviceId, Guid messageId, CancellationToken cancellationToken) =>
        await db.ProcessedAgentMessages.AnyAsync(x => x.DeviceId == deviceId && x.MessageId == messageId, cancellationToken);

    private static string? ToJsonOrNull(JsonElement? value) =>
        !value.HasValue || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value.Value.GetRawText();

    private static string? Truncate(string? value, int maximum) => string.IsNullOrEmpty(value) ? value : value.Length <= maximum ? value : value[..maximum];

    private static async Task UpdateReportedApplicationsAsync(
        ManagementDbContext db,
        CommandJob command,
        CommandStatus resultStatus,
        string? resultDataJson,
        DateTimeOffset reportedAt,
        CancellationToken cancellationToken)
    {
        if (command.CommandType != CommandTypes.GetInstalledApps || resultStatus != CommandStatus.Success || string.IsNullOrWhiteSpace(resultDataJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(resultDataJson);
            var apps = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : document.RootElement.TryGetProperty("applications", out var property) && property.ValueKind == JsonValueKind.Array
                    ? property.EnumerateArray().ToArray()
                    : Array.Empty<JsonElement>();
            foreach (var app in apps)
            {
                if (!app.TryGetProperty("package_id", out var packageElement) || packageElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var packageId = packageElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(packageId) || packageId.Length > 256)
                {
                    continue;
                }
                var display = app.TryGetProperty("display_name", out var displayElement) && displayElement.ValueKind == JsonValueKind.String
                    ? displayElement.GetString() ?? packageId
                    : packageId;
                var version = app.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String
                    ? versionElement.GetString()
                    : null;
                var existing = await db.DeviceApplications.SingleOrDefaultAsync(x => x.DeviceId == command.DeviceId && x.PackageId == packageId, cancellationToken);
                if (existing is null)
                {
                    db.DeviceApplications.Add(new DeviceApplication
                    {
                        DeviceId = command.DeviceId,
                        PackageId = packageId,
                        DisplayName = Truncate(display, 256) ?? packageId,
                        InstalledVersion = Truncate(version, 128),
                        IsInstalled = true,
                        ReportedAt = reportedAt
                    });
                }
                else
                {
                    existing.DisplayName = Truncate(display, 256) ?? packageId;
                    existing.InstalledVersion = Truncate(version, 128);
                    existing.IsInstalled = true;
                    existing.ReportedAt = reportedAt;
                }
            }
        }
        catch (JsonException)
        {
            // Preserve the original result as audit evidence. A malformed optional application list does not reject a command result.
        }
    }
}
