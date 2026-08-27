namespace OMClientAgent.Core.Models;

public enum JobExecutionMode
{
    Admin = 0,
    User = 1
}

public enum JobStatus
{
    Pending = 0,
    Dispatched = 1,
    Acknowledged = 2,
    Running = 3,
    Success = 4,
    Failed = 5,
    Timeout = 6,
    Retrying = 7,
    WaitingForUser = 8,
    Cancelled = 9,
    Waiting = 10
}

public enum ConnectionState
{
    Unknown = 0,
    Connected = 1,
    Degraded = 2,
    Offline = 3
}

public enum ApplicationAction
{
    Block = 0,
    Unblock = 1
}

public enum JobPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

public enum JobType
{
    Command = 0,
    Software = 1,
    SoftwareInstall = 2,
    SoftwareUninstall = 3,
    SoftwareUpdate = 4,
    Policy = 5,
    Dns = 6,
    Restart = 7,
    CollectInfo = 8,
    AgentUpdate = 9
}

public enum ApplyState
{
    NotApplied = 0,
    Applied = 1,
    Failed = 2,
    Pending = 3
}

public enum PolicyMatch
{
    NotMatched = 0,
    Allowed = 1,
    Blocked = 2
}
