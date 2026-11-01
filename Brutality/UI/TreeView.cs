using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// A tree of items, one per row, indented under their parents, with one selected. Click a row to select it, its
/// arrow (or double-click it) to open or close it; with focus, Up and Down move, Right opens or steps in, Left closes
/// or steps out, and Enter opens or closes. Children are asked for only when a row is opened, so a tree of a whole
/// drive's folders costs nothing until it's explored. Put it in a <see cref="ScrollPanel"/> and set
/// <see cref="ScrollOwner"/>, as with <see cref="ListView{T}"/>.
/// </summary>
sealed class TreeView<T> : Element
    where T : class
{
    const long DOUBLE_CLICK_MS = 500;

    public Font Font { get; set; }

    /// <summary>
    /// What a row says.
    /// </summary>
    public required Func<T, string> Text { get; init; }

    /// <summary>
    /// An item's children; only asked for when it's opened.
    /// </summary>
    public required Func<T, IReadOnlyList<T>> Children { get; init; }

    /// <summary>
    /// Whether an item might have children, so gets an arrow. Asked every frame, so it shouldn't do any work.
    /// </summary>
    public required Func<T, bool> CanExpand { get; init; }

    /// <summary>
    /// The colour of a small square drawn before an item's text, saying what kind of thing it is; none if null.
    /// </summary>
    public Func<T, float4?>? IconColor { get; init; }

    public ScrollPanel? ScrollOwner { get; set; }

    public float Indent { get; set; } = 18;

    /// <summary>
    /// The width given to the arrow, and to the icon, before the text.
    /// </summary>
    const float CELL = 17;
    public float MinWidth { get; set; }

    public float4 TextColor { get; set; } = Colors.White;
    public float4 ArrowColor { get; set; } = Colors.Hex(0x9A9AA8);

    /// <summary>
    /// The arrows by an item that can be opened, pointing right while it's closed and down while it's open, tinted
    /// <see cref="ArrowColor"/>; none are drawn if not given.
    /// </summary>
    public (Image Collapsed, Image Expanded)? Arrows { get; set; }
    public float4 HoverRow { get; set; } = Colors.Hex(0xFFFFFF, 0.07f);
    public float4 SelectedRow { get; set; } = Colors.Hex(0x3D6FB8, 0.7f);
    public float4 SelectedRowUnfocused { get; set; } = Colors.Hex(0x3D6FB8, 0.35f);

    public float4 DropColor { get; set; } = Colors.Hex(0xEB7828);

    /// <summary>
    /// The selection changed, by mouse or keyboard.
    /// </summary>
    public event Action<T>? SelectionChanged;

    /// <summary>
    /// A row was right-clicked, at a point, after being selected: for a menu of what can be done with it.
    /// </summary>
    public event Action<T, float2>? ItemSecondaryClicked;

    /// <summary>
    /// Whether an item would take something dragged onto its row; nothing can be dropped if null.
    /// </summary>
    public Func<T, object, bool>? AcceptsDrop { get; set; }

    /// <summary>
    /// Something was dropped on an item's row, which said it would take it.
    /// </summary>
    public event Action<T, object>? ItemDropped;

    readonly HashSet<T> _expanded = [];
    IReadOnlyList<T> _roots = [];
    List<(T Item, int Depth)> _rows = [];
    bool _rowsStale = true;
    T? _selected;
    int _hovered = -1;
    int _dropRow = -1;
    long _lastClickTime;
    T? _lastClicked;

    public TreeView(Font font) => Font = font;

    public float RowHeight => Font.LineHeight + 6;

    public IReadOnlyList<T> Roots
    {
        get => _roots;
        set
        {
            _roots = value;
            _rowsStale = true;
        }
    }

    public T? SelectedItem => _selected;

    public override bool IsInteractive => true;
    public override bool IsFocusable => true;
    public override Cursor Cursor => Cursor.Arrow;

    public bool IsExpanded(T item) => _expanded.Contains(item);

    /// <summary>
    /// Opens or closes <paramref name="item"/>. Closing one that holds the selection moves the selection up to it.
    /// </summary>
    public void SetExpanded(T item, bool expanded)
    {
        // Whether the selection is inside has to be asked before closing, while its row is still there.
        var selectionInside = !expanded && _selected is not null && IsUnder(_selected, item);
        if (expanded ? !_expanded.Add(item) : !_expanded.Remove(item))
            return;
        _rowsStale = true;
        if (selectionInside)
            Select(item);
    }

    /// <summary>
    /// Selects <paramref name="item"/>, which should be showing, and raises <see cref="SelectionChanged"/>.
    /// </summary>
    public void Select(T item)
    {
        if (ReferenceEquals(item, _selected))
            return;
        _selected = item;
        SelectionChanged?.Invoke(item);
    }

    /// <summary>
    /// Asks for the rows afresh, after items' children have changed. A selected item that's gone stays the selection
    /// in name only, so select another if it matters.
    /// </summary>
    public void Refresh() => _rowsStale = true;

    /// <summary>
    /// Scrolls <paramref name="item"/> into view, if it's showing, without selecting it.
    /// </summary>
    public void ScrollIntoView(T item)
    {
        var row = IndexOf(item);
        if (row >= 0)
            ScrollOwner?.ScrollToShow(row * RowHeight, (row + 1) * RowHeight);
    }

    /// <summary>
    /// The rows showing now: every root, and the children of every open item, depth first.
    /// </summary>
    public IReadOnlyList<(T Item, int Depth)> Rows
    {
        get
        {
            if (_rowsStale)
            {
                _rows = [];
                foreach (var root in _roots)
                    AddRows(root, 0);
                _rowsStale = false;
            }
            return _rows;
        }
    }

    void AddRows(T item, int depth)
    {
        _rows.Add((item, depth));
        if (_expanded.Contains(item))
        {
            foreach (var child in Children(item))
                AddRows(child, depth + 1);
        }
    }

    bool IsUnder(T item, T ancestor)
    {
        var rows = Rows;
        var at = IndexOf(ancestor);
        if (at < 0)
            return false;
        for (var i = at + 1; i < rows.Count && rows[i].Depth > rows[at].Depth; i++)
        {
            if (ReferenceEquals(rows[i].Item, item))
                return true;
        }
        return false;
    }

    int IndexOf(T item)
    {
        var rows = Rows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i].Item, item))
                return i;
        }
        return -1;
    }

    float TextLeft(int depth) => 4 + depth * Indent + CELL + (IconColor is null ? 0 : CELL);

    public override float2 Measure()
    {
        var rows = Rows;
        var width = MinWidth;
        foreach (var (item, depth) in rows)
            width = MathF.Max(width, TextLeft(depth) + Font.Advance(Text(item)) + 8);
        return Size = new float2(width, rows.Count * RowHeight);
    }

    public override void Draw(Batch batch)
    {
        var rows = Rows;
        var top = ScreenPosition.Y;
        var bottom = top + Size.Y;
        if (batch.ClipBounds is { } clip)
            (top, bottom) = (MathF.Max(top, clip.Min.Y), MathF.Min(bottom, clip.Max.Y));
        var first = Math.Max(0, (int)((top - ScreenPosition.Y) / RowHeight));
        var last = Math.Min(rows.Count - 1, (int)((bottom - ScreenPosition.Y) / RowHeight));

        for (var row = first; row <= last; row++)
        {
            var (item, depth) = rows[row];
            var position = ScreenPosition + new float2(0, row * RowHeight);
            var isSelected = ReferenceEquals(item, _selected);
            var background =
                isSelected ? (IsFocused ? SelectedRow : SelectedRowUnfocused)
                : row == _hovered && IsHovered ? HoverRow
                : Colors.Transparent;
            batch.AddRect(position, new float2(Size.X, RowHeight), background);
            if (IsDropTarget && row == _dropRow)
            {
                batch.AddRect(position, new float2(Size.X, RowHeight), Theme.Fade(DropColor, 0.25f));
                batch.AddBorder(position, new float2(Size.X, RowHeight), 1, DropColor);
            }

            var x = position.X + 4 + depth * Indent;
            var textY = position.Y + 3;
            // Centred in its column and on the row.
            if (CanExpand(item) && Arrows is { } arrows)
            {
                var image = _expanded.Contains(item) ? arrows.Expanded : arrows.Collapsed;
                batch.AddImage(image, new float2(x + (CELL - image.Size.X) / 2 - 2, position.Y + (RowHeight - image.Size.Y) / 2), ArrowColor);
            }
            x += CELL;

            if (IconColor?.Invoke(item) is { } icon)
            {
                var square = MathF.Round(Font.LineHeight * 0.55f);
                batch.AddRect(new float2(x, MathF.Round(position.Y + (RowHeight - square) / 2)), new float2(square, square), icon);
            }
            if (IconColor is not null)
                x += CELL;

            batch.AddText(Font, Text(item), new float2(x, textY), TextColor);
        }
    }

    int RowAt(float2 point)
    {
        var row = (int)MathF.Floor((point.Y - ScreenPosition.Y) / RowHeight);
        return row >= 0 && row < Rows.Count ? row : -1;
    }

    public override void PointerMove(float2 point) => _hovered = RowAt(point);

    public override void SecondaryClick(float2 point)
    {
        var row = RowAt(point);
        if (row < 0)
            return;
        var item = Rows[row].Item;
        Select(item);
        ItemSecondaryClicked?.Invoke(item, point);
    }

    public override bool CanDrop(object payload, float2 point)
    {
        var row = RowAt(point);
        _dropRow = row >= 0 && AcceptsDrop is { } accepts && accepts(Rows[row].Item, payload) ? row : -1;
        return _dropRow >= 0;
    }

    public override void Drop(object payload, float2 point)
    {
        if (CanDrop(payload, point))
            ItemDropped?.Invoke(Rows[_dropRow].Item, payload);
        _dropRow = -1;
    }

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        var row = RowAt(point);
        if (row < 0)
            return;
        var (item, depth) = Rows[row];

        var now = Environment.TickCount64;
        var isDoubleClick = ReferenceEquals(item, _lastClicked) && now - _lastClickTime <= DOUBLE_CLICK_MS;
        _lastClickTime = isDoubleClick ? 0 : now;
        _lastClicked = item;

        // The arrow, and a little either side of it, opens and closes without moving the selection.
        var arrowLeft = ScreenPosition.X + depth * Indent;
        if (CanExpand(item) && point.X >= arrowLeft && point.X < arrowLeft + 4 + CELL)
        {
            SetExpanded(item, !_expanded.Contains(item));
            return;
        }

        Select(item);
        if (isDoubleClick && CanExpand(item))
            SetExpanded(item, !_expanded.Contains(item));
    }

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        var rows = Rows;
        if (rows.Count == 0)
            return false;

        var at = _selected is null ? -1 : IndexOf(_selected);
        switch (key)
        {
            case Key.Up:
                MoveTo(Math.Max(0, at - 1));
                return true;
            case Key.Down:
                MoveTo(Math.Min(rows.Count - 1, at + 1));
                return true;
            case Key.Home:
                MoveTo(0);
                return true;
            case Key.End:
                MoveTo(rows.Count - 1);
                return true;
            case Key.Right when at >= 0:
            {
                var item = rows[at].Item;
                if (!CanExpand(item))
                    return true;
                if (!_expanded.Contains(item))
                    SetExpanded(item, true);
                else if (at + 1 < Rows.Count && Rows[at + 1].Depth > rows[at].Depth)
                    MoveTo(at + 1);
                return true;
            }
            case Key.Left when at >= 0:
            {
                var (item, depth) = rows[at];
                if (_expanded.Contains(item))
                {
                    SetExpanded(item, false);
                    return true;
                }
                for (var i = at - 1; i >= 0; i--)
                {
                    if (rows[i].Depth < depth)
                    {
                        MoveTo(i);
                        break;
                    }
                }
                return true;
            }
            case Key.Enter when at >= 0 && CanExpand(rows[at].Item):
                SetExpanded(rows[at].Item, !_expanded.Contains(rows[at].Item));
                return true;
        }
        return false;
    }

    void MoveTo(int row)
    {
        if (row < 0 || row >= Rows.Count)
            return;
        Select(Rows[row].Item);
        ScrollOwner?.ScrollToShow(row * RowHeight, (row + 1) * RowHeight);
    }

    public override void FocusGained(bool byKeyboard)
    {
        if (byKeyboard && _selected is null && Rows.Count > 0)
            Select(Rows[0].Item);
    }
}
