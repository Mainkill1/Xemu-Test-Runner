using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

internal sealed partial class AgentJobStore
{
    // Reuse is a verified copy into a NEW draft, not a hard link and not a
    // mutable cache shared with another attempt. The normal submit still hashes
    // and validates the complete materialized package before queue publication.
    public AgentOperation StartReuse(string destinationId, string sourceId, CancellationToken lifetime)
    {
        if (destinationId == sourceId) throw new InvalidDataException("Reuse requires a separate destination job.");
        var destinationDocument = ReadDocument(destinationId);
        var previous = GetOperation(destinationId);
        if (Locate(destinationId).State is "queued" or "testing" or "tested")
            return previous ?? throw Conflict("job_already_started", "The destination was already submitted.", "Observe the existing attempt, not another submission.");
        if (previous?.Action == "reuse" && previous.SourceJobId == sourceId && previous.State is ("queued" or "running" or "completed"))
            return previous;
        var sourceLease = Reserve(sourceId);
        IDisposable? destinationLease = null;
        try
        {
            var sourceDocument = ReadDocument(sourceId);
            var source = Locate(sourceId);
            RequireStableSource(source.State);
            destinationLease = Reserve(destinationId);
            var destination = RequireDraft(destinationId);
            var sourceFiles = sourceDocument.Request.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var matches = destinationDocument.Request.Files.Where(file => sourceFiles.TryGetValue(file.Path, out var original) &&
                original.Length == file.Length && original.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
            return StartOperation(destinationId, "reuse", sourceId, new CombinedReservation(sourceLease, destinationLease), async (operation, ct) =>
            {
                var copied = 0;
                long reusedBytes = 0;
                foreach (var file in matches)
                {
                    ct.ThrowIfCancellationRequested();
                    var original = ResolveFile(source.Package, file.Path);
                    var status = _uploads.GetStatus(original);
                    if (!status.Complete || status.Partial || status.Length != file.Length)
                        throw new InvalidDataException($"Source payload is incomplete: {file.Path}.");
                    await using var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read,
                        _bufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length != file.Length) throw new InvalidDataException($"Source payload length changed: {file.Path}.");
                    var target = ResolveFile(destination, file.Path);
                    await _uploads.ReceiveAsync(input, target, file.Length, null, file.Sha256, null, _bufferBytes, ct).ConfigureAwait(false);
                    if (OperatingSystem.IsLinux() && file.Executable)
                        File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute);
                    copied++;
                    reusedBytes = checked(reusedBytes + file.Length);
                    AtomicJson.Write(OperationPath(destinationId), operation with { State = "running", FilesChecked = copied });
                }
                return new
                {
                    job = Url(destinationId), reusedFiles = copied, reusedBytes,
                    remainingFiles = destinationDocument.Request.Files.Count - copied,
                    next = Url(destinationId) + "/files"
                };
            }, lifetime);
        }
        catch
        {
            destinationLease?.Dispose();
            sourceLease.Dispose();
            throw;
        }
    }
}
