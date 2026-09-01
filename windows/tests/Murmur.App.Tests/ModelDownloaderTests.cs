using System.Net;
using Murmur.Speech;
using Shouldly;
using Xunit;

namespace Murmur.AppTests;

/// <summary>
/// Covers the part of downloading that is not "call HttpClient".
/// </summary>
/// <remarks>
/// A truncated model file is the failure worth a test. It satisfies
/// <c>ParakeetTranscriber.IsComplete</c>, so nothing notices until sherpa-onnx fails with an
/// opaque protobuf parse error the first time the user speaks — hours after the download, and
/// reading like a corrupt build rather than a missing byte range.
/// </remarks>
public sealed class ModelDownloaderTests : IDisposable
{
    // A directory of its own. SpecialFolder.LocalApplicationData resolves through the shell
    // and ignores the environment variable, so the only way to keep a test off a real
    // installation is to pass the path in.
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"murmur-dl-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // A scanner holding a handle is not a test failure.
        }
    }

    [Fact]
    public async Task Every_required_file_is_written()
    {
        using var client = new HttpClient(new StubHandler(name => (Body(name), Body(name).Length)));

        var directory = await ModelDownloader.DownloadAsync(client, _home);

        ParakeetTranscriber.IsComplete(directory).ShouldBeTrue();
        foreach (var name in ParakeetTranscriber.RequiredFiles)
        {
            var written = await File.ReadAllBytesAsync(Path.Combine(directory, name));
            written.Length.ShouldBe(Body(name).Length);
        }
    }

    /// <summary>
    /// The one that matters: a body shorter than its Content-Length must fail loudly and leave
    /// nothing behind, rather than install a file the engine will choke on later.
    /// </summary>
    [Fact]
    public async Task A_body_that_stops_early_fails_and_leaves_no_file()
    {
        // Claims 4096 bytes, sends 10.
        using var client = new HttpClient(new StubHandler(_ => (new byte[10], 4096)));

        await Should.ThrowAsync<HttpRequestException>(() => ModelDownloader.DownloadAsync(client, _home));

        ParakeetTranscriber.IsComplete(_home).ShouldBeFalse();
        Directory.GetFiles(_home).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_already_present_is_not_fetched_again()
    {
        var directory = _home;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "tokens.txt"), "kept");

        var requested = new List<string>();
        using var client = new HttpClient(new StubHandler(name =>
        {
            requested.Add(name);
            return (Body(name), Body(name).Length);
        }));

        await ModelDownloader.DownloadAsync(client, _home);

        requested.ShouldNotContain("tokens.txt");
        (await File.ReadAllTextAsync(Path.Combine(directory, "tokens.txt"))).ShouldBe("kept");
    }

    [Fact]
    public void Progress_is_weighted_by_bytes_not_by_file_count()
    {
        // Halfway through the encoder is roughly halfway through the download, because the
        // encoder is almost all of it. A per-file bar would read 0% here.
        var half = new ModelDownloadProgress(
            "encoder.int8.onnx", 1, 4,
            ModelDownloader.ExpectedSizes[0] / 2, ModelDownloader.ExpectedSizes[0]);

        half.Fraction.ShouldNotBeNull();
        half.Fraction!.Value.ShouldBeInRange(0.4, 0.6);

        // And the last, tiny file must not sit at 25%.
        var last = new ModelDownloadProgress(
            "tokens.txt", 4, 4, 0, ModelDownloader.ExpectedSizes[3]);

        last.Fraction!.Value.ShouldBeGreaterThan(0.98);
    }

    private static byte[] Body(string name) => new byte[name.Length * 8];

    private sealed class StubHandler(Func<string, (byte[] Body, long Length)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = request.RequestUri!.Segments[^1];
            var (body, length) = respond(name);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };

            // Set explicitly so a short body can be described as a long one — which is exactly
            // the truncation case being tested.
            response.Content.Headers.ContentLength = length;
            return Task.FromResult(response);
        }
    }
}
