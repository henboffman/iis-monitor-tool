using Microsoft.Win32.TaskScheduler;

namespace iis_monitor_app.Services;

public class ScheduledTaskService
{
    private readonly ILogger<ScheduledTaskService> _logger;

    public ScheduledTaskService(ILogger<ScheduledTaskService> logger)
    {
        _logger = logger;
    }

    public List<ScheduledTaskInfo> GetAllTasks(string? folderPath = null)
    {
        var tasks = new List<ScheduledTaskInfo>();

        try
        {
            using var ts = new TaskService();
            var folder = string.IsNullOrEmpty(folderPath)
                ? ts.RootFolder
                : ts.GetFolder(folderPath);

            if (folder != null)
            {
                CollectTasks(folder, tasks);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting scheduled tasks");
        }

        return tasks;
    }

    private void CollectTasks(TaskFolder folder, List<ScheduledTaskInfo> tasks)
    {
        // Get tasks in current folder
        foreach (var task in folder.Tasks)
        {
            try
            {
                var taskInfo = MapTaskToInfo(task);
                tasks.Add(taskInfo);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading task {TaskName}", task.Name);
            }
        }

        // Recursively get tasks from subfolders
        foreach (var subFolder in folder.SubFolders)
        {
            CollectTasks(subFolder, tasks);
        }
    }

    public ScheduledTaskInfo? GetTaskByPath(string taskPath)
    {
        try
        {
            using var ts = new TaskService();
            var task = ts.GetTask(taskPath);
            if (task != null)
            {
                return MapTaskToInfo(task);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting task {TaskPath}", taskPath);
        }

        return null;
    }

    public List<TaskRunInfo> GetTaskRunHistory(string taskPath, int maxEntries = 100)
    {
        var runHistory = new List<TaskRunInfo>();

        try
        {
            using var ts = new TaskService();
            var task = ts.GetTask(taskPath);

            if (task != null)
            {
                // Get run history from event log
                var eventLog = new System.Diagnostics.EventLog("Microsoft-Windows-TaskScheduler/Operational");

                // Filter events for this task
                var taskName = task.Name;
                var entries = new List<TaskRunInfo>();

                // Use TaskService's built-in history if available
                foreach (var run in task.GetRunTimes(DateTime.Now.AddDays(-30), DateTime.Now))
                {
                    // This gives scheduled run times, not actual runs
                }

                // Query task history via COM
                var history = GetTaskHistory(task, maxEntries);
                runHistory.AddRange(history);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting task history for {TaskPath}", taskPath);
        }

        return runHistory;
    }

    private List<TaskRunInfo> GetTaskHistory(Microsoft.Win32.TaskScheduler.Task task, int maxEntries)
    {
        var history = new List<TaskRunInfo>();

        try
        {
            // Get history from the task's event log
            var eventLog = new TaskEventLog(task.Path);
            var eventList = eventLog.OrderByDescending(e => e.TimeCreated).Take(maxEntries * 2).ToList();

            // Group events into runs
            TaskRunInfo? currentRun = null;

            foreach (var evt in eventList.OrderBy(e => e.TimeCreated))
            {
                // Event IDs for Task Scheduler:
                // 100 = Task started (started a task)
                // 101 = Task started successfully
                // 102 = Task completed
                // 103 = Task failed to start
                // 107 = Task triggered
                // 110 = Task launched
                // 129 = Task was launched (created new process)
                // 200 = Action started
                // 201 = Action completed
                // 202 = Action failed

                var eventCode = evt.EventId;
                var eventTime = evt.TimeCreated ?? DateTime.Now;

                if (eventCode == 100 || eventCode == 107 || eventCode == 110 || eventCode == 129)
                {
                    // Task started
                    if (currentRun != null && currentRun.EndTime == null)
                    {
                        // Previous run didn't complete properly
                        currentRun.EndTime = eventTime;
                        currentRun.ResultCode = -1;
                        currentRun.ResultMessage = "Unknown (no completion event)";
                        history.Add(currentRun);
                    }

                    currentRun = new TaskRunInfo
                    {
                        TaskPath = task.Path,
                        TaskName = task.Name,
                        StartTime = eventTime,
                        TriggeredBy = GetTriggerDescription(evt)
                    };
                }
                else if (eventCode == 102 || eventCode == 201)
                {
                    // Task/Action completed
                    if (currentRun != null)
                    {
                        currentRun.EndTime = eventTime;
                        currentRun.ResultCode = 0;
                        currentRun.ResultMessage = "Success";
                        currentRun.Duration = currentRun.EndTime.Value - currentRun.StartTime;
                        history.Add(currentRun);
                        currentRun = null;
                    }
                }
                else if (eventCode == 103 || eventCode == 202)
                {
                    // Task/Action failed
                    if (currentRun != null)
                    {
                        currentRun.EndTime = eventTime;
                        currentRun.ResultCode = GetResultCodeFromEvent(evt);
                        currentRun.ResultMessage = GetResultMessageFromEvent(evt);
                        currentRun.Duration = currentRun.EndTime.Value - currentRun.StartTime;
                        history.Add(currentRun);
                        currentRun = null;
                    }
                    else
                    {
                        // Failed to start - create entry
                        history.Add(new TaskRunInfo
                        {
                            TaskPath = task.Path,
                            TaskName = task.Name,
                            StartTime = eventTime,
                            EndTime = eventTime,
                            ResultCode = GetResultCodeFromEvent(evt),
                            ResultMessage = GetResultMessageFromEvent(evt),
                            Duration = TimeSpan.Zero
                        });
                    }
                }
            }

            // Add any in-progress run
            if (currentRun != null)
            {
                currentRun.ResultMessage = "Running";
                history.Add(currentRun);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not retrieve task history events, using last run info only");

            // Fallback to basic last run info
            if (task.LastRunTime > DateTime.MinValue)
            {
                history.Add(new TaskRunInfo
                {
                    TaskPath = task.Path,
                    TaskName = task.Name,
                    StartTime = task.LastRunTime,
                    EndTime = task.LastRunTime,
                    ResultCode = task.LastTaskResult,
                    ResultMessage = GetResultMessage(task.LastTaskResult)
                });
            }
        }

        return history.OrderByDescending(r => r.StartTime).Take(maxEntries).ToList();
    }

    private ScheduledTaskInfo MapTaskToInfo(Microsoft.Win32.TaskScheduler.Task task)
    {
        var info = new ScheduledTaskInfo
        {
            Name = task.Name,
            Path = task.Path,
            FolderPath = task.Folder?.Path ?? "\\",
            Description = task.Definition.RegistrationInfo.Description ?? "",
            Author = task.Definition.RegistrationInfo.Author ?? "",
            State = task.State.ToString(),
            Enabled = task.Enabled,
            LastRunTime = task.LastRunTime > DateTime.MinValue ? task.LastRunTime : null,
            NextRunTime = task.NextRunTime > DateTime.MinValue ? task.NextRunTime : null,
            LastTaskResult = task.LastTaskResult,
            LastTaskResultMessage = GetResultMessage(task.LastTaskResult)
        };

        // Get triggers
        foreach (var trigger in task.Definition.Triggers)
        {
            info.Triggers.Add(new TaskTriggerInfo
            {
                Type = trigger.TriggerType.ToString(),
                Enabled = trigger.Enabled,
                StartBoundary = trigger.StartBoundary,
                Description = GetTriggerSummary(trigger)
            });
        }

        // Get actions
        foreach (var action in task.Definition.Actions)
        {
            var actionInfo = new TaskActionInfo
            {
                Type = action.ActionType.ToString()
            };

            if (action is ExecAction execAction)
            {
                actionInfo.Path = execAction.Path;
                actionInfo.Arguments = execAction.Arguments;
                actionInfo.WorkingDirectory = execAction.WorkingDirectory;
            }

            info.Actions.Add(actionInfo);
        }

        // Get settings
        info.Settings = new TaskSettingsInfo
        {
            AllowDemandStart = task.Definition.Settings.AllowDemandStart,
            StopIfGoingOnBatteries = task.Definition.Settings.StopIfGoingOnBatteries,
            RunOnlyIfNetworkAvailable = task.Definition.Settings.RunOnlyIfNetworkAvailable,
            RunOnlyIfIdle = task.Definition.Settings.RunOnlyIfIdle,
            Hidden = task.Definition.Settings.Hidden,
            Priority = (int)task.Definition.Settings.Priority,
            RestartCount = task.Definition.Settings.RestartCount,
            MultipleInstances = task.Definition.Settings.MultipleInstances.ToString()
        };

        // Get principal/security
        info.Principal = new TaskPrincipalInfo
        {
            UserId = task.Definition.Principal.UserId ?? "",
            LogonType = task.Definition.Principal.LogonType.ToString(),
            RunLevel = task.Definition.Principal.RunLevel.ToString()
        };

        return info;
    }

    private string GetTriggerSummary(Trigger trigger)
    {
        return trigger switch
        {
            DailyTrigger daily => $"Daily at {daily.StartBoundary:HH:mm}, every {daily.DaysInterval} day(s)",
            WeeklyTrigger weekly => $"Weekly on {weekly.DaysOfWeek} at {weekly.StartBoundary:HH:mm}",
            MonthlyTrigger monthly => $"Monthly on day {string.Join(",", monthly.DaysOfMonth)} at {monthly.StartBoundary:HH:mm}",
            TimeTrigger time => $"Once at {time.StartBoundary:yyyy-MM-dd HH:mm}",
            LogonTrigger => "At logon",
            BootTrigger => "At startup",
            IdleTrigger => "On idle",
            EventTrigger evt => $"On event: {evt.Subscription}",
            _ => trigger.TriggerType.ToString()
        };
    }

    private string GetTriggerDescription(TaskEvent evt)
    {
        try
        {
            var triggerName = evt.DataValues?["TriggerName"];
            return !string.IsNullOrEmpty(triggerName) ? triggerName : "Manual/Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private int GetResultCodeFromEvent(TaskEvent evt)
    {
        try
        {
            var resultStr = evt.DataValues?["ResultCode"];
            return int.TryParse(resultStr, out var result) ? result : -1;
        }
        catch
        {
            return -1;
        }
    }

    private string GetResultMessageFromEvent(TaskEvent evt)
    {
        try
        {
            var message = evt.DataValues?["Message"];
            if (!string.IsNullOrEmpty(message))
            {
                return message;
            }
            return GetResultMessage(GetResultCodeFromEvent(evt));
        }
        catch
        {
            return "Error";
        }
    }

    public static string GetResultMessage(int resultCode)
    {
        return resultCode switch
        {
            0 => "Success",
            1 => "Incorrect function (or task still running)",
            2 => "File not found",
            10 => "Environment incorrect",
            0x00041300 => "Task is ready to run",
            0x00041301 => "Task is currently running",
            0x00041302 => "Task is disabled",
            0x00041303 => "Task has not yet run",
            0x00041304 => "No more runs scheduled",
            0x00041305 => "Task is not scheduled to run again",
            0x00041306 => "Task terminated by user",
            0x00041307 => "No instances running",
            0x00041308 => "Queued",
            unchecked((int)0x8004130F) => "Credentials required",
            unchecked((int)0x8004131F) => "Instance already running",
            -2147024891 => "Access denied",
            -2147024894 => "File not found",
            -2147467259 => "Unspecified failure",
            _ => $"Error code: 0x{resultCode:X8} ({resultCode})"
        };
    }

    public List<TaskFolderInfo> GetTaskFolders()
    {
        var folders = new List<TaskFolderInfo>();

        try
        {
            using var ts = new TaskService();
            CollectFolders(ts.RootFolder, folders);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting task folders");
        }

        return folders;
    }

    private void CollectFolders(TaskFolder folder, List<TaskFolderInfo> folders)
    {
        folders.Add(new TaskFolderInfo
        {
            Name = folder.Name,
            Path = folder.Path,
            TaskCount = folder.Tasks.Count
        });

        foreach (var subFolder in folder.SubFolders)
        {
            CollectFolders(subFolder, folders);
        }
    }
}

// Data Models
public class ScheduledTaskInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTime? LastRunTime { get; set; }
    public DateTime? NextRunTime { get; set; }
    public int LastTaskResult { get; set; }
    public string LastTaskResultMessage { get; set; } = string.Empty;
    public List<TaskTriggerInfo> Triggers { get; set; } = new();
    public List<TaskActionInfo> Actions { get; set; } = new();
    public TaskSettingsInfo Settings { get; set; } = new();
    public TaskPrincipalInfo Principal { get; set; } = new();
}

public class TaskTriggerInfo
{
    public string Type { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTime StartBoundary { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class TaskActionInfo
{
    public string Type { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
}

public class TaskSettingsInfo
{
    public bool AllowDemandStart { get; set; }
    public bool StopIfGoingOnBatteries { get; set; }
    public bool RunOnlyIfNetworkAvailable { get; set; }
    public bool RunOnlyIfIdle { get; set; }
    public bool Hidden { get; set; }
    public int Priority { get; set; }
    public int RestartCount { get; set; }
    public string MultipleInstances { get; set; } = string.Empty;
}

public class TaskPrincipalInfo
{
    public string UserId { get; set; } = string.Empty;
    public string LogonType { get; set; } = string.Empty;
    public string RunLevel { get; set; } = string.Empty;
}

public class TaskRunInfo
{
    public string TaskPath { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public TimeSpan? Duration { get; set; }
    public int ResultCode { get; set; }
    public string ResultMessage { get; set; } = string.Empty;
    public string TriggeredBy { get; set; } = string.Empty;
}

public class TaskFolderInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int TaskCount { get; set; }
}
