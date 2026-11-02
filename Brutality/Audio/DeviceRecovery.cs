namespace Brutality.Audio;

/// <summary>
/// What to do about the main output: nothing, move to the system's default because the chosen device has gone, go
/// back to the chosen device now it's here again, or open the default afresh because it's stopped or nothing's open.
/// </summary>
enum OutputStep
{
    None,
    FallBack,
    Return,
    ReopenDefault,
}

/// <summary>
/// What to do about headphones on a sound card of their own: nothing, close them because it's gone, or open them on
/// it again now it's back.
/// </summary>
enum HeadphoneStep
{
    None,
    Drop,
    Return,
}

/// <summary>
/// Decides what to do about sound cards coming and going mid-set, from how things stand, for
/// <see cref="AudioEngine.CheckDevices"/> to carry out. Kept apart from SDL so it can be tested without unplugging
/// anything. The rule is to keep the music playing somewhere: a chosen device that goes leaves the main mix on the
/// system's default, and when it's back, everything goes back to it.
/// </summary>
static class DeviceRecovery
{
    /// <param name="chosen">The device chosen for the main mix; null for the system's default.</param>
    /// <param name="open">Whether the main mix is open on any device.</param>
    /// <param name="current">The device it's open on; null for the default.</param>
    /// <param name="stalled">Whether the device it's open on has stopped asking for audio.</param>
    /// <param name="chosenPresent">Whether the chosen device is plugged in; the default always is.</param>
    /// <param name="mayRetry">Whether it's long enough since the chosen device last went or wouldn't open to try it
    /// again.</param>
    public static OutputStep ForOutput(string? chosen, bool open, string? current, bool stalled, bool chosenPresent, bool mayRetry)
    {
        var canReturn = chosen is not null && chosenPresent && mayRetry;
        if (!open)
            return canReturn ? OutputStep.Return : OutputStep.ReopenDefault;
        if (current == chosen)
            return !stalled && chosenPresent ? OutputStep.None
                : chosen is null ? OutputStep.ReopenDefault
                : OutputStep.FallBack;
        // On the default in place of the chosen device.
        return canReturn ? OutputStep.Return
            : stalled ? OutputStep.ReopenDefault
            : OutputStep.None;
    }

    /// <param name="chosen">The device chosen for the headphones; null for none.</param>
    /// <param name="chosenOutput">The device chosen for the main mix: headphones chosen on it are on its channels 3
    /// and 4, and come and go with it.</param>
    /// <param name="current">The device of their own they're open on, if they are.</param>
    /// <param name="chosenPresent">Whether the chosen device is plugged in.</param>
    /// <param name="mayRetry">Whether it's long enough since it last wouldn't open to try it again.</param>
    public static HeadphoneStep ForHeadphones(string? chosen, string? chosenOutput, string? current, bool chosenPresent, bool mayRetry)
    {
        if (chosen is null || chosen == chosenOutput)
            return HeadphoneStep.None;
        if (current == chosen)
            return chosenPresent ? HeadphoneStep.None : HeadphoneStep.Drop;
        return chosenPresent && mayRetry ? HeadphoneStep.Return : HeadphoneStep.None;
    }
}
