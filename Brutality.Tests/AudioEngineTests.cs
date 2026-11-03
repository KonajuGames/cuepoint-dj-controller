namespace Brutality.Tests;

/// <summary>
/// The tests that make an <see cref="Brutality.Audio.AudioEngine"/>, run one at a time: an engine starts and stops
/// SDL's audio for the whole process, so two at once, as test classes otherwise run, bring the test run down.
/// </summary>
[CollectionDefinition(NAME, DisableParallelization = true)]
public class AudioEngineTests
{
    public const string NAME = "Audio engine";
}
