namespace Brutality.Music;

/// <summary>
/// What other DJ software keeps about a hot cue besides where it is: where it was when this was said, in seconds; its
/// name; its colour as 0xRRGGBB; and where it ends, if it's a hot loop.
/// </summary>
sealed record HotcueDetail(double At, string? Name, uint? Color, double? LoopEnd);
