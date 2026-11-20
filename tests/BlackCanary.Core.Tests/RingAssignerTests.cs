using BlackCanary.Core.Rollouts;

namespace BlackCanary.Core.Tests;

public class RingAssignerTests
{
    private static readonly string[] Devices = Enumerable.Range(0, 20_000).Select(i => $"device-{i}").ToArray();

    [Fact]
    public void Assignment_is_deterministic()
    {
        Assert.Equal(
            RingAssigner.Bucket("device-42", "macos-core@3"),
            RingAssigner.Bucket("device-42", "macos-core@3"));
    }

    [Fact]
    public void Ring_sizes_track_the_configured_percentages()
    {
        var plan = RolloutPlan.Default; // 1 / 10 / 50 / 100 cumulative
        var counts = new int[plan.Rings.Count];
        foreach (var device in Devices)
        {
            counts[RingAssigner.RingIndexFor(plan, device, "change@1")]++;
        }

        Assert.InRange(counts[0] / (double)Devices.Length, 0.005, 0.015);
        Assert.InRange(counts[1] / (double)Devices.Length, 0.08, 0.10);
        Assert.InRange(counts[2] / (double)Devices.Length, 0.38, 0.42);
        Assert.InRange(counts[3] / (double)Devices.Length, 0.48, 0.52);
    }

    [Fact]
    public void Widening_never_removes_a_device_that_already_has_the_change()
    {
        var plan = RolloutPlan.Default;

        foreach (var device in Devices.Take(2_000))
        {
            var targetedBefore = false;
            for (var ring = 0; ring < plan.Rings.Count; ring++)
            {
                var targeted = RingAssigner.IsTargeted(plan, ring, device, "change@1");
                Assert.False(targetedBefore && !targeted);
                targetedBefore = targeted;
            }

            Assert.True(targetedBefore, "every device is covered by the final ring");
        }
    }

    [Fact]
    public void Different_changes_pick_different_canaries()
    {
        var plan = RolloutPlan.Default;
        var first = Devices.Where(d => RingAssigner.RingIndexFor(plan, d, "a@1") == 0).ToHashSet();
        var second = Devices.Where(d => RingAssigner.RingIndexFor(plan, d, "b@1") == 0).ToHashSet();

        first.IntersectWith(second);

        // Independent 1% draws overlap on ~0.01% of the fleet; anything near 1% means the salt is ignored.
        Assert.InRange(first.Count, 0, 20);
    }

    [Fact]
    public void Nothing_is_targeted_before_the_rollout_starts()
    {
        Assert.False(RingAssigner.IsTargeted(RolloutPlan.Default, -1, "device-1", "change@1"));
    }
}
