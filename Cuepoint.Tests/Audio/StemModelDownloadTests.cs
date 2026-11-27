using System.Net;
using System.Security.Cryptography;
using Cuepoint.Audio.Stems;

namespace Cuepoint.Tests.Audio;

public class StemModelDownloadTests
{
    static readonly Uri Url = new("https://example.test/htdemucs.onnx");

    // Answers every request with the same status and bytes, counting them.
    sealed class Server(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    static byte[] Model() => Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 31)).ToArray();

    static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    static async Task Finish(StemModelDownload download)
    {
        try
        {
            await download.Task!;
        }
        catch (Exception) { }
    }

    [Fact]
    public async Task The_model_is_downloaded_into_place_once_its_hash_is_right()
    {
        using var folder = new TempFolder();
        var path = folder["models", "htdemucs.onnx"];
        var model = Model();
        var server = new Server(HttpStatusCode.OK, model);
        using var download = new StemModelDownload(path, Url, Sha256(model), model.Length, server);

        download.Start();
        Assert.True(download.Downloading || File.Exists(path));
        await Finish(download);

        Assert.Null(download.Failed);
        Assert.Equal(model, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".part"));
        Assert.Equal(1, download.Progress);

        // There now, so not downloaded again.
        download.Start();
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task A_download_with_the_wrong_hash_is_thrown_away_and_not_tried_again_until_asked()
    {
        using var folder = new TempFolder();
        var path = folder["models", "htdemucs.onnx"];
        var model = Model();
        var server = new Server(HttpStatusCode.OK, model);
        using var download = new StemModelDownload(path, Url, new string('0', 64), model.Length, server);

        download.Start();
        await Finish(download);

        Assert.IsType<InvalidDataException>(download.Failed);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".part"));

        download.Start();
        Assert.Equal(1, server.Requests);

        download.ForgetFailure();
        download.Start();
        await Finish(download);
        Assert.Equal(2, server.Requests);
    }

    [Fact]
    public async Task A_download_of_the_wrong_size_fails()
    {
        using var folder = new TempFolder();
        var path = folder["htdemucs.onnx"];
        var model = Model();
        using var download = new StemModelDownload(path, Url, Sha256(model), model.Length + 1, new Server(HttpStatusCode.OK, model));

        download.Start();
        await Finish(download);

        Assert.IsType<InvalidDataException>(download.Failed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_missing_download_fails()
    {
        using var folder = new TempFolder();
        var path = folder["htdemucs.onnx"];
        using var download = new StemModelDownload(path, Url, Sha256([]), 10, new Server(HttpStatusCode.NotFound, []));

        download.Start();
        await Finish(download);

        Assert.IsType<HttpRequestException>(download.Failed);
        Assert.False(File.Exists(path));
    }
}
