using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dreadnought.Core.Audit;

public sealed record AuditEntry(
    long Sequence,
    DateTimeOffset At,
    string Actor,
    string Action,
    string Subject,
    string Detail,
    string PreviousHash,
    string Hash);

/// <summary>
/// Append-only, hash-chained record of every state-changing action.
///
/// Each entry commits to the one before it, so editing or deleting history breaks
/// the chain from that point on and <see cref="Verify"/> reports where.
/// </summary>
public sealed class AuditLog(TimeProvider clock)
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly List<AuditEntry> _entries = [];
    private readonly object _gate = new();

    public AuditEntry Append(string actor, string action, string subject, string detail = "")
    {
        lock (_gate)
        {
            var previous = _entries.Count == 0 ? Genesis : _entries[^1].Hash;
            var sequence = _entries.Count + 1L;
            var at = clock.GetUtcNow();
            var entry = new AuditEntry(
                sequence, at, actor, action, subject, detail, previous,
                ComputeHash(sequence, at, actor, action, subject, detail, previous));
            _entries.Add(entry);
            return entry;
        }
    }

    public IReadOnlyList<AuditEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    /// <summary>Returns null when the chain is intact, else the first bad sequence number.</summary>
    public static long? Verify(IReadOnlyList<AuditEntry> entries)
    {
        var previous = Genesis;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var expected = ComputeHash(e.Sequence, e.At, e.Actor, e.Action, e.Subject, e.Detail, previous);
            if (e.Sequence != i + 1 || e.PreviousHash != previous || e.Hash != expected)
            {
                return i + 1;
            }

            previous = e.Hash;
        }

        return null;
    }

    private static string ComputeHash(
        long sequence, DateTimeOffset at, string actor, string action, string subject, string detail, string previous)
    {
        // Length-prefix every field so ("ab","c") and ("a","bc") can never hash alike.
        var canonical = new StringBuilder();
        foreach (var field in (string[])
                 [
                     sequence.ToString(CultureInfo.InvariantCulture),
                     at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                     actor, action, subject, detail, previous,
                 ])
        {
            canonical.Append(field.Length).Append(':').Append(field);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}
