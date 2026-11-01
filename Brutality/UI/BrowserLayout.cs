namespace Brutality.UI;

/// <summary>
/// How the song list is laid out, to keep between runs: what it's sorted by, the columns' widths, and which are
/// hidden, each column by its <see cref="ListColumn{T}.Id"/>.
/// </summary>
sealed class BrowserLayout
{
    /// <summary>
    /// The column the songs are sorted by; null for the order they come in (a virtual folder's own, say).
    /// </summary>
    public string? SortColumn { get; set; }

    public bool SortDescending { get; set; }

    public Dictionary<string, float> ColumnWidths { get; set; } = [];

    public List<string> HiddenColumns { get; set; } = [];
}
