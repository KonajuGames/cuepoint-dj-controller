using Brutal.SdlApi;

namespace Brutality.Audio;

/// <summary>
/// Decodes any song a deck can play: SDL_mixer for MP3, WAV, FLAC and OGG, and Windows' Media Foundation for what it
/// can't (AAC in .m4a, chiefly), when on Windows.
/// </summary>
static class AudioDecoder
{
    /// <summary>
    /// Decodes all of <paramref name="path"/>, with any title and artist SDL_mixer finds in its tags.
    /// </summary>
    public static (PcmAudio Audio, string? Title, string? Artist) Decode(string path)
    {
        var collector = new PcmCollector();
        var (title, artist) = Stream(path, collector);
        return (collector.ToPcm(), title, artist);
    }

    /// <summary>
    /// Decodes <paramref name="path"/> into <paramref name="sink"/>. Media Foundation takes over only if SDL_mixer
    /// can't start on the file, so the sink never sees samples from both.
    /// </summary>
    public static (string? Title, string? Artist) Stream(string path, IPcmSink sink)
    {
        var started = new StartWatch(sink);
        try
        {
            return SdlPcmDecoder.Stream(path, started);
        }
        catch (SdlException sdlError) when (!started.HasStarted && OperatingSystem.IsWindows() && File.Exists(path))
        {
            try
            {
                MediaFoundationDecoder.Stream(path, sink);
                return (null, null);
            }
            catch (Exception mfError) when (mfError is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Couldn't decode {System.IO.Path.GetFileName(path)}. SDL_mixer: {sdlError.Message} Media Foundation: {mfError.Message}",
                    mfError
                );
            }
        }
    }

    sealed class StartWatch(IPcmSink inner) : IPcmSink
    {
        public bool HasStarted { get; private set; }

        public void Start(int channels, int sampleRate)
        {
            HasStarted = true;
            inner.Start(channels, sampleRate);
        }

        public void Write(ReadOnlySpan<float> samples, float progress) => inner.Write(samples, progress);
    }
}
