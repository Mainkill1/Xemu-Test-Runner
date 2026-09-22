using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using XemuTestRunner.Config;

namespace XemuTestRunner.Reliability;

public enum RecoveryAction
{
    Hold,
    Retry,
    Archive,
    Exhausted
}

public sealed class AttemptRecord
{
    public string RunId { get; set; } = "";
    public int Attempt { get; set; }
    public string Phase { get; set; } = "preflight";
    public int? ProcessId { get; set; }
    public DateTime? ProcessStartedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public static class AttemptJournal
{
    public const string FileName = ".runner-attempt.json";
    private const int MaxJournalBytes = 64 * 1024;
    private const int MaxResultBytes = 1024 * 1024;

    public static AttemptRecord? Read(string package)
    {
        var path = Path.Combine(package, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        if (new FileInfo(path).Length > MaxJournalBytes)
        {
            throw new InvalidDataException("Attempt journal exceeds 64 KiB.");
        }

        var record = JsonSerializer.Deserialize<AttemptRecord>(
            File.ReadAllText(path), ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException("Empty attempt journal.");

        if (record.Attempt < 1 || !IsValidRunId(record.RunId))
        {
            throw new InvalidDataException("Invalid attempt identity.");
        }

        return record;
    }

    public static void Write(string package, AttemptRecord record)
    {
        record.UpdatedUtc = DateTimeOffset.UtcNow;
        AtomicJson.Write(Path.Combine(package, FileName), record);
    }

    public static bool HasFinalResult(string resultsRoot, AttemptRecord record)
    {
        if (!IsValidRunId(record.RunId))
        {
            return false;
        }

        try
        {
            var path = Path.Combine(resultsRoot, record.RunId, "result.json");
            if (!File.Exists(path) || new FileInfo(path).Length > MaxResultBytes)
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var result = document.RootElement;
            if (!result.TryGetProperty("runId", out var runId) ||
                runId.ValueKind != JsonValueKind.String ||
                runId.GetString() != record.RunId ||
                !result.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            // Publishing an interrupted result must not erase the retry that
            // recovery still needs to move back to Pending.
            if (status.GetString() == "interrupted" &&
                result.TryGetProperty("retryScheduled", out var retry) &&
                retry.ValueKind == JsonValueKind.True)
            {
                return false;
            }

            return status.GetString() is not
                (null or "running" or "starting" or "cleanup_failed");
        }
        catch (Exception exception) when (exception is IOException or JsonException or
                                         InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static RecoveryAction RecoveryDecision(
        AttemptRecord? record, bool finalResult, int maxRetries)
    {
        // A hold is an ownership decision, not an incomplete result. Neither a
        // result file nor exhausting the retry count is permission to clear it.
        if (record is null || record.Phase is "starting" or "held")
        {
            return RecoveryAction.Hold;
        }

        if (record.Phase is not ("preflight" or "running" or "exited" or "finalized"))
        {
            return RecoveryAction.Hold;
        }

        if (MayStillOwnProcess(record))
        {
            return RecoveryAction.Hold;
        }

        if (finalResult)
        {
            return RecoveryAction.Archive;
        }

        return record.Attempt > maxRetries
            ? RecoveryAction.Exhausted
            : RecoveryAction.Retry;
    }

    private static bool MayStillOwnProcess(AttemptRecord record)
    {
        if (record.ProcessId is null && record.ProcessStartedUtc is null)
        {
            return record.Phase == "running";
        }

        // Partial identity cannot distinguish a stale PID from our child.
        if (record.ProcessId is null || record.ProcessStartedUtc is null)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(record.ProcessId.Value);
            if (process.HasExited)
            {
                return false;
            }

            var difference = process.StartTime.ToUniversalTime() - record.ProcessStartedUtc.Value;
            return Math.Abs(difference.TotalMilliseconds) < 1000;
        }
        catch (ArgumentException)
        {
            return false; // PID no longer exists.
        }
        catch (InvalidOperationException)
        {
            return false; // Process exited during inspection.
        }
        catch (Win32Exception)
        {
            return true; // Failure to inspect is not permission to release ownership.
        }
    }

    private static bool IsValidRunId(string? runId) =>
        !string.IsNullOrWhiteSpace(runId) &&
        runId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
