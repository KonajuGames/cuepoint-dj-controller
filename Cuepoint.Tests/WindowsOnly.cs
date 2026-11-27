namespace Cuepoint.Tests;

/// <summary>
/// A test written around Windows (its paths with drive letters and backslashes, or what only Windows reads), skipped
/// elsewhere.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Written around Windows";
    }
}

/// <summary>
/// A theory written around Windows, skipped elsewhere; see <see cref="WindowsFactAttribute"/>.
/// </summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Written around Windows";
    }
}
