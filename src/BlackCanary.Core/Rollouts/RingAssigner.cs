using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BlackCanary.Core.Rollouts;

/// <summary>
/// Decides which ring a device belongs to for a given change.
///
/// Assignment is a pure function of (change, device), so every component — control
/// plane, CLI, analytics — computes the same answer without sharing state. Salting
/// by change id means the same unlucky devices are not the canaries every time.
/// </summary>
public static class RingAssigner
{
    private const int Buckets = 10_000;

    /// <summary>Stable bucket in [0, 10000) for this device under this salt.</summary>
    public static int Bucket(string deviceId, string salt)
    {
        var bytes = Encoding.UTF8.GetBytes($"{salt}\n{deviceId}");
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % Buckets);
    }

    /// <summary>Index of the first ring that includes the device.</summary>
    public static int RingIndexFor(RolloutPlan plan, string deviceId, string salt)
    {
        var bucket = Bucket(deviceId, salt);
        for (var i = 0; i < plan.Rings.Count; i++)
        {
            if (bucket < plan.Rings[i].CumulativePercent * (Buckets / 100.0))
            {
                return i;
            }
        }

        return plan.Rings.Count - 1;
    }

    /// <summary>True once the rollout has reached a ring that includes the device.</summary>
    public static bool IsTargeted(RolloutPlan plan, int currentRingIndex, string deviceId, string salt) =>
        currentRingIndex >= 0 && RingIndexFor(plan, deviceId, salt) <= currentRingIndex;
}
