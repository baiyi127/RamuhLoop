#nullable enable
using System;

namespace RamuhLoop.Services;

public enum WorkResult { Pending, Complete, Failed }
public enum RepairResume { QueueDuty, OpenCoffer }

public readonly record struct RepairCheckpointContext(
    bool Ready, bool InDuty, bool QueueEmpty, uint CurrentJob, uint Territory);

public interface IEquipmentRepairOperations
{
    int ReadMinimumCondition();
    void Begin();
    WorkResult Tick();
    void Reset();
    string Error { get; }
    string Status { get; }
}

/// <summary>Runs self repair only while the expected out-of-duty job checkpoint remains valid.</summary>
public sealed class RepairCheckpoint
{
    private enum Phase { Inactive, Checking, Repairing, Complete, Failed }
    private readonly IEquipmentRepairOperations operations;
    private Phase phase;
    private uint expectedJob;
    private uint expectedTerritory;

    public string Error { get; private set; } = "";
    public string Status { get; private set; } = "";
    public RepairResume Resume { get; private set; }

    public RepairCheckpoint(IEquipmentRepairOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        this.operations = operations;
    }

    public void Begin(uint expectedJob, uint territory, RepairResume continuation)
    {
        Reset();
        Resume = continuation;
        if (Error.Length > 0) { phase = Phase.Failed; return; }
        this.expectedJob = expectedJob;
        expectedTerritory = territory;
        phase = Phase.Checking;
        Status = "检查当前穿戴装备耐久";
    }

    public WorkResult Tick(RepairCheckpointContext context)
    {
        // Explicit Begin is required. Stop/Reset and failed checkpoints cannot restart work.
        if (phase == Phase.Inactive)
        {
            Error = Status = "维修检查点尚未开始或已取消。";
            return WorkResult.Failed;
        }
        if (phase == Phase.Failed) return WorkResult.Failed;

        if (!context.Ready) return Fail("角色尚未就绪或正在战斗，停止维修检查点。");
        if (context.InDuty) return Fail("角色已进入副本，停止维修检查点。");
        if (!context.QueueEmpty) return Fail("外部任务队列不为空，停止维修检查点。");
        if (expectedJob == 0 || context.CurrentJob != expectedJob)
            return Fail("当前职业与维修检查点要求不符，停止维修。");
        if (expectedTerritory == 0 || context.Territory != expectedTerritory)
            return Fail("区域发生变化，停止维修检查点。");
        if (phase == Phase.Complete) return WorkResult.Complete;

        try
        {
            if (phase == Phase.Checking)
            {
                var condition = operations.ReadMinimumCondition();
                if (condition < 0) return Fail("装备耐久数据尚未就绪，停止维修检查点。");
                if (!RepairPolicy.ShouldRepair(condition))
                    return Complete(condition == int.MaxValue ? "没有可维修装备，继续流程" : "装备耐久无需维修，继续流程");
                operations.Begin();
                phase = Phase.Repairing;
                Status = string.IsNullOrEmpty(operations.Status) ? "等待自行维修完成" : operations.Status;
                return WorkResult.Pending;
            }

            var result = operations.Tick();
            if (result == WorkResult.Complete)
            {
                var detail = operations.Status;
                return Complete(string.IsNullOrEmpty(detail) ? "自行维修完成，继续流程" : detail);
            }
            if (result == WorkResult.Failed)
            {
                var detail = operations.Error;
                return Fail(string.IsNullOrEmpty(detail) ? "自行维修失败，已停止。" : detail);
            }
            if (result != WorkResult.Pending) return Fail("自行维修返回未知状态，已停止。");
            Status = string.IsNullOrEmpty(operations.Status) ? "等待自行维修完成" : operations.Status;
            return WorkResult.Pending;
        }
        catch (Exception exception)
        {
            return Fail("自行维修接口异常：" + exception.Message);
        }
    }

    public void Reset()
    {
        phase = Phase.Inactive;
        expectedJob = expectedTerritory = 0;
        Error = Status = "";
        var cleanupError = ResetOperations();
        if (cleanupError.Length > 0) Error = Status = cleanupError;
    }

    private WorkResult Complete(string status)
    {
        phase = Phase.Complete;
        Error = "";
        Status = status;
        var cleanupError = ResetOperations();
        if (cleanupError.Length == 0) return WorkResult.Complete;
        phase = Phase.Failed;
        Error = Status = cleanupError;
        return WorkResult.Failed;
    }

    private WorkResult Fail(string error)
    {
        phase = Phase.Failed;
        Error = Status = error;
        var cleanupError = ResetOperations();
        if (cleanupError.Length > 0) Error = Status = error + " " + cleanupError;
        return WorkResult.Failed;
    }

    private string ResetOperations()
    {
        try { operations.Reset(); return ""; }
        catch (Exception exception) { return "清理维修操作失败：" + exception.Message; }
    }
}
