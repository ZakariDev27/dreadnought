using BlackCanary.Core.Audit;

namespace BlackCanary.Core.Tests;

public class AuditLogTests
{
    private static AuditLog Populated()
    {
        var log = new AuditLog(new ManualClock(TestData.T0));
        log.Append("ada", "rollout.created", "r-1", "macos-core@3");
        log.Append("grace", "rollout.approved", "r-1", "1/1 approvals");
        log.Append("ada", "rollout.started", "r-1", "ring=canary");
        return log;
    }

    [Fact]
    public void Entries_chain_from_genesis()
    {
        var entries = Populated().Snapshot();

        Assert.Equal(AuditLog.Genesis, entries[0].PreviousHash);
        Assert.Equal(entries[0].Hash, entries[1].PreviousHash);
        Assert.Equal(new[] { 1L, 2L, 3L }, entries.Select(e => e.Sequence).ToArray());
        Assert.Null(AuditLog.Verify(entries));
    }

    [Fact]
    public void Editing_an_entry_is_detected_at_that_entry()
    {
        var entries = Populated().Snapshot().ToList();
        entries[1] = entries[1] with { Actor = "mallory" };

        Assert.Equal(2, AuditLog.Verify(entries));
    }

    [Fact]
    public void Deleting_an_entry_is_detected()
    {
        var entries = Populated().Snapshot().ToList();
        entries.RemoveAt(1);

        Assert.Equal(2, AuditLog.Verify(entries));
    }

    [Fact]
    public void Field_boundaries_are_part_of_the_hash()
    {
        var clock = new ManualClock(TestData.T0);
        var a = new AuditLog(clock).Append("ab", "c", "s");
        var b = new AuditLog(clock).Append("a", "bc", "s");

        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Concurrent_appends_keep_the_chain_intact()
    {
        var log = new AuditLog(new ManualClock(TestData.T0));

        Parallel.For(0, 500, i => log.Append("actor", "action", $"subject-{i}"));

        var entries = log.Snapshot();
        Assert.Equal(500, entries.Count);
        Assert.Null(AuditLog.Verify(entries));
    }
}
