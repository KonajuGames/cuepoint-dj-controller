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
}

/// <summary>
/// Rows of items in columns, with one selected. Click to select and double-click (or Enter) to activate; with focus,
/// the arrows, Page Up/Down, Home and End move the selection. Only rows that can be seen are drawn, so it copes
/// with thousands of items. Put it in a <see cref="ScrollPanel"/>, as its first child, and set
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
    /// The selected row's colour while the list doesn't have focus: still visible, but not shouting.
    /// </summary>
    public float4 SelectedRowUnfocused { get; set; } = Colors.Hex(0x3D6FB8, 0.35f);

    /// <summary>
    /// The selection changed, by mouse, keyboard or <see cref="Items"/> changing.
    /// </summary>
    public event Action<ListView<T>>? SelectionChanged;

    /// <summary>
    /// An item was double-clicked, or Enter was pressed on it.
    /// </summary>
    public event Action<T>? ItemActivated;

    IReadOnlyList<T> _items = [];
    int _selected = -1;
    int _hovered = -1;
    long _lastClickTime;
    int _lastClickRow = -1;

    public ListView(Font font) => Font = font;

    public float RowHeight => Font.LineHeight + 6;

    /// <summary>
    /// What the list shows, in order. Setting it keeps the selected item selected if it's still there.
    /// </summary>
    public IReadOnlyList<T> Items
    {
        get => _items;
        set
        {
            var selected = SelectedItem;
            _items = value;
            _hovered = -1;
            var index = selected is null ? -1 : IndexOf(selected);
            if (index != _selected || selected is not null && index < 0)
            {
                _selected = index;
                SelectionChanged?.Invoke(this);
            }
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var index = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
            if (index == _selected)
                return;
            _selected = index;
            SelectionChanged?.Invoke(this);
        }
    }

    public T? SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

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

    public float ContentWidth => Columns.Sum(column => column.Width);

    public override float2 Measure() => Size = new float2(ContentWidth, Math.Max(_items.Count, 1) * RowHeight);

    public override void Draw(Batch batch)
    {
        if (_items.Count == 0)
        {
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

        for (var row = first; row <= last; row++)
        {
            var rowPosition = ScreenPosition + new float2(0, row * RowHeight);
            var rowSize = new float2(Size.X, RowHeight);

            var background =
                row == _selected ? (IsFocused ? SelectedRow : SelectedRowUnfocused)
                : row == _hovered && IsHovered ? HoverRow
                : row % 2 == 1 ? AlternateRow
                : Colors.Transparent;
            batch.AddRect(rowPosition, rowSize, background);

            var x = rowPosition.X;
            foreach (var column in Columns)
            {
                var text = column.Text(_items[row]);
                var cellLeft = x + CellPadding;
                var cellWidth = MathF.Max(0, column.Width - 2 * CellPadding);
                var textX = column.AlignRight ? cellLeft + cellWidth - Font.Advance(text) : cellLeft;

                batch.PushClip(new float2(cellLeft, rowPosition.Y), new float2(cellWidth, RowHeight));
                batch.AddText(Font, text, new float2(textX, rowPosition.Y + 3), TextColor);
                batch.PopClip();
                x += column.Width;
            }
        }
    }

    int RowAt(float2 point)
    {
        var row = (int)MathF.Floor((point.Y - ScreenPosition.Y) / RowHeight);
        return row >= 0 && row < _items.Count ? row : -1;
    }

    // Only meaningful while IsHovered: once the pointer leaves, this stops being called, so Draw checks both.
    public override void PointerMove(float2 point) => _hovered = RowAt(point);

    public override void PointerDown(float2 point, Modifiers modifiers)
    {
        var row = RowAt(point);
        if (row < 0)
            return;

        var now = Environment.TickCount64;
        var isDoubleClick = row == _lastClickRow && now - _lastClickTime <= DOUBLE_CLICK_MS;
        // A third click starts a new pair rather than activating again.
        _lastClickTime = isDoubleClick ? 0 : now;
        _lastClickRow = row;

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
            SelectedIndex = index;
            ScrollOwner?.ScrollToShow(index * RowHeight, (index + 1) * RowHeight);
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
