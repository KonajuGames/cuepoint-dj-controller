namespace Brutality.Audio.Analysis;

/// <summary>
/// A tempo change in a <see cref="BeatGrid"/>: a beat at <paramref name="Time"/> seconds, then a beat every 60/Bpm
/// seconds until the next marker, which falls exactly on one of them.
/// </summary>
readonly record struct GridMarker(double Time, double Bpm)
{
    public double BeatSeconds => 60 / Bpm;
}

/// <summary>
/// Where a song's beats fall: one marker for a steady tempo, as nearly all dance music has, or several where the
/// tempo changes or drifts, as with a live drummer. The grid runs back from the first marker at its tempo, and on
/// from the last at its.
/// </summary>
sealed class BeatGrid : IEquatable<BeatGrid>
{
    public IReadOnlyList<GridMarker> Markers { get; }

    /// <summary>
    /// How long the song is, in seconds, which says how much of it the last marker's tempo covers.
    /// </summary>
    public double Duration { get; }

    public BeatGrid(IReadOnlyList<GridMarker> markers, double duration)
    {
        if (markers.Count == 0)
            throw new ArgumentException("A beat grid needs at least one marker", nameof(markers));
        for (var i = 1; i < markers.Count; i++)
        {
            if (markers[i].Time <= markers[i - 1].Time)
                throw new ArgumentException("Beat grid markers must be in time order", nameof(markers));
        }
        Markers = markers.ToArray();
        Duration = duration;
    }

    /// <summary>
    /// A steady tempo, with a beat at <paramref name="beat"/> seconds.
    /// </summary>
    public static BeatGrid Constant(double bpm, double beat, double duration) => new([new GridMarker(beat, bpm)], duration);

    public bool IsVariable => Markers.Count > 1;

    /// <summary>
    /// The tempo at <paramref name="seconds"/> into the song.
    /// </summary>
    public double BpmAt(double seconds) => MarkerAt(seconds).Bpm;

    GridMarker MarkerAt(double seconds)
    {
        var marker = Markers[0];
        foreach (var next in Markers)
        {
            if (next.Time > seconds)
                break;
            marker = next;
        }
        return marker;
    }

    /// <summary>
    /// How many beats <paramref name="seconds"/> is from the first marker, counting through every tempo change:
    /// 2.5 is halfway between the third beat and the fourth. Negative before the first marker. Bars are counted in
    /// fours from the first marker's beat, since where the downbeat falls isn't known.
    /// </summary>
    public double BeatNumberAt(double seconds)
    {
        var beats = 0.0;
        for (var i = 0; i < Markers.Count; i++)
        {
            var marker = Markers[i];
            if (i + 1 == Markers.Count || seconds < Markers[i + 1].Time)
                return beats + (seconds - marker.Time) / marker.BeatSeconds;
            beats += Math.Round((Markers[i + 1].Time - marker.Time) / marker.BeatSeconds);
        }
        return beats;
    }

    /// <summary>
    /// When beat <paramref name="beat"/> falls, counting as <see cref="BeatNumberAt"/> does; the reverse of it.
    /// </summary>
    public double TimeOfBeat(double beat)
    {
        var start = 0.0;
        for (var i = 0; i < Markers.Count; i++)
        {
            var marker = Markers[i];
            var beats = i + 1 < Markers.Count ? Math.Round((Markers[i + 1].Time - marker.Time) / marker.BeatSeconds) : double.MaxValue;
            if (beat < start + beats || i + 1 == Markers.Count)
                return marker.Time + (beat - start) * marker.BeatSeconds;
            start += beats;
        }
        return Markers[^1].Time;
    }

    /// <summary>
    /// The tempo that covers most of the song, for showing as its tempo: for a song that drifts, the middle of its
    /// tempos, weighted by how long each lasts.
    /// </summary>
    public double MainBpm
    {
        get
        {
            if (!IsVariable)
                return Markers[0].Bpm;

            var spans = new List<(double Bpm, double Seconds)>();
            for (var i = 0; i < Markers.Count; i++)
            {
                var start = i == 0 ? 0 : Markers[i].Time;
                var end = i + 1 < Markers.Count ? Markers[i + 1].Time : Math.Max(Duration, Markers[i].Time);
                spans.Add((Markers[i].Bpm, Math.Max(0, end - start)));
            }
            spans.Sort((a, b) => a.Bpm.CompareTo(b.Bpm));
            var half = spans.Sum(span => span.Seconds) / 2;
            var covered = 0.0;
            foreach (var (bpm, seconds) in spans)
            {
                covered += seconds;
                if (covered >= half)
                    return bpm;
            }
            return spans[^1].Bpm;
        }
    }

    /// <summary>
    /// The first beat at or after the start of the song.
    /// </summary>
    public double FirstBeat
    {
        get
        {
            var first = Markers[0];
            return ((first.Time % first.BeatSeconds) + first.BeatSeconds) % first.BeatSeconds;
        }
    }

    /// <summary>
    /// Every beat from the start of the song to its end, in order.
    /// </summary>
    public IEnumerable<double> Beats()
    {
        for (var i = 0; i < Markers.Count; i++)
        {
            var marker = Markers[i];
            var start = i == 0 ? FirstBeat : marker.Time;
            var end = i + 1 < Markers.Count ? Markers[i + 1].Time : Duration;
            // Counting beats from the marker, rather than adding, keeps rounding from creeping in over a long song.
            var from = (int)Math.Round((start - marker.Time) / marker.BeatSeconds);
            for (var n = from; ; n++)
            {
                var beat = marker.Time + n * marker.BeatSeconds;
                // The next marker's own beat is its to give, with a little leeway for rounding.
                if (beat >= end - (i + 1 < Markers.Count ? marker.BeatSeconds / 2 : 0))
                    break;
                yield return beat;
            }
        }
    }

    public bool Equals(BeatGrid? other) =>
        other is not null && Duration == other.Duration && Markers.SequenceEqual(other.Markers);

    public override bool Equals(object? obj) => Equals(obj as BeatGrid);

    public override int GetHashCode() => HashCode.Combine(Markers.Count, Markers[0], Duration);

    public override string ToString() =>
        IsVariable ? $"{MainBpm:F2} BPM, varying over {Markers.Count} sections" : $"{Markers[0].Bpm:F2} BPM";
}
