// WhisperRunner logs: integrity-check failure, subprocess non-zero exit, timeout,
// side-file cleanup failure. All exceptional / once-per-transcribe — not a hot path.
#pragma warning disable CA1848 // Use the LoggerMessage delegates
#pragma warning disable CA1873 // Avoid potentially expensive logging argument evaluation

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KusPus.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Whisper;

/// <summary>
/// Subprocess-based implementation of <see cref="ITranscriber"/>. See TECH_SPEC §15.
///
/// Lifecycle per transcribe call:
///   1. validate paths (whisper.exe / model / wav exist)
///   2. on first call, verify SHA-256 of whisper.exe vs the bundled expected hash;
///      cache the result (pass-or-fail) for the rest of the runner's lifetime
///   3. spawn <c>whisper.exe</c> with the §15 argument list
///   4. invoke <see cref="_onProcessStarted"/> so callers (the Coordinator) can
///      assign the process to a Job Object for KILL_ON_JOB_CLOSE containment
///   5. await with timeout; kill the process tree on timeout
///   6. on non-zero exit, log the stderr preview (first + last 4 KB) and return Fail
///   7. read &lt;wav&gt;.txt, trim, delete the side file, return Ok
///
/// End-to-end correctness against a real whisper.exe is a manual smoke test at the
/// Phase 6 milestone. Phase 3 unit tests cover argument formatting, thread-count
/// selection, early-validation, and integrity-mismatch paths.
/// </summary>
public sealed class WhisperRunner : ITranscriber, IDisposable
{
    /// <summary>Default subprocess file name — the Windows payload renames whisper-cli.exe to this.</summary>
    public const string WindowsExeName = "whisper.exe";
    /// <summary>Subprocess file name in the macOS bundle (built from source, upstream name kept).</summary>
    public const string MacExeName = "whisper-cli";

    private const string IntegrityFailureMessage =
        "Whisper binary integrity check failed — please reinstall KusPus.";

    private readonly string _whisperDirectory;
    private readonly string _whisperExePath;
    private readonly string _exeName;
    private readonly string _expectedWhisperSha256;
    private readonly Action<Process>? _onProcessStarted;
    private readonly TimeSpan _timeout;
    private readonly ILogger<WhisperRunner> _logger;

    // Always acquired before reading or writing _integrityChecked / _integrityPassed.
    // No double-checked locking — the perf delta (one semaphore acquire per Transcribe call)
    // is irrelevant at our call rate and the memory model around plain-bool reads outside
    // the lock is more subtle than it's worth.
    private readonly SemaphoreSlim _integrityLock = new(initialCount: 1, maxCount: 1);
    private bool _integrityChecked;
    private bool _integrityPassed;

    public WhisperRunner(
        string whisperDirectory,
        string expectedWhisperSha256,
        Action<Process>? onProcessStarted = null,
        TimeSpan? timeout = null,
        ILogger<WhisperRunner>? logger = null,
        string? exeName = null)
    {
        _whisperDirectory = whisperDirectory ?? throw new ArgumentNullException(nameof(whisperDirectory));
        _exeName = exeName ?? WindowsExeName;
        _whisperExePath = Path.Combine(_whisperDirectory, _exeName);
        _expectedWhisperSha256 = expectedWhisperSha256
            ?? throw new ArgumentNullException(nameof(expectedWhisperSha256));
        _onProcessStarted = onProcessStarted;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
        _logger = logger ?? NullLogger<WhisperRunner>.Instance;
    }

    public async Task<Result<string>> TranscribeAsync(
        string wavPath,
        ResolvedModel model,
        CancellationToken ct = default)
    {
        if (!File.Exists(_whisperExePath))
        {
            return Result.Fail<string>($"{_exeName} not found at {_whisperExePath}");
        }
        if (!File.Exists(model.Path))
        {
            return Result.Fail<string>($"Model file not found at {model.Path}");
        }
        if (!File.Exists(wavPath))
        {
            return Result.Fail<string>($"Audio file not found at {wavPath}");
        }

        var integrity = await EnsureIntegrityAsync(ct).ConfigureAwait(false);
        if (!integrity.Success)
        {
            return Result.Fail<string>(integrity.Error!, integrity.Cause);
        }

        var threads = ChooseThreadCount(model.Descriptor.Id, Environment.ProcessorCount);
        var psi = new ProcessStartInfo
        {
            FileName = _whisperExePath,
            WorkingDirectory = _whisperDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in BuildArgumentList(model.Path, wavPath, threads))
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };
        // Drain stdout (whisper writes progress there, not the transcript).
        process.OutputDataReceived += (_, _) => { };

        try
        {
            if (!process.Start())
            {
                return Result.Fail<string>($"{_exeName} failed to start.");
            }
        }
        catch (Win32Exception ex)
        {
            return Result.Fail<string>($"Failed to launch {_exeName}: {ex.Message}", ex);
        }

        // Run the containment hook (typically Job Object assignment) immediately after
        // Start so a hung/crashed child can't outlive us. If it throws, kill the orphan
        // and fail — leaving an uncontained process behind would defeat the safety net.
        try
        {
            _onProcessStarted?.Invoke(process);
        }
        catch (Exception ex)
        {
            KillProcessTree(process);
            return Result.Fail<string>(
                $"Failed to attach {_exeName} to its containment scope: {ex.Message}", ex);
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = _timeout + EstimateProcessingAllowance(wavPath);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (not caller cancellation).
            KillProcessTree(process);
            _logger.LogWarning("Whisper subprocess timed out after {Seconds}s; killed.", timeout.TotalSeconds);
            return Result.Fail<string>($"{_exeName} timed out after {timeout.TotalSeconds:F0} seconds.");
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            return Result.Fail<string>("Transcription cancelled by caller.");
        }

        if (process.ExitCode != 0)
        {
            var preview = StderrPreview(stderr.ToString());
            _logger.LogWarning(
                "Whisper subprocess exited with code {Code}. stderr preview: {Preview}",
                process.ExitCode, preview);
            return Result.Fail<string>($"{_exeName} exited with code {process.ExitCode}.");
        }

        var sideFile = wavPath + ".txt";
        if (!File.Exists(sideFile))
        {
            return Result.Fail<string>($"{_exeName} did not produce {sideFile}.");
        }

        var transcript = await File.ReadAllTextAsync(sideFile, ct).ConfigureAwait(false);
        try
        {
            File.Delete(sideFile);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Failed to delete side file {Path}.", sideFile);
        }

        return Result.Ok(transcript.Trim());
    }

    public void Dispose()
    {
        _integrityLock.Dispose();
    }

    // ── internal helpers (visible to KusPus.Whisper.Tests via InternalsVisibleTo) ─

    /// <summary>Builds the §15 argument list for whisper-cli.</summary>
    internal static IReadOnlyList<string> BuildArgumentList(string modelPath, string wavPath, int threads) =>
    [
        "-m", modelPath,
        "-f", wavPath,
        "-nt",
        "--output-txt",
        "-l", "en",
        "-t", threads.ToString(CultureInfo.InvariantCulture),
    ];

    /// <summary>
    /// Model-aware thread count per TECH_SPEC §15: tiny capped at 4, others at 8;
    /// always at least 2; <c>ProcessorCount - 1</c> as the natural target.
    /// </summary>
    internal static int ChooseThreadCount(string modelId, int processorCount)
    {
        var cap = modelId.StartsWith("ggml-tiny", StringComparison.Ordinal) ? 4 : 8;
        return Math.Clamp(processorCount - 1, 2, cap);
    }

    // ── private ──────────────────────────────────────────────────────────────

    private async Task<Result<bool>> EnsureIntegrityAsync(CancellationToken ct)
    {
        await _integrityLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_integrityChecked)
            {
                return _integrityPassed
                    ? Result.Ok(true)
                    : Result.Fail<bool>(IntegrityFailureMessage);
            }

            // Dev mode: empty expected SHA means "trust the binary without verification."
            // Phase 12 (installer) populates the SHA from the build manifest; until then
            // this lets developers point at a freshly-built whisper.exe without manually
            // computing its hash. Logged in CLAUDE.md.
            if (string.IsNullOrEmpty(_expectedWhisperSha256))
            {
                _logger.LogWarning(
                    "Whisper subprocess integrity check skipped — no expected SHA configured. " +
                    "Phase 12 release builds will set this from the build manifest.");
                _integrityPassed = true;
                _integrityChecked = true;
                return Result.Ok(true);
            }

            var actual = await ComputeSha256Async(_whisperExePath, ct).ConfigureAwait(false);
            _integrityPassed = string.Equals(
                actual, _expectedWhisperSha256, StringComparison.OrdinalIgnoreCase);
            _integrityChecked = true;

            if (!_integrityPassed)
            {
                _logger.LogWarning(
                    "Whisper subprocess SHA mismatch: expected {Expected}, got {Actual}.",
                    _expectedWhisperSha256, actual);
                return Result.Fail<bool>(IntegrityFailureMessage);
            }

            return Result.Ok(true);
        }
        finally
        {
            _integrityLock.Release();
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string StderrPreview(string stderr)
    {
        const int chunkSize = 4096;
        if (stderr.Length <= chunkSize * 2)
        {
            return stderr;
        }
        return string.Concat(
            stderr.AsSpan(0, chunkSize),
            "\n…\n",
            stderr.AsSpan(stderr.Length - chunkSize));
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Best-effort: process may have exited between HasExited and Kill, or be
            // protected by the OS. There's nothing useful to do beyond stopping here.
        }
    }

    /// <summary>
    /// Extra timeout budget proportional to the take: 2× the audio length, assuming the
    /// recorder's 16 kHz mono 16-bit PCM. A fixed budget alone fails long dictations on
    /// the larger models.
    /// </summary>
    internal static TimeSpan EstimateProcessingAllowance(string wavPath)
    {
        try
        {
            long pcmBytes = Math.Max(0, new FileInfo(wavPath).Length - 44);
            return TimeSpan.FromSeconds(2.0 * pcmBytes / (16_000 * 2));
        }
        catch (IOException)
        {
            return TimeSpan.Zero;
        }
    }
}
