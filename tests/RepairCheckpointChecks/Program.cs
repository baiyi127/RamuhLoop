using RamuhLoop.Services;

var checks = new (string Name, Action Run)[]
{
    ("repair checkpoint requires explicit begin", RequiresExplicitBegin),
    ("9.99 percent and the closest value below ten percent begin repair once", LowConditionBeginsOnce),
    ("ten percent and healthy equipment complete without beginning repair", HealthyEquipmentSkipsRepair),
    ("unavailable condition fails without beginning repair", UnavailableConditionFails),
    ("unsafe initial contexts never read or begin repair", RejectUnsafeInitialContexts),
    ("battle or loss of readiness cancels active repair before another tick", ReadinessChangeCancelsRepair),
    ("entering a duty cancels active repair before another tick", DutyEntryCancelsRepair),
    ("external queued work cancels active repair before another tick", QueueChangeCancelsRepair),
    ("job change cancels active repair before another tick", JobChangeCancelsRepair),
    ("territory change cancels active repair before another tick", TerritoryChangeCancelsRepair),
    ("completed repair preserves the queue-duty continuation", QueueDutyResume),
    ("completed repair preserves the open-coffer continuation", OpenCofferResume),
    ("skipping unnecessary repair preserves either continuation", HealthyResume),
    ("repair failure preserves its error after native reset clears it", PreserveRepairError),
    ("reset before the first read cannot start work later", ResetBeforeChecking),
    ("stop during repair cannot poll or restart repair later", ResetDuringRepair),
    ("failed checkpoint stays failed even after context recovers", FailureDoesNotRestart),
    ("an explicit new checkpoint can recover after an earlier failure", ExplicitRestart),
    ("beginning a new job checkpoint cancels the earlier repair", NewCheckpointReplacesOld),
    ("completed checkpoint does not read or repair again", CompletionIsStable),
    ("context changes remain guarded even after repair completes", CompletionStillChecksContext),
    ("repair operation exceptions fail and clean up", OperationExceptionsFail),
};
var passed = 0;
foreach (var check in checks)
{
    try { check.Run(); Console.WriteLine("PASS " + check.Name); passed++; }
    catch (Exception exception) { Console.Error.WriteLine("FAIL " + check.Name + ": " + exception.Message); }
}
Console.WriteLine($"{passed}/{checks.Length} repair checkpoint checks passed.");
return passed == checks.Length ? 0 : 1;

static RepairCheckpointContext Ready(uint job = 24) => new(true, false, true, job, 128);
static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static RepairCheckpoint LowCheckpoint(FakeRepair operations, RepairResume resume = RepairResume.QueueDuty, uint job = 24)
{
    operations.Condition = 2997;
    var checkpoint = new RepairCheckpoint(operations);
    checkpoint.Begin(job, 128, resume);
    Expect(checkpoint.Tick(Ready(job)) == WorkResult.Pending, "first low-condition tick should begin repair and wait");
    Expect(operations.Begins == 1 && operations.Ticks == 0, "do not poll repair in the same tick that begins it");
    return checkpoint;
}

static void RequiresExplicitBegin()
{
    var operations = new FakeRepair();
    var checkpoint = new RepairCheckpoint(operations);
    Expect(checkpoint.Tick(Ready()) == WorkResult.Failed && operations.NoCalls, "unbegun checkpoint must be inactive and non-actionable");
}

static void LowConditionBeginsOnce()
{
    foreach (var condition in new[] { 0, 2997, 2999 })
    {
        var operations = new FakeRepair { Condition = condition };
        var checkpoint = new RepairCheckpoint(operations);
        checkpoint.Begin(24, 128, RepairResume.QueueDuty);
        Expect(checkpoint.Tick(Ready()) == WorkResult.Pending, "strictly below ten percent should trigger repair");
        Expect(operations.Reads == 1 && operations.Begins == 1 && operations.Ticks == 0, "first tick only reads and begins once");
        checkpoint.Tick(Ready()); checkpoint.Tick(Ready());
        Expect(operations.Begins == 1 && operations.Reads == 1 && operations.Ticks == 2, "pending repair must not restart or replace its baseline");
    }
}

static void HealthyEquipmentSkipsRepair()
{
    foreach (var condition in new[] { 3000, 30000, int.MaxValue })
    {
        var operations = new FakeRepair { Condition = condition };
        var checkpoint = new RepairCheckpoint(operations);
        checkpoint.Begin(24, 128, RepairResume.QueueDuty);
        var resets = operations.Resets;
        Expect(checkpoint.Tick(Ready()) == WorkResult.Complete, "ten percent, healthy and no-equipment states can continue");
        Expect(operations.Begins == 0 && operations.Ticks == 0 && operations.Resets > resets, "unnecessary repair must complete without a repair action and clean up");
    }
}

static void UnavailableConditionFails()
{
    var operations = new FakeRepair { Condition = -1 };
    var checkpoint = new RepairCheckpoint(operations); checkpoint.Begin(24, 128, RepairResume.QueueDuty);
    var resets = operations.Resets;
    Expect(checkpoint.Tick(Ready()) == WorkResult.Failed, "unavailable condition must fail closed");
    Expect(operations.Reads == 1 && operations.Begins == 0 && operations.Ticks == 0 && operations.Resets > resets, "do not attempt repair with unknown equipment state");
}

static void RejectUnsafeInitialContexts()
{
    var contexts = new[]
    {
        Ready() with { Ready = false }, Ready() with { InDuty = true }, Ready() with { QueueEmpty = false },
        Ready() with { CurrentJob = 19 }, Ready() with { Territory = 129 },
    };
    foreach (var context in contexts)
    {
        var operations = new FakeRepair { Condition = 2997 };
        var checkpoint = new RepairCheckpoint(operations); checkpoint.Begin(24, 128, RepairResume.QueueDuty);
        var resets = operations.Resets;
        Expect(checkpoint.Tick(context) == WorkResult.Failed, "unsafe initial context must fail");
        Expect(operations.Reads == 0 && operations.Begins == 0 && operations.Ticks == 0 && operations.Resets > resets, "validate actor, duty, queue, job and territory before any repair work");
    }
}

static void CheckActiveContextChange(RepairCheckpointContext unsafeContext)
{
    var operations = new FakeRepair(); var checkpoint = LowCheckpoint(operations);
    var resets = operations.Resets;
    Expect(checkpoint.Tick(unsafeContext) == WorkResult.Failed, "unsafe context must interrupt active repair");
    Expect(operations.Resets > resets && operations.Ticks == 0, "clean up before polling a repair action in an unsafe context");
}
static void ReadinessChangeCancelsRepair() => CheckActiveContextChange(Ready() with { Ready = false });
static void DutyEntryCancelsRepair() => CheckActiveContextChange(Ready() with { InDuty = true });
static void QueueChangeCancelsRepair() => CheckActiveContextChange(Ready() with { QueueEmpty = false });
static void JobChangeCancelsRepair() => CheckActiveContextChange(Ready() with { CurrentJob = 19 });
static void TerritoryChangeCancelsRepair() => CheckActiveContextChange(Ready() with { Territory = 129 });

static void CheckSuccessfulResume(RepairResume continuation, uint job)
{
    var operations = new FakeRepair(); var checkpoint = LowCheckpoint(operations, continuation, job);
    Expect(checkpoint.Tick(Ready(job)) == WorkResult.Pending, "repair is still in progress");
    var resets = operations.Resets;
    operations.Result = WorkResult.Complete;
    Expect(checkpoint.Tick(Ready(job)) == WorkResult.Complete && operations.Resets > resets, "completed native repair must be reset");
    Expect(checkpoint.Resume == continuation && checkpoint.Error.Length == 0, "keep the requested post-repair destination");
}
static void QueueDutyResume() => CheckSuccessfulResume(RepairResume.QueueDuty, 24);
static void OpenCofferResume() => CheckSuccessfulResume(RepairResume.OpenCoffer, 19);

static void HealthyResume()
{
    foreach (var continuation in new[] { RepairResume.QueueDuty, RepairResume.OpenCoffer })
    {
        var operations = new FakeRepair(); var checkpoint = new RepairCheckpoint(operations);
        checkpoint.Begin(24, 128, continuation);
        Expect(checkpoint.Tick(Ready()) == WorkResult.Complete && checkpoint.Resume == continuation, "healthy gear must retain either destination without repair");
    }
}

static void PreserveRepairError()
{
    var operations = new FakeRepair(); var checkpoint = LowCheckpoint(operations);
    operations.Result = WorkResult.Failed;
    Expect(checkpoint.Tick(Ready()) == WorkResult.Failed, "native failure must fail the checkpoint");
    Expect(operations.Error == "" && checkpoint.Error.Contains("native repair failure", StringComparison.Ordinal), "copy native error before Reset clears it");
}

static void ResetBeforeChecking()
{
    var operations = new FakeRepair { Condition = 2997 }; var checkpoint = new RepairCheckpoint(operations);
    checkpoint.Begin(24, 128, RepairResume.QueueDuty); checkpoint.Reset();
    var resets = operations.Resets;
    Expect(checkpoint.Tick(Ready()) == WorkResult.Failed && checkpoint.Tick(Ready()) == WorkResult.Failed, "canceled checkpoint remains canceled");
    Expect(operations.Reads == 0 && operations.Begins == 0 && operations.Ticks == 0 && operations.Resets == resets, "ticks after reset must not touch repair operations");
}

static void ResetDuringRepair()
{
    var operations = new FakeRepair(); var checkpoint = LowCheckpoint(operations);
    checkpoint.Reset(); var counts = operations.Counts;
    operations.Result = WorkResult.Complete;
    Expect(checkpoint.Tick(Ready()) == WorkResult.Failed, "late native completion cannot reactivate a stopped checkpoint");
    Expect(operations.Counts == counts, "stop must prevent every later repair read, begin, tick or reset");
}

static void FailureDoesNotRestart()
{
    var operations = new FakeRepair(); var checkpoint = LowCheckpoint(operations);
    checkpoint.Tick(Ready() with { InDuty = true }); var counts = operations.Counts;
    operations.Result = WorkResult.Complete;
    Expect(checkpoint.Tick(Ready()) == WorkResult.Failed && operations.Counts == counts, "recovered context requires a new explicit Begin");
}

static void ExplicitRestart()
{
    var operations = new FakeRepair(); var checkpoint = LowCheckpoint(operations);
    checkpoint.Tick(Ready() with { InDuty = true });
    operations.Condition = 30000;
    checkpoint.Begin(19, 128, RepairResume.OpenCoffer);
    Expect(checkpoint.Tick(Ready(19)) == WorkResult.Complete && checkpoint.Error == "" && checkpoint.Resume == RepairResume.OpenCoffer, "explicitly starting a new valid checkpoint can recover");
}

static void NewCheckpointReplacesOld()
{
    var operations = new FakeRepair(); var checkpoint = LowCheckpoint(operations);
    var resets = operations.Resets;
    checkpoint.Begin(19, 128, RepairResume.OpenCoffer);
    Expect(operations.Resets > resets && operations.Begins == 1, "new checkpoint cancels the previous operation without starting work before validation");
    Expect(checkpoint.Tick(Ready(19)) == WorkResult.Pending && operations.Begins == 2 && checkpoint.Resume == RepairResume.OpenCoffer, "new validated job gets exactly one new repair operation");
}

static void CompletionIsStable()
{
    var operations = new FakeRepair(); var checkpoint = new RepairCheckpoint(operations);
    checkpoint.Begin(24, 128, RepairResume.QueueDuty); checkpoint.Tick(Ready());
    var counts = operations.Counts;
    Expect(checkpoint.Tick(Ready()) == WorkResult.Complete && operations.Counts == counts, "repeated completion reads cannot repeat repair operations");
}

static void CompletionStillChecksContext()
{
    var operations = new FakeRepair(); var checkpoint = new RepairCheckpoint(operations);
    checkpoint.Begin(24, 128, RepairResume.QueueDuty); checkpoint.Tick(Ready());
    Expect(checkpoint.Tick(Ready() with { QueueEmpty = false }) == WorkResult.Failed, "do not authorize continuation after external queued work changes the checkpoint");
}

static void OperationExceptionsFail()
{
    foreach (var phase in new[] { "read", "begin", "tick" })
    {
        var operations = new FakeRepair { Condition = 2997, ThrowAt = phase };
        var checkpoint = new RepairCheckpoint(operations); checkpoint.Begin(24, 128, RepairResume.QueueDuty);
        var result = checkpoint.Tick(Ready());
        if (phase == "tick") result = checkpoint.Tick(Ready());
        Expect(result == WorkResult.Failed && checkpoint.Error.Contains("test exception", StringComparison.Ordinal), "native operation exceptions must become visible failures");
        var counts = operations.Counts;
        Expect(checkpoint.Tick(Ready()) == WorkResult.Failed && operations.Counts == counts, "exception failure must not restart repair");
    }
}

sealed class FakeRepair : IEquipmentRepairOperations
{
    public int Condition = 30000;
    public WorkResult Result = WorkResult.Pending;
    public string ThrowAt = "";
    public int Reads, Begins, Ticks, Resets;
    public string Error { get; private set; } = "";
    public string Status { get; private set; } = "";
    public (int Reads, int Begins, int Ticks, int Resets) Counts => (Reads, Begins, Ticks, Resets);
    public bool NoCalls => Counts == (0, 0, 0, 0);
    public int ReadMinimumCondition() { Reads++; ThrowIf("read"); return Condition; }
    public void Begin() { Begins++; ThrowIf("begin"); Status = "native repair pending"; }
    public WorkResult Tick()
    {
        Ticks++; ThrowIf("tick");
        Error = Result == WorkResult.Failed ? "native repair failure" : "";
        Status = Result == WorkResult.Complete ? "native repair complete" : "native repair pending";
        return Result;
    }
    public void Reset() { Resets++; Error = Status = ""; }
    private void ThrowIf(string phase) { if (ThrowAt == phase) throw new InvalidOperationException("test exception " + phase); }
}
