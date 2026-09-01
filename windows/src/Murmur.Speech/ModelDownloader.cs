using System.Globalization;

namespace Murmur.Speech;

/// <summary>Progress of a model download.</summary>
/// <param name="FileName">The file currently transferring.</param>
/// <param name="FileIndex">Its position in the set, from 1.</param>
/// <param name="FileCount">How many files in total.</param>
/// <param name="BytesReceived">Bytes of this file so far.</param>
/// <param name="BytesTotal">Size of this file, or null if the server did not say.</param>
public readonly record struct ModelDownloadProgress(
    string FileName,
    int FileIndex,
    int FileCount,
    long BytesReceived,
    long? BytesTotal)
{
    /// <summary>Fraction of the whole download, 0…1, or null while a size is unknown.</summary>
    /// <remarks>
    /// Weighted by bytes rather than by file count. The encoder is 622 MB of a 661 MB
    /// download, so a per-file bar would sit at 0% for the entire wait and then jump to 100% —
    /// the exact bar that makes people think an installer has hung.
    /// </remarks>
    public double? Fraction
    {
        get
        {
            if (BytesTotal is null or 0) return null;

            var done = ModelDownloader.ExpectedBytesBefore(FileIndex - 1);
            var whole = ModelDownloader.ExpectedBytesTotal;
            return Math.Clamp((done + BytesReceived * ((double)ExpectedSize / BytesTotal.Value)) / whole, 0, 1);
        }
    }

    private long ExpectedSize => ModelDownloader.ExpectedSizes[FileIndex - 1];

    /// <summary>A line fit to show a person, e.g. "encoder.int8.onnx — 210 MB of 622 MB".</summary>
    public override string ToString() => BytesTotal is > 0
        ? string.Create(CultureInfo.CurrentCulture,
            $"{FileName} — {BytesReceived / 1048576} MB of {BytesTotal / 1048576} MB")
        : string.Create(CultureInfo.CurrentCulture,
            $"{FileName} — {BytesReceived / 1048576} MB");
}

/// <summary>
/// Fetches the Parakeet weights so the app can install its own speech engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the app downloads this rather than shipping it:</b> the weights are 661 MB against
/// a 116 MB application. Carrying them makes every release a 750 MB download that must be
/// re-fetched for a one-line code change, and it puts this build in the business of
/// redistributing someone else's CC-BY-4.0 artefact. Fetching them once, from the publisher,
/// keeps the app small and the licensing simple.
/// </para>
/// <para>
/// <b>Partial files are the failure that matters.</b> A half-downloaded encoder satisfies
/// <see cref="ParakeetTranscriber.IsComplete"/>, and then sherpa-onnx fails with an opaque
/// protobuf parse error that reads like a corrupt build rather than a missing byte range. So
/// every file lands as <c>.part</c> and is renamed only once the last byte is written: an
/// interrupted download leaves no file at all, which is a state the app already understands.
/// </para>
/// </remarks>
public static class ModelDownloader
{
    /// <summary>Where the weights come from.</summary>
    /// <remarks>
    /// The int8 conversion published by csukuangfj, which is what sherpa-onnx's own
    /// documentation points at. English-only (v2); v3 carries 25 languages in a larger
    /// vocabulary at the same speed.
    /// </remarks>
    public const string BaseUrl =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/resolve/main";

    /// <summary>Published sizes, in the order of <see cref="ParakeetTranscriber.RequiredFiles"/>.</summary>
    /// <remarks>
    /// Used only to weight the progress bar before the server reports a length. Being a little
    /// wrong costs a slightly uneven bar, nothing more.
    /// </remarks>
    public static IReadOnlyList<long> ExpectedSizes { get; } =
    [
        652_000_000, // encoder.int8.onnx
        7_300_000,   // decoder.int8.onnx
        1_800_000,   // joiner.int8.onnx
        9_400,       // tokens.txt
    ];

    /// <summary>Expected size of the whole set.</summary>
    public static long ExpectedBytesTotal { get; } = ExpectedSizes.Sum();

    /// <summary>Expected bytes across the first <paramref name="count"/> files.</summary>
    public static long ExpectedBytesBefore(int count)
    {
        long sum = 0;
        for (var i = 0; i < count && i < ExpectedSizes.Count; i++) sum += ExpectedSizes[i];
        return sum;
    }

    /// <summary>Where a downloaded model is installed.</summary>
    /// <remarks>
    /// <c>%LOCALAPPDATA%</c>, never beside the executable: an app in Program Files cannot
    /// write next to itself without administrator rights, and asking for those to download a
    /// public file is not a trade worth making.
    /// </remarks>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Murmur", "models", "parakeet-v2");

    /// <summary>
    /// Downloads every missing file into <see cref="InstallDirectory"/>.
    /// </summary>
    /// <param name="client">
    /// The transport. Supplied by the caller so a test can hand over a fake handler, and so
    /// the app can own the lifetime of a client that lives for one long transfer.
    /// </param>
    /// <param name="directory">
    /// Where to install. Defaults to <see cref="InstallDirectory"/>; a parameter because
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> resolves through the shell
    /// and cannot be redirected, so without this a test would write to the real installation.
    /// </param>
    /// <param name="progress">Reports the current file and its byte count.</param>
    /// <param name="cancellationToken">Abandons the download; part files are cleaned up.</param>
    /// <returns>The directory the model was installed to.</returns>
    /// <exception cref="HttpRequestException">The server refused or the transfer failed.</exception>
    public static async Task<string> DownloadAsync(
        HttpClient client,
        string? directory = null,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        directory ??= InstallDirectory;
        Directory.CreateDirectory(directory);

        var files = ParakeetTranscriber.RequiredFiles;

        for (var i = 0; i < files.Count; i++)
        {
            var name = files[i];
            var final = Path.Combine(directory, name);

            // Already there and whole. Re-downloading 622 MB because one small file failed
            // last time is the kind of thing that makes a person give up on an installer.
            if (File.Exists(final)) continue;

            var part = final + ".part";

            try
            {
                using var response = await client
                    .GetAsync($"{BaseUrl}/{name}", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;

                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var target = File.Create(part))
                {
                    var buffer = new byte[1024 * 128];
                    long received = 0;
                    int read;

                    progress?.Report(new ModelDownloadProgress(name, i + 1, files.Count, 0, total));

                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        received += read;
                        progress?.Report(new ModelDownloadProgress(name, i + 1, files.Count, received, total));
                    }

                    // A truncated body is not an error on the socket — the server can simply
                    // stop. Catching it here is the difference between a clear failure now and
                    // a protobuf parse error the first time the user speaks.
                    if (total is > 0 && received != total)
                    {
                        throw new HttpRequestException(
                            string.Create(CultureInfo.InvariantCulture,
                                $"{name} ended early: {received} of {total} bytes."));
                    }
                }

                File.Move(part, final, overwrite: true);
            }
            catch
            {
                // Never leave a partial file behind under a name the app would trust.
                TryDelete(part);
                throw;
            }
        }

        return directory;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Locked by a scanner. The .part suffix keeps it out of the app's way regardless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
