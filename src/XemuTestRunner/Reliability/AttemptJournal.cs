using System.Diagnostics;
using System.Text.Json;
using XemuTestRunner.Config;

namespace XemuTestRunner.Reliability;

public enum RecoveryAction { Hold, Retry, Archive, Exhausted }

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
    public static AttemptRecord? Read(string package)
    {
        var path = Path.Combine(package, FileName);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Attempt journal exceeds 64 KiB.");
        var record = JsonSerializer.Deserialize<AttemptRecord>(File.ReadAllText(path), ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException("Empty attempt journal.");
        if (record.Attempt < 1 || string.IsNullOrWhiteSpace(record.RunId) || !record.RunId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InvalidDataException("Invalid attempt identity.");
        return record;
    }
    public static void Write(string package, AttemptRecord record)
    {
        record.UpdatedUtc = DateTimeOffset.UtcNow;
        AtomicJson.Write(Path.Combine(package, FileName), record);
    }
    public static bool HasFinalResult(string resultsRoot, AttemptRecord record)
    {
        try
        {
            var path = Path.Combine(resultsRoot, record.RunId, "result.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.GetProperty("runId").GetString() != record.RunId ||
                !doc.RootElement.TryGetProperty("status", out var status)) return false;
            // An interrupted result can be durable while its requested retry has
            // not yet been moved to Pending. Preserve that intent across recovery crashes.
            if (status.GetString() == "interrupted" && doc.RootElement.TryGetProperty("retryScheduled", out var retry) &&
                retry.ValueKind == JsonValueKind.True) return false;
            return status.GetString() is not (null or "running" or "starting" or "cleanup_failed");
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
    public static RecoveryAction RecoveryDecision(AttemptRecord? record, bool finalResult, int maxRetries)
    {
        if (finalResult) return RecoveryAction.Archive;
        if (record is null || record.Phase == "starting") return RecoveryAction.Hold;
        if (record.Phase == "running")
        {
            if (record.ProcessId is null || record.ProcessStartedUtc is null) return RecoveryAction.Hold;
            try
            {
                using var process = Process.GetProcessById(record.ProcessId.Value);
                if (!process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - record.ProcessStartedUtc.Value).TotalMilliseconds) < 1000)
                    return RecoveryAction.Hold;
            }
            catch (ArgumentException) { /* PID no longer exists. */ }
            catch (InvalidOperationException) { /* Process exited during inspection. */ }
            catch (System.ComponentModel.Win32Exception) { return RecoveryAction.Hold; }
        }
        else if (record.Phase is not ("preflight" or "exited" or "finalized")) return RecoveryAction.Hold;
        return record.Attempt > maxRetries ? RecoveryAction.Exhausted : RecoveryAction.Retry;
    }
}
