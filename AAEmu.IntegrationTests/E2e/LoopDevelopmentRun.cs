#nullable enable
using System.Security.Cryptography;
using System.Text.Json;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Invocation-local control for observation-led loop harnesses. It never issues
/// gameplay actions, restores world state, or turns a continued run into acceptance.
/// </summary>
internal sealed class LoopDevelopmentRun
{
    internal const string Schema = "aaemu-loop-development/1";
    internal const int MaximumCheckpointBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _directory;
    private readonly string _temporaryId = Guid.NewGuid().ToString("N");
    private readonly Identity _identity;
    private readonly Source _source;
    private readonly string[] _portions;
    private readonly Predecessor? _predecessor;
    private readonly JsonElement _initialState;
    private Failure? _failure;
    private int _next;
    private bool _finished;

    internal sealed record Identity(string Scenario, string DatabaseId, uint CharacterId, string BotName);
    internal sealed record Source(string Head, string PublishSourceIdentity, string TestAssemblySha256,
        string RuntimeGameSha256, string CanonicalMd5, string RuntimeMd5);
    internal sealed record Failure(string Portion, string Reason, DateTime AtUtc);
    internal sealed record Predecessor(string Path, string Sha256, string RunId);
    internal sealed record Receipt(string Schema, string Mode, string RunId, Identity Identity, Source Source,
        string[] Portions, int NextPortion, string? CurrentPortion, Failure? FirstFailure, Predecessor? Predecessor,
        JsonElement InitialState, JsonElement? DurableState, JsonElement? ContinuationData,
        bool Quiescent, bool DurableVerified, bool Resumable, string Status);

    internal string Mode { get; }
    internal string RunId { get; }
    internal bool IsDevelopment => Mode == "development";
    internal bool IsResumed => _predecessor != null;
    internal string? CurrentPortion => _next < _portions.Length ? _portions[_next] : null;
    internal JsonElement? ResumeData { get; }
    internal string CheckpointPath => Path.Combine(_directory, "loop-checkpoint.json");
    internal string? CheckpointSha256 { get; private set; }

    private LoopDevelopmentRun(string directory, Identity identity, Source source, string[] portions,
        string mode, JsonElement initialState, Receipt? previous, Predecessor? predecessor, string runId)
    {
        _directory = directory;
        _identity = identity;
        _source = source;
        _portions = portions;
        Mode = mode;
        RunId = runId;
        _initialState = initialState.Clone();
        _predecessor = predecessor;
        _next = previous?.NextPortion ?? 0;
        ResumeData = previous?.ContinuationData?.Clone();
        WriteProgress(initial: true);
    }

    /// <summary>
    /// Call after read-only fixture/state admission and BEFORE adoption, grants or
    /// enrollment. Exact state equality is the default; an explicit adapter comparator
    /// may accept bounded unrelated drift without rewriting actual state. Domain
    /// effects must still be reconciled separately before replay can be considered safe.
    /// </summary>
    internal static LoopDevelopmentRun Begin(string directory, Identity identity, Source source,
        string[] portions, string mode, JsonElement actualDurableState,
        string? resumePath = null, string? expectedResumeSha256 = null,
        Func<Receipt, string?>? reconcile = null, string? invocationId = null,
        Func<JsonElement, JsonElement, bool>? stateMatches = null)
    {
        if (mode is not ("development" or "acceptance"))
            throw new InvalidDataException("Explicit development or acceptance mode required");
        if (invocationId != null && !Guid.TryParseExact(invocationId, "N", out _))
            throw new InvalidDataException("Supervisor invocation identity must be a fresh UUID");
        if (identity.CharacterId == 0 || string.IsNullOrWhiteSpace(identity.Scenario)
            || string.IsNullOrWhiteSpace(identity.DatabaseId) || string.IsNullOrWhiteSpace(identity.BotName))
            throw new InvalidDataException("Observed fixture and character identity required");
        if (portions.Length == 0 || portions.Any(string.IsNullOrWhiteSpace)
            || portions.Distinct(StringComparer.Ordinal).Count() != portions.Length)
            throw new InvalidDataException("Unique ordered portion names required");
        if (actualDurableState.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Observed durable state required before mutation");
        if (string.IsNullOrWhiteSpace(source.Head) || source.Head.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(source.PublishSourceIdentity)
            || string.IsNullOrWhiteSpace(source.TestAssemblySha256) || string.IsNullOrWhiteSpace(source.RuntimeGameSha256)
            || string.IsNullOrWhiteSpace(source.CanonicalMd5) || source.CanonicalMd5 != source.RuntimeMd5)
            throw new InvalidDataException("Current source, binary and matching reference-data identities required");
        if (mode == "acceptance" && (resumePath != null || expectedResumeSha256 != null))
            throw new InvalidDataException("Fresh acceptance cannot inherit a development checkpoint");
        if (File.Exists(Path.Combine(directory, "loop-progress.json"))
            || File.Exists(Path.Combine(directory, "loop-checkpoint.json")))
            throw new InvalidDataException("A new invocation directory is required; preserve prior evidence");

        Receipt? previous = null;
        Predecessor? predecessor = null;
        if (resumePath != null)
        {
            if (string.IsNullOrWhiteSpace(expectedResumeSha256) || reconcile == null)
                throw new InvalidDataException("Continuation requires a checkpoint hash and domain reconciliation");
            byte[] bytes;
            using (var input = File.OpenRead(resumePath))
            {
                var length = input.Length;
                if (length > MaximumCheckpointBytes)
                    throw new InvalidDataException("Checkpoint exceeds its byte bound");
                bytes = new byte[(int)length];
                input.ReadExactly(bytes);
                if (input.ReadByte() != -1)
                    throw new InvalidDataException("Checkpoint changed while reading");
            }
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(digest, expectedResumeSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Checkpoint bytes changed; refuse continuation");
            previous = JsonSerializer.Deserialize<Receipt>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Missing checkpoint receipt");
            if (previous.Schema != Schema || previous.Mode != "development" || !previous.Resumable
                || !previous.Quiescent || !previous.DurableVerified || previous.Status != "FAIL"
                || previous.FirstFailure == null || string.IsNullOrWhiteSpace(previous.RunId))
                throw new InvalidDataException("Predecessor is not a settled, resumable failed development attempt");
            if (previous.Identity != identity || previous.Portions == null || previous.Source == null
                || !previous.Portions.SequenceEqual(portions, StringComparer.Ordinal)
                || previous.Source.CanonicalMd5 != source.CanonicalMd5 || previous.Source.RuntimeMd5 != source.RuntimeMd5)
                throw new InvalidDataException("Fixture, character, portion contract or reference data changed");
            if (previous.NextPortion < 0 || previous.NextPortion >= portions.Length
                || previous.CurrentPortion != portions[previous.NextPortion]
                || previous.FirstFailure.Portion != previous.CurrentPortion
                || previous.DurableState is not { ValueKind: JsonValueKind.Object } saved
                || previous.ContinuationData is not { ValueKind: JsonValueKind.Object })
                throw new InvalidDataException("Current durable state or failed-portion boundary is not the checkpoint");
            if (!JsonElement.DeepEquals(saved, actualDurableState)
                && (stateMatches == null || !stateMatches(saved, actualDurableState)))
                throw new InvalidDataException("Current durable state is not compatible with the checkpoint");
            if (previous.RunId == invocationId)
                throw new InvalidDataException("Continuation requires a new invocation identity");
            var refusal = reconcile(previous);
            if (refusal != null)
                throw new InvalidDataException("Unsafe continuation: " + refusal);
            predecessor = new Predecessor(Path.GetFullPath(resumePath), digest, previous.RunId);
        }
        else if (expectedResumeSha256 != null)
        {
            throw new InvalidDataException("Checkpoint hash supplied without a checkpoint");
        }

        Directory.CreateDirectory(directory);
        // Keep exclusive ownership separate from atomic progress replacement.
        // The claim persists even if publishing the first receipt fails.
        using var claim = File.OpenHandle(Path.Combine(directory, "loop-owner.claim"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return new LoopDevelopmentRun(directory, identity, source, (string[])portions.Clone(),
            mode, actualDurableState, previous, predecessor, invocationId ?? Guid.NewGuid().ToString("N"));
    }

    /// <summary>Only call for an observed outcome, never a predicted action effect.</summary>
    internal void Complete(string portion)
    {
        if (_finished || _failure != null)
            throw new InvalidOperationException("Stopped invocation cannot complete another portion");
        if (CurrentPortion != portion)
            throw new InvalidOperationException("Only the current portion can complete: " + CurrentPortion);
        _next++;
        WriteProgress();
    }

    internal void Fail(string reason)
    {
        if (_finished) throw new InvalidOperationException("Invocation already concluded");
        if (_failure != null) return;
        _failure = new Failure(CurrentPortion ?? "finalization", reason, DateTime.UtcNow);
        WriteProgress();
    }

    /// <summary>
    /// Capture after owned cleanup. The adapter proves both quiescence and that
    /// persisted state matches the observed settled state. Unknown means false.
    /// </summary>
    internal Receipt Finish(JsonElement? durableState, JsonElement? continuationData,
        bool quiescent, bool durableVerified, bool nativePassed)
    {
        if (_finished) throw new InvalidOperationException("Invocation already concluded");
        var complete = nativePassed && _failure == null && _next == _portions.Length
            && quiescent && durableVerified && durableState is { ValueKind: JsonValueKind.Object };
        if (!complete && _failure == null) Fail("Native checks, remaining portions or final settlement are unproved");
        var resumable = IsDevelopment && !complete && _next < _portions.Length
            && quiescent && durableVerified
            && durableState is { ValueKind: JsonValueKind.Object }
            && continuationData is { ValueKind: JsonValueKind.Object };
        var receipt = Snapshot(durableState, continuationData, quiescent, durableVerified, resumable,
            complete ? IsDevelopment ? "DEVELOPMENT_PASS" : "PASS" : "FAIL");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
        if (bytes.Length > MaximumCheckpointBytes)
            throw new InvalidDataException("Checkpoint exceeds its byte bound; no resumable claim");
        using (var output = new FileStream(CheckpointPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        CheckpointSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        _finished = true;
        WriteProgress(receipt);
        return receipt;
    }

    private Receipt Snapshot(JsonElement? durableState = null, JsonElement? continuationData = null,
        bool quiescent = false, bool durableVerified = false, bool resumable = false, string? status = null)
        => new(Schema, Mode, RunId, _identity, _source, _portions, _next, CurrentPortion,
            _failure, _predecessor, _initialState, durableState, continuationData, quiescent,
            durableVerified, resumable, status ?? (_failure == null ? "RUNNING" : "FAIL"));

    private void WriteProgress(Receipt? receipt = null, bool initial = false)
    {
        var path = Path.Combine(_directory, "loop-progress.json");
        var temporary = path + "." + _temporaryId + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(receipt ?? Snapshot(), JsonOptions));
            File.Move(temporary, path, overwrite: !initial);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
