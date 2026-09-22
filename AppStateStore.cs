using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NextCue;

public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public AppStateStore()
    {
        DefaultStateDirectory = NormalizeDataDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NextCue"));
        ConfigPath = Path.Combine(DefaultStateDirectory, "config.json");

        SetActiveDataDirectory(ResolveDataDirectoryFromConfig());
    }

    public string DefaultStateDirectory { get; }

    public string ConfigPath { get; }

    public string StateDirectory { get; private set; } = "";

    public string StatePath { get; private set; } = "";

    public bool IsUsingCustomDataDirectory => !PathsEqual(StateDirectory, DefaultStateDirectory);

    public AppState Load()
    {
        try
        {
            EnsureStateDirectoryAvailableForLoad();

            if (!File.Exists(StatePath))
            {
                return Normalize(new AppState());
            }

            return ReadStateFile(StatePath);
        }
        catch (DataDirectoryUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (IsUsingCustomDataDirectory && IsDataAccessException(ex))
        {
            throw new DataDirectoryUnavailableException(StateDirectory, ex);
        }
        catch
        {
            PreserveCorruptedStateFile();
            return Normalize(new AppState());
        }
    }

    public void Save(AppState state)
    {
        try
        {
            WriteStateFileAtomic(StatePath, state);
        }
        catch
        {
        }
    }

    public void ReloadBootstrapConfig()
    {
        SetActiveDataDirectory(ResolveDataDirectoryFromConfig());
    }

    public DataLocationChangeResult ChangeDataDirectory(string destinationDirectory, AppState state)
    {
        try
        {
            string destination = NormalizeDataDirectory(destinationDirectory);
            ValidateDestinationDirectory(destination);
            PrepareDataDirectoryForUse(destination);

            if (PathsEqual(destination, StateDirectory))
            {
                return DataLocationChangeResult.Ok("已使用该数据存储位置。");
            }

            WriteStateFileAtomic(StatePath, state);

            string destinationStatePath = Path.Combine(destination, "state.json");
            WriteStateFileAtomic(destinationStatePath, state);
            _ = ReadStateFile(destinationStatePath);

            WriteBootstrapConfig(destination);
            SetActiveDataDirectory(destination);

            return DataLocationChangeResult.Ok("已更改数据存储位置。");
        }
        catch (Exception ex)
        {
            return DataLocationChangeResult.Fail(GetDataLocationErrorMessage(ex));
        }
    }

    public DataLocationChangeResult UseDataDirectoryWithoutMigration(string destinationDirectory)
    {
        try
        {
            string destination = NormalizeDataDirectory(destinationDirectory);
            ValidateDestinationDirectory(destination);
            PrepareDataDirectoryForUse(destination);
            WriteBootstrapConfig(destination);
            SetActiveDataDirectory(destination);

            return DataLocationChangeResult.Ok("已切换数据存储位置。");
        }
        catch (Exception ex)
        {
            return DataLocationChangeResult.Fail(GetDataLocationErrorMessage(ex));
        }
    }

    private void SetActiveDataDirectory(string directory)
    {
        StateDirectory = NormalizeDataDirectory(directory);
        StatePath = Path.Combine(StateDirectory, "state.json");
    }

    private AppState Normalize(AppState state)
    {
        state.OngoingPlans ??= [];
        state.PlanHistory ??= [];
        state.Settings ??= new AppSettings();

        if (state.ActivePlan is not null)
        {
            NormalizePlan(state.ActivePlan);

            if (state.ActivePlan.Status is not PlanStatus.Completed and not PlanStatus.Ended &&
                state.OngoingPlans.All(plan => plan.Id != state.ActivePlan.Id))
            {
                state.ActivePlan.Status = PlanStatus.Ongoing;
                state.OngoingPlans.Add(state.ActivePlan);
            }

            state.CurrentPlanId ??= state.ActivePlan.Id;
            state.ActivePlan = null;
        }

        List<Plan> normalizedHistory = [];

        foreach (Plan plan in state.PlanHistory)
        {
            NormalizePlan(plan);

            if (plan.Status is PlanStatus.Active or PlanStatus.Ongoing)
            {
                plan.Status = PlanStatus.Ongoing;

                if (state.OngoingPlans.All(ongoingPlan => ongoingPlan.Id != plan.Id))
                {
                    state.OngoingPlans.Add(plan);
                }

                state.CurrentPlanId ??= plan.Id;
                continue;
            }

            normalizedHistory.Add(plan);
        }

        state.PlanHistory = normalizedHistory;

        List<Plan> normalizedOngoingPlans = [];

        foreach (Plan plan in state.OngoingPlans)
        {
            NormalizePlan(plan);

            if (plan.Status is PlanStatus.Completed or PlanStatus.Ended)
            {
                if (normalizedHistory.All(historyPlan => historyPlan.Id != plan.Id))
                {
                    normalizedHistory.Add(plan);
                }

                continue;
            }

            plan.Status = PlanStatus.Ongoing;
            plan.CompletedAt = null;
            plan.EndedAt = null;
            normalizedOngoingPlans.Add(plan);
        }

        state.OngoingPlans = normalizedOngoingPlans
            .GroupBy(plan => plan.Id)
            .Select(group => group.First())
            .ToList();

        state.PlanHistory = normalizedHistory
            .GroupBy(plan => plan.Id)
            .Select(group => group.First())
            .ToList();

        if (state.CurrentPlanId.HasValue && state.OngoingPlans.All(plan => plan.Id != state.CurrentPlanId.Value))
        {
            state.CurrentPlanId = null;
        }

        if (!state.CurrentPlanId.HasValue && state.OngoingPlans.Count > 0)
        {
            state.CurrentPlanId = state.OngoingPlans
                .OrderByDescending(plan => plan.LastAccessedAt)
                .ThenByDescending(plan => plan.CreatedAt)
                .First()
                .Id;
        }

        if (state.Settings.WindowWidth < 280 || state.Settings.WindowWidth > 420)
        {
            state.Settings.WindowWidth = 310;
        }

        if (state.Settings.ExpandedWidth <= 0)
        {
            state.Settings.ExpandedWidth = state.Settings.WindowWidth;
        }

        if (state.Settings.ExpandedWidth < 280 || state.Settings.ExpandedWidth > 420)
        {
            state.Settings.ExpandedWidth = 310;
        }

        if (state.Settings.ExpandedHeight < 500)
        {
            state.Settings.ExpandedHeight = 700;
        }

        if (state.Settings.CompactWidth < 145 || state.Settings.CompactWidth > 260)
        {
            state.Settings.CompactWidth = 170;
        }

        if (state.Settings.CompactHeight < 240)
        {
            state.Settings.CompactHeight = 700;
        }

        if (!Enum.IsDefined(state.Settings.DockSide))
        {
            state.Settings.DockSide = DockSide.Right;
        }

        return state;
    }

    private string ResolveDataDirectoryFromConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return DefaultStateDirectory;
            }

            string json = File.ReadAllText(ConfigPath);
            BootstrapConfig? config = JsonSerializer.Deserialize<BootstrapConfig>(json, JsonOptions);

            if (string.IsNullOrWhiteSpace(config?.DataDirectory))
            {
                return DefaultStateDirectory;
            }

            return NormalizeDataDirectory(config.DataDirectory);
        }
        catch
        {
            return DefaultStateDirectory;
        }
    }

    private void EnsureStateDirectoryAvailableForLoad()
    {
        if (IsUsingCustomDataDirectory && !Directory.Exists(StateDirectory))
        {
            throw new DataDirectoryUnavailableException(StateDirectory);
        }

        try
        {
            PrepareDataDirectoryForUse(StateDirectory);
        }
        catch (Exception ex) when (IsUsingCustomDataDirectory)
        {
            throw new DataDirectoryUnavailableException(StateDirectory, ex);
        }
    }

    private void WriteBootstrapConfig(string dataDirectory)
    {
        Directory.CreateDirectory(DefaultStateDirectory);

        if (PathsEqual(dataDirectory, DefaultStateDirectory))
        {
            TryDelete(ConfigPath);
            return;
        }

        string? tempPath = null;

        try
        {
            tempPath = Path.Combine(DefaultStateDirectory, $"config.{Guid.NewGuid():N}.tmp");
            BootstrapConfig config = new()
            {
                DataDirectory = dataDirectory
            };
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
        catch
        {
            if (tempPath is not null)
            {
                TryDelete(tempPath);
            }

            throw;
        }
    }

    private AppState ReadStateFile(string statePath)
    {
        string json = File.ReadAllText(statePath);
        AppState? state = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
        return Normalize(state ?? new AppState());
    }

    private string WriteStateFileAtomic(string statePath, AppState state)
    {
        string directory = Path.GetDirectoryName(statePath) ?? StateDirectory;
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, $"state.{Guid.NewGuid():N}.tmp");

        try
        {
            string json = JsonSerializer.Serialize(Normalize(state), JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, statePath, overwrite: true);
            return tempPath;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private void PrepareDataDirectoryForUse(string directory)
    {
        Directory.CreateDirectory(directory);
        VerifyDirectoryWritable(directory);
    }

    private void ValidateDestinationDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("请选择一个有效的数据文件夹。");
        }

        string executableDirectory = NormalizeDataDirectory(AppContext.BaseDirectory);

        if (!PathsEqual(directory, DefaultStateDirectory) && PathsEqual(directory, executableDirectory))
        {
            throw new InvalidOperationException("请不要把数据存放在 NextCue 程序目录中。");
        }
    }

    private static void NormalizePlan(Plan plan)
    {
        if (plan.Id == Guid.Empty)
        {
            plan.Id = Guid.NewGuid();
        }

        plan.OverallTask ??= "";
        plan.Steps ??= [];

        if (plan.LastAccessedAt == default)
        {
            plan.LastAccessedAt = plan.CreatedAt == default ? DateTimeOffset.Now : plan.CreatedAt;
        }

        foreach (PlanStep step in plan.Steps)
        {
            if (step.Id == Guid.Empty)
            {
                step.Id = Guid.NewGuid();
            }

            step.Text ??= "";
        }
    }

    private void PreserveCorruptedStateFile()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return;
            }

            string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            string corruptPath = Path.Combine(StateDirectory, $"state.corrupt.{timestamp}.json");
            File.Move(StatePath, corruptPath, overwrite: true);
        }
        catch
        {
            // Startup must never fail because local state could not be renamed.
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new()
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static void VerifyDirectoryWritable(string directory)
    {
        string testPath = Path.Combine(directory, $".nextcue-write-test-{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(testPath, "");
        }
        finally
        {
            TryDelete(testPath);
        }
    }

    private static string NormalizeDataDirectory(string directory)
    {
        string expanded = Environment.ExpandEnvironmentVariables(directory.Trim());
        string fullPath = Path.GetFullPath(expanded);
        string root = Path.GetPathRoot(fullPath) ?? "";
        string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }

    private static bool PathsEqual(string firstPath, string secondPath)
    {
        try
        {
            return string.Equals(
                NormalizeDataDirectory(firstPath),
                NormalizeDataDirectory(secondPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string GetDataLocationErrorMessage(Exception exception)
    {
        if (exception is InvalidOperationException invalidOperationException)
        {
            return invalidOperationException.Message;
        }

        if (exception is UnauthorizedAccessException)
        {
            return "无法写入所选数据文件夹。";
        }

        if (exception is IOException)
        {
            return "无法访问所选数据文件夹。";
        }

        if (exception is JsonException)
        {
            return "写入后的状态文件校验失败。";
        }

        return "更改数据存储位置失败。";
    }

    private static bool IsDataAccessException(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or NotSupportedException;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}

public sealed class DataDirectoryUnavailableException : Exception
{
    public DataDirectoryUnavailableException(string dataDirectory, Exception? innerException = null)
        : base($"无法访问数据位置：{dataDirectory}", innerException)
    {
        DataDirectory = dataDirectory;
    }

    public string DataDirectory { get; }
}

public sealed record DataLocationChangeResult(bool Success, string Message)
{
    public static DataLocationChangeResult Ok(string message)
    {
        return new DataLocationChangeResult(true, message);
    }

    public static DataLocationChangeResult Fail(string message)
    {
        return new DataLocationChangeResult(false, message);
    }
}

public sealed class BootstrapConfig
{
    public string? DataDirectory { get; set; }
}
