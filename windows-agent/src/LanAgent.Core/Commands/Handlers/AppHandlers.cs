using LanAgent.Core.Management;

namespace LanAgent.Core.Commands.Handlers;

public sealed class InstallAppHandler : ICommandHandler
{
    private readonly IWingetClient _winget;
    public InstallAppHandler(IWingetClient winget) => _winget = winget;
    public string CommandType => Protocol.CommandType.InstallApp;

    public async Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        string? packageId = context.PayloadString("package_id");
        if (string.IsNullOrWhiteSpace(packageId)) return HandlerResult.Fail("payload.package_id is required");
        string scope = context.PayloadString("scope") ?? "machine";

        var result = await _winget.InstallAsync(packageId, scope, context.CancellationToken).ConfigureAwait(false);
        return ToResult(packageId, result);
    }

    private static HandlerResult ToResult(string packageId, WingetOperationResult result)
    {
        var payload = new Dictionary<string, object?>
        {
            ["package_id"] = packageId,
            ["already_installed"] = result.AlreadyInstalled,
            ["installed_version"] = result.InstalledVersion,
            ["winget_exit_code"] = result.WingetExitCode,
            ["stdout_tail"] = result.StdOutTail,
            ["stderr_tail"] = result.StdErrTail
        };
        return result.Success
            ? HandlerResult.Ok(payload)
            : HandlerResult.Fail(result.Detail, payload);
    }
}

public sealed class UninstallAppHandler : ICommandHandler
{
    private readonly IWingetClient _winget;
    public UninstallAppHandler(IWingetClient winget) => _winget = winget;
    public string CommandType => Protocol.CommandType.UninstallApp;

    public async Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        string? packageId = context.PayloadString("package_id");
        if (string.IsNullOrWhiteSpace(packageId)) return HandlerResult.Fail("payload.package_id is required");

        var result = await _winget.UninstallAsync(packageId, context.CancellationToken).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>
        {
            ["package_id"] = packageId,
            ["already_installed"] = result.AlreadyInstalled,
            ["installed_version"] = result.InstalledVersion,
            ["winget_exit_code"] = result.WingetExitCode,
            ["stdout_tail"] = result.StdOutTail,
            ["stderr_tail"] = result.StdErrTail
        };
        return result.Success
            ? HandlerResult.Ok(payload)
            : HandlerResult.Fail(result.Detail, payload);
    }
}

public sealed class UpdateAppHandler : ICommandHandler
{
    private readonly IWingetClient _winget;
    public UpdateAppHandler(IWingetClient winget) => _winget = winget;
    public string CommandType => Protocol.CommandType.UpdateApp;

    public async Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        string? packageId = context.PayloadString("package_id");
        if (string.IsNullOrWhiteSpace(packageId)) return HandlerResult.Fail("payload.package_id is required");

        var result = await _winget.UpgradeAsync(packageId, context.CancellationToken).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>
        {
            ["package_id"] = packageId,
            ["was_up_to_date"] = result.Detail.Contains("up to date", StringComparison.OrdinalIgnoreCase),
            ["installed_version"] = result.InstalledVersion,
            ["winget_exit_code"] = result.WingetExitCode,
            ["stdout_tail"] = result.StdOutTail,
            ["stderr_tail"] = result.StdErrTail
        };
        return result.Success
            ? HandlerResult.Ok(payload)
            : HandlerResult.Fail(result.Detail, payload);
    }
}

public sealed class CheckAppHandler : ICommandHandler
{
    private readonly IWingetClient _winget;
    public CheckAppHandler(IWingetClient winget) => _winget = winget;
    public string CommandType => Protocol.CommandType.CheckApp;

    public async Task<HandlerResult> ExecuteAsync(CommandContext context)
    {
        string? packageId = context.PayloadString("package_id");
        if (string.IsNullOrWhiteSpace(packageId)) return HandlerResult.Fail("payload.package_id is required");

        var status = await _winget.CheckAsync(packageId, context.CancellationToken).ConfigureAwait(false);
        return HandlerResult.Ok(new Dictionary<string, object?>
        {
            ["package_id"] = packageId,
            ["installed"] = status.Installed,
            ["version"] = status.Version,
            ["source"] = status.Source,
            ["detail"] = status.Detail
        });
    }
}
