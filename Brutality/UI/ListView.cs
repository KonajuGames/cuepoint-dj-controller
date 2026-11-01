using Brutal.Numerics;
using Brutality.Input;

namespace Brutality.UI;

/// <summary>
/// One column of a <see cref="ListView{T}"/>: a heading, a width in pixels, and the text to show for each item.
/// </summary>
sealed class ListColumn<T>(string title, float width, Func<T, string> text)
{
    public string Title { get; } = title;
    public float Width { get; set; } = width;
    public Func<T, string> Text { get; } = text;

    /// <summary>
    /// Right-aligns the text, for numbers and times.
    /// </summary>
    public bool AlignRight { get; init; }

    /// <summary>
    /// A colour for an item's text in this column, in place of the list's own; null for the list's.
    /// </summary>
    public Func<T, float4?>? Color { get; init; }
}

/// <summary>
/// Items dragged from a <see cref="ListView{T}"/>, in list order, and the list they came from.
/// </summary>
sealed class DraggedItems<T>(object source, IReadOnlyList<T> items) : IReadOnlyList<T>
{
    public object Source { get; } = source;
    public T this[int index] => items[index];
    public int Count => items.Count;
    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Rows of items in columns. Click to select and double-click (or Enter) to activate; Ctrl+click adds or takes away
/// a row, Shift+click selects the rows from the last one clicked, and Ctrl+A selects them all. With focus, the
/// arrows, Page Up/Down, Home and End move the selection, with Shift to stretch it. Only rows that can be seen are
/// drawn, so it copes with thousands of items. Put it in a <see cref="ScrollPanel"/>, as its first child, and set
/// <see cref="ScrollOwner"/> so keyboard moves keep the selection in view.
/// </summary>
sealed class ListView<T> : Element
    where T : class
{
    const long DOUBLE_CLICK_MS = 500;

    public Font Font { get; set; }
    public List<ListColumn<T>> Columns { get; } = [];

    /// <summary>
    /// The scroll panel this list is the content of, if any.
    /// </summary>
    public ScrollPanel? ScrollOwner { get; set; }

    /// <summary>
    /// Shown in place of rows when there are no items.
    /// </summary>
    public string EmptyText { get; set; } = "Nothing here";

    public float CellPadding { get; set; } = 8;

    public float4 TextColor { get; set; } = Colors.White;
    public float4 MutedColor { get; set; } = Colors.Hex(0x9A9AA8);
    public float4 AlternateRow { get; set; } = Colors.Hex(0xFFFFFF, 0.025f);
    public float4 HoverRow { get; set; } = Colors.Hex(0xFFFFFF, 0.07f);
    public float4 SelectedRow { get; set; } = Colors.Hex(0x3D6FB8, 0.7f);

    /// <summary>
    /// The selected rows' colour while the list doesn't have focus: still visible, but not shouting.
    /// </summary>
    public float4 SelectedRowUnfocused { get; set; } = Colors.Hex(0x3D6FB8, 0.35f);

    /// <summary>
    /// How far along some work on an item is, from 0 to 1, or null if there's none: drawn as a bar filling the row's
    /// background from the left.
    /// </summary>
    public Func<T, float?>? RowProgress { get; set; }

    public float4 ProgressColor { get; set; } = Colors.Hex(0xEB7828, 0.35f);

    /// <summary>
    /// What to call an item on the label that follows it when it's dragged; rows can't be dragged if null.
    /// </summary>
    public Func<T, string>? DragLabel { get; set; }

    /// <summary>
    /// Whether the rows can be dragged to new places in the list, raising <see cref="Reordered"/>.
    /// </summary>
    public bool CanReorder { get; set; }

    public float4 DropLineColor { get; set; } = Colors.Hex(0xEB7828);

    /// <summary>
    /// The rows drawn last frame, by index into <see cref="Items"/>: those in view. Empty before the first draw, and
    /// again from when <see cref="Items"/> changes until the next, since the old rows may no longer exist.
    /// </summary>
    public Range VisibleRows { get; private set; } = 0..0;

    /// <summary>
    /// The selection changed, by mouse, keyboard or <see cref="Items"/> changing.
    /// </summary>
    public event Action<ListView<T>>? SelectionChanged;

    /// <summary>
    /// An item was double-clicked, or Enter was pressed on it.
    /// </summary>
    public event Action<T>? ItemActivated;

    /// <summary>
    /// Delete was pressed with these selected, in list order. Delete does nothing if no one's listening.
    /// </summary>
    public event Action<IReadOnlyList<T>>? DeleteRequested;

    /// <summary>
    /// Rows were dragged to a new place: the items, and the index (in <see cref="Items"/> as they were) of the row
    /// they were dropped before, or the count for the end. The list doesn't move them itself.
    /// </summary>
    public event Action<IReadOnlyList<T>, int>? Reordered;

    IReadOnlyList<T> _items = [];

    // The current row (where the keyboard moves from and Shift stretches to), the row a Shift selection stretches
    // from, and every selected item.
    int _selected = -1;
    int _anchor = -1;
    readonly HashSet<T> _selection = new(ReferenceEqualityComparer.Instance);

    int _hovered = -1;
    long _lastClickTime;
    int _lastClickRow = -1;

    // A plain press on a row already among several selected waits for the release to select it alone, so the
    // selection can be dragged.
    int _pendingSingle = -1;

    // Where a reordering drag would drop, as the row it would go before.
    int _dropBefore = -1;

    public ListView(Font font)
    {
        Font = font;
        Clicked += _ =>
        {
            if (_pendingSingle >= 0 && _pendingSingle < _items.Count)
                SelectedIndex = _pendingSingle;
            _pendingSingle = -1;
        };
    }

    public float RowHeight => Font.LineHeight + 6;

    /// <summary>
    /// What the list shows, in order. Setting it keeps selected items selected where they're still there.
    /// </summary>
    public IReadOnlyList<T> Items
    {
        get => _items;
        set
        {
            var current = _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;
            var anchor = _anchor >= 0 && _anchor < _items.Count ? _items[_anchor] : null;
            _items = value;
            _hovered = -1;
            _pendingSingle = -1;
            VisibleRows = 0..0;

            var still = new HashSet<T>(ReferenceEqualityComparer.Instance);
            foreach (var item in value)
            {
                if (_selection.Contains(item))
                    still.Add(item);
            }
            var changed = still.Count != _selection.Count;
            _selection.Clear();
            _selection.UnionWith(still);

            var index = current is null ? -1 : IndexOf(current);
            changed |= index != _selected;
            _selected = index;
            _anchor = anchor is null ? index : IndexOf(anchor);
            if (changed)
                SelectionChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// The current row, or -1 for none. Setting it selects that row alone.
    /// </summary>
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var index = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
            var alone = index < 0 ? _selection.Count == 0 : _selection.Count == 1 && _selection.Contains(_items[index]);
            if (index == _selected && alone)
                return;
            _selected = _anchor = index;
            _selection.Clear();
            if (index >= 0)
                _selection.Add(_items[index]);
            SelectionChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// The current row's item if it's selected, otherwise the first selected; null if none is.
    /// </summary>
    public T? SelectedItem =>
        _selected >= 0 && _selected < _items.Count && _selection.Contains(_items[_selected]) ? _items[_selected]
        : _selection.Count > 0 ? SelectedItems[0]
        : null;

    /// <summary>
    /// Every selected item, in list order.
    /// </summary>
    public IReadOnlyList<T> SelectedItems => _items.Where(_selection.Contains).ToList();

    public bool IsSelected(T item) => _selection.Contains(item);

    public override bool IsInteractive => true;
    public override bool IsFocusable => true;
    public override Cursor Cursor => Cursor.Arrow;

    int IndexOf(T item)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], item))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Selects the rows from the anchor (the last row clicked alone) to <paramref name="row"/>, as well as what's
    /// selected already if <paramref name="add"/>.
    /// </summary>
    void SelectRange(int row, bool add)
    {
        if (_anchor < 0 || _anchor >= _items.Count)
            _anchor = row;
        if (!add)
            _selection.Clear();
        for (var i = Math.Min(_anchor, row); i <= Math.Max(_anchor, row); i++)
            _selection.Add(_items[i]);
        _selected = row;
        SelectionChanged?.Invoke(this);
    }

    void Toggle(int row)
    {
        if (!_selection.Remove(_items[row]))
            _selection.Add(_items[row]);
        _selected = _anchor = row;
        SelectionChanged?.Invoke(this);
    }

    public void SelectAll()
    {
        if (_items.Count == 0)
            return;
        _selection.UnionWith(_items);
        if (_selected < 0)
            _selected = _anchor = 0;
        SelectionChanged?.Invoke(this);
    }

    public float ContentWidth => Columns.Sum(column => column.Width);

    public override float2 Measure() => Size = new float2(ContentWidth, Math.Max(_items.Count, 1) * RowHeight);

    public override void Draw(Batch batch)
    {
        if (_items.Count == 0)
        {
            VisibleRows = 0..0;
            batch.AddText(Font, EmptyText, ScreenPosition + new float2(CellPadding, 3), MutedColor);
            return;
        }

        // Only the rows inside the clip (the scroll panel's viewport) can be seen.
        var top = ScreenPosition.Y;
        var bottom = top + Size.Y;
        if (batch.ClipBounds is { } clip)
            (top, bottom) = (MathF.Max(top, clip.Min.Y), MathF.Min(bottom, clip.Max.Y));
        var first = Math.Max(0, (int)((top - ScreenPosition.Y) / RowHeight));
        var last = Math.Min(_items.Count - 1, (int)((bottom - ScreenPosition.Y) / RowHeight));
        VisibleRows = first..Math.Max(first, last + 1);

        for (var row = first; row <= last; row++)
        {
            var rowPosition = ScreenPosition + new float2(0, row * RowHeight);
            var rowSize = new float2(Size.X, RowHeight);

            var background =
                _selection.Contains(_items[row]) ? (IsFocused ? SelectedRow : SelectedRowUnfocused)
                : row == _hovered && IsHovered ? HoverRow
                : row % 2 == 1 ? AlternateRow
                : Colors.Transparent;
            batch.AddRect(rowPosition, rowSize, background);
            if (RowProgress?.Invoke(_items[row]) is { } progress)
                batch.AddRect(rowPosition, new float2(Size.X * Math.Clamp(progress, 0, 1), RowHeight), ProgressColor);

            var x = rowPosition.X;
            foreach (var column in Columns)
            {
                var text = column.Text(_items[row]);
                var cellLeft = x + CellPadding;
                var cellWidth = MathF.Max(0, column.Width - 2 * CellPadding);
                var textX = column.AlignRight ? cellLeft + cellWidth - Font.Advance(text) : cellLeft;

                batch.PushClip(new float2(cellLeft, rowPosition.Y), new float2(cellWidth, RowHeight));
                batch.AddText(Font, text, new float2(textX, rowPosition.Y + 3), column.Color?.Invoke(_items[row]) ?? TextColor);
                batch.PopClip();
                x += column.Width;
            }
        }

        // Where a reordering drag would land, as a line between rows.
        if (IsDropTarget && _dropBefore >= 0)
            batch.AddRect(new float2(ScreenPosition.X, MathF.Round(ScreenPosition.Y + _dropBefore * RowHeight - 1)), new float2(Size.X, 2), DropLineColor);
    }

    int RowAt(float2 point)
    {
        var row = (int)MathF.Floor((point.Y - ScreenPosition.Y) / RowHeight);
        return row >= 0 && row < _items.Count ? row : -1;
    }

    // A selected row drags the whole selection; any other, just itself.
    public override DragData? StartDrag(float2 point)
    {
        var row = RowAt(point);
        if (DragLabel is not { } label || row < 0)
            return null;
        _pendingSingle = -1;
        IReadOnlyList<T> items = _selection.Contains(_items[row]) ? SelectedItems : [_items[row]];
        var text = items.Count == 1 ? label(items[0]) : $"{label(items[0])} and {items.Count - 1} more";
        return new DragData(new DraggedItems<T>(this, items), text, Font);
    }

    public override bool CanDrop(object payload, float2 point)
    {
        if (!CanReorder || payload is not DraggedItems<T> dragged || dragged.Source != this)
            return false;
        _dropBefore = Math.Clamp((int)MathF.Round((point.Y - ScreenPosition.Y) / RowHeight), 0, _items.Count);
        return true;
    }

    public override void Drop(object payload, float2 point)
    {
        if (payload is DraggedItems<T> dragged && CanDrop(payload, point))
            Reordered?.Invoke(dragged, _dropBefore);
        _dropBefore = -1;
    }

    // Only meaningful while IsHovered: once the pointer leaves, this stops being called, so Draw checks both.
    public override void PointerMove(float2 point) => _hovered = RowAt(point);

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        _pendingSingle = -1;
        var row = RowAt(point);
        if (row < 0)
            return;

        if ((modifiers & Modifiers.Shift) != 0)
        {
            SelectRange(row, add: (modifiers & Modifiers.Control) != 0);
            return;
        }
        if ((modifiers & Modifiers.Control) != 0)
        {
            Toggle(row);
            return;
        }

        var now = Environment.TickCount64;
        var isDoubleClick = row == _lastClickRow && now - _lastClickTime <= DOUBLE_CLICK_MS;
        // A third click starts a new pair rather than activating again.
        _lastClickTime = isDoubleClick ? 0 : now;
        _lastClickRow = row;

        if (_selection.Count > 1 && _selection.Contains(_items[row]))
        {
            _selected = _anchor = row;
            _pendingSingle = row;
        }
        else
            SelectedIndex = row;
        if (isDoubleClick)
            ItemActivated?.Invoke(_items[row]);
    }

    public override bool KeyDown(Key key, Modifiers modifiers)
    {
        if (_items.Count == 0)
            return false;

        var page = ScrollOwner is { } owner ? Math.Max(1, (int)(owner.ViewportSize.Y / RowHeight) - 1) : 10;
        var from = _selected < 0 ? -1 : _selected;
        int? to = key switch
        {
            Key.Up => Math.Max(0, from - 1),
            Key.Down => Math.Min(_items.Count - 1, from + 1),
            Key.PageUp => Math.Max(0, from - page),
            Key.PageDown => Math.Min(_items.Count - 1, Math.Max(0, from) + page),
            Key.Home => 0,
            Key.End => _items.Count - 1,
            _ => null,
        };

        if (to is { } index)
        {
            if ((modifiers & Modifiers.Shift) != 0)
                SelectRange(index, add: false);
            else
                SelectedIndex = index;
            ScrollOwner?.ScrollToShow(index * RowHeight, (index + 1) * RowHeight);
            return true;
        }

        if (key == Key.A && (modifiers & Modifiers.Control) != 0)
        {
            SelectAll();
            return true;
        }

        if (key == Key.Delete && DeleteRequested is { } delete && _selection.Count > 0)
        {
            delete(SelectedItems);
            return true;
        }

        if (key == Key.Enter && SelectedItem is { } item)
        {
            ItemActivated?.Invoke(item);
            return true;
        }
        return false;
    }

    public override void FocusGained(bool byKeyboard)
    {
        // Tabbing into an unselected list picks the first row, so the arrows have somewhere to start.
        if (byKeyboard && _selected < 0 && _items.Count > 0)
            SelectedIndex = 0;
    }
}

/// <summary>
/// The column headings for a <see cref="ListView{T}"/>, drawn to line up with its columns. Put it just above the
/// list's scroll panel.
/// </summary>
sealed class ListHeader<T>(ListView<T> list) : Element
    where T : class
{
    public float4 Color { get; set; } = Colors.Hex(0x9A9AA8);
    public float4 Divider { get; set; } = Colors.Hex(0xFFFFFF, 0.12f);

    public override float2 Measure() => Size = new float2(list.ContentWidth, list.Font.LineHeight + 6);

    public override void Draw(Batch batch)
    {
        var x = ScreenPosition.X;
        foreach (var column in list.Columns)
        {
            var cellLeft = x + list.CellPadding;
            var cellWidth = MathF.Max(0, column.Width - 2 * list.CellPadding);
            var textX = column.AlignRight ? cellLeft + cellWidth - list.Font.Advance(column.Title) : cellLeft;

            batch.PushClip(new float2(cellLeft, ScreenPosition.Y), new float2(cellWidth, Size.Y));
            batch.AddText(list.Font, column.Title, new float2(textX, ScreenPosition.Y + 3), Color);
            batch.PopClip();
            x += column.Width;
        }
        batch.AddRect(ScreenPosition + new float2(0, Size.Y - 1), new float2(Size.X, 1), Divider);
    }
}
