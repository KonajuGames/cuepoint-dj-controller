using System.Text;
using Brutal.Numerics;

namespace Brutality.UI;

/// <summary>
/// A single-line text field. Click or Tab into it, then type: arrows, Home and End move the caret, and holding Shift
/// with them selects. Ctrl (Option on macOS) moves or deletes a word at a time. Ctrl+A (Cmd+A on macOS) selects
/// everything, Ctrl+C, Ctrl+X and Ctrl+V copy, cut and paste, Ctrl+Z undoes, Ctrl+Y or Ctrl+Shift+Z redoes (Cmd+Z and
/// Cmd+Shift+Z on macOS), and Enter raises <see cref="Submitted"/>. Drag to select, Shift+click to extend,
/// double-click to select a word and triple-click to select everything. Text wider than the box glides sideways to
/// keep the caret in view (see <see cref="SmoothScrolling"/>).
/// </summary>
sealed class TextBox(Font font) : UiElement
{
    const long CARET_BLINK_MS = 530;

    /// <summary>
    /// A click this soon after the last one, and this close to it, counts towards a double or triple click.
    /// </summary>
    const long DOUBLE_CLICK_MS = 500;
    const float DOUBLE_CLICK_DISTANCE = 4;

    public Font Font { get; set; } = font;

    /// <summary>
    /// Shown, dimmed, while the box is empty.
    /// </summary>
    public string Placeholder { get; set; } = "";

    public float Width { get; set; } = 200;
    public float Padding { get; set; } = 6;
    public int MaxLength { get; set; } = 256;

    public float4 Background { get; set; } = UiColor.Hex(0x0E0E13);
    public float4 BorderColor { get; set; } = UiColor.Hex(0x3A3A48);
    public float4 FocusColor { get; set; } = UiColor.Hex(0x7AB8FF);
    public float4 TextColor { get; set; } = UiColor.White;
    public float4 PlaceholderColor { get; set; } = UiColor.Hex(0x6A6A78);
    public float4 SelectionColor { get; set; } = UiColor.Hex(0x3D6FB8, 0.7f);

    /// <summary>
    /// Glide sideways to follow the caret rather than jumping. On by default.
    /// </summary>
    public bool SmoothScrolling { get; set; } = true;

    /// <summary>
    /// How quickly the sideways glide settles: the time to cover about two thirds of the remaining distance.
    /// </summary>
    public float SmoothScrollTime { get; set; } = 0.035f;

    /// <summary>
    /// How far the text is scrolled left, in pixels, as drawn right now.
    /// </summary>
    public float ScrollOffset => _scroll;

    /// <summary>
    /// Where the sideways scroll is heading; equal to <see cref="ScrollOffset"/> once it settles.
    /// </summary>
    public float ScrollTarget => _scrollTarget;

    public bool IsScrollAnimating => _scroll != _scrollTarget;

    /// <summary>
    /// Enter was pressed. Handlers often read <see cref="Text"/> and then clear it.
    /// </summary>
    public event Action<TextBox>? Submitted;

    /// <summary>
    /// The text changed by typing, deleting, cutting or pasting (not by setting <see cref="Text"/>).
    /// </summary>
    public event Action<TextBox>? TextChanged;

    string _text = "";

    /// <summary>
    /// Where the caret is, and the other end of the selection. Equal when nothing is selected.
    /// </summary>
    int _caret, _anchor;

    /// <summary>
    /// How far the text is scrolled left right now, and where that's heading to keep the caret in view.
    /// </summary>
    float _scroll, _scrollTarget;

    double _lastScrollStep = double.NaN;
    long _lastActivity = Environment.TickCount64;

    long _lastClickTime;
    float2 _lastClickPoint;

    /// <summary>
    /// Clicks in the current run: 1 places the caret, 2 selects a word, 3 or more select everything.
    /// </summary>
    int _clickCount;

    /// <summary>
    /// After a double-click, dragging extends by whole words from the word that was clicked.
    /// </summary>
    (int Start, int End)? _dragWord;

    /// <summary>
    /// After a triple-click, dragging keeps everything selected.
    /// </summary>
    bool _dragAll;

    /// <summary>
    /// The text, caret and selection before an edit, so undo puts all three back.
    /// </summary>
    readonly record struct Snapshot(string Text, int Caret, int Anchor);

    /// <summary>
    /// What the last edit was, so a run of the same kind becomes one undo step.
    /// </summary>
    enum EditKind
    {
        None,
        Typing,
        Deleting,
    }

    const int MAX_UNDO_STEPS = 100;

    /// <summary>
    /// A run of typing or deleting stays one undo step until a pause this long.
    /// </summary>
    const long UNDO_GROUP_MS = 1000;

    readonly List<Snapshot> _undo = [];
    readonly List<Snapshot> _redo = [];
    EditKind _lastEdit;
    long _lastEditTime;

    public string Text
    {
        get => _text;
        set
        {
            _text = value.Length > MaxLength ? value[..MaxLength] : value;
            _caret = Math.Min(_caret, _text.Length);
            _anchor = Math.Min(_anchor, _text.Length);

            // Setting the text from code starts afresh, rather than being something the user can undo.
            ClearHistory();
        }
    }

    /// <summary>
    /// The caret's position as an index into <see cref="Text"/>. Setting it clears the selection.
    /// </summary>
    public int CaretIndex
    {
        get => _caret;
        set => _caret = _anchor = Math.Clamp(value, 0, _text.Length);
    }

    public int SelectionStart => Math.Min(_caret, _anchor);
    public int SelectionLength => Math.Abs(_caret - _anchor);
    public bool HasSelection => _caret != _anchor;
    public string SelectedText => _text.Substring(SelectionStart, SelectionLength);

    /// <summary>
    /// Selects <paramref name="length"/> characters from <paramref name="start"/>, leaving the caret at the end.
    /// </summary>
    public void Select(int start, int length)
    {
        _anchor = Math.Clamp(start, 0, _text.Length);
        _caret = Math.Clamp(start + length, 0, _text.Length);
    }

    public void SelectAll() => Select(0, _text.Length);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Reverts the last edit (or run of typing or deleting), restoring the caret and selection from before it.
    /// </summary>
    public bool Undo() => Step(_undo, _redo);

    public bool Redo() => Step(_redo, _undo);

    public void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _lastEdit = EditKind.None;
    }

    bool Step(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0)
            return false;

        to.Add(new Snapshot(_text, _caret, _anchor));
        var state = from[^1];
        from.RemoveAt(from.Count - 1);
        (_text, _caret, _anchor) = (state.Text, state.Caret, state.Anchor);

        _lastEdit = EditKind.None;
        _lastActivity = Environment.TickCount64;
        TextChanged?.Invoke(this);
        return true;
    }

    /// <summary>
    /// Call just before changing the text. Saves an undo step, unless this edit continues the current run of typing
    /// or deleting, and drops anything that could have been redone.
    /// </summary>
    void RecordEdit(EditKind kind, bool startsWord = false)
    {
        var now = Environment.TickCount64;
        var continues =
            kind != EditKind.None
            && kind == _lastEdit
            && !HasSelection
            && !startsWord
            && now - _lastEditTime <= UNDO_GROUP_MS;

        if (!continues)
        {
            _undo.Add(new Snapshot(_text, _caret, _anchor));
            if (_undo.Count > MAX_UNDO_STEPS)
                _undo.RemoveAt(0);
        }

        _redo.Clear();
        _lastEdit = kind;
        _lastEditTime = now;
    }

    public override bool IsInteractive => true;
    public override bool IsFocusable => true;
    public override bool CapturesKeyboard => true;
    public override UiCursor Cursor => UiCursor.Text;

    const float BORDER = 1;
    float Inset => Padding + BORDER;
    float InnerWidth => MathF.Max(0, Size.X - 2 * Inset);

    float XOf(int index) => MathF.Round(Font.Advance(_text.AsSpan(0, index)));

    public override float2 Measure() => Size = new float2(Width, Font.LineHeight + 2 * Inset);

    public override void Arrange(float2 position)
    {
        base.Arrange(position);

        // Aim to scroll just enough to keep the caret in view, leaving a pixel for the caret itself. Worked out from
        // the target rather than the current position, so the view heads for one place even mid-glide.
        var caretX = XOf(_caret);
        var maxScroll = MathF.Max(0, XOf(_text.Length) + 1 - InnerWidth);
        var target = _scrollTarget;
        if (caretX - target > InnerWidth - 1)
            target = caretX - InnerWidth + 1;
        if (caretX - target < 0)
            target = caretX;
        _scrollTarget = Math.Clamp(target, 0, maxScroll);

        // The first layout lands in place; after that, glide.
        var first = double.IsNaN(_lastScrollStep);
        var dt = UiClock.Delta(ref _lastScrollStep);
        _scroll = UiClock.Approach(_scroll, _scrollTarget, dt, SmoothScrolling && !first ? SmoothScrollTime : 0);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);
    }

    public override void Draw(UiBatch batch)
    {
        batch.AddRect(ScreenPosition, Size, Background);
        batch.AddBorder(ScreenPosition, Size, IsFocused ? 2 : BORDER, IsFocused ? FocusColor : BorderColor);

        var inner = ScreenPosition + new float2(Inset, Inset);
        batch.PushClip(inner, new float2(InnerWidth, Font.LineHeight));

        // The selection only shows while focused, as in most toolkits; it's kept for when focus returns.
        if (IsFocused && HasSelection)
        {
            var start = XOf(SelectionStart) - _scroll;
            var end = XOf(SelectionStart + SelectionLength) - _scroll;
            batch.AddRect(inner + new float2(start, 0), new float2(end - start, Font.LineHeight), SelectionColor);
        }

        if (_text.Length == 0)
            batch.AddText(Font, Placeholder, inner, PlaceholderColor);
        else
            batch.AddText(Font, _text, inner - new float2(_scroll, 0), TextColor);

        // Solid while typing, blinking once idle.
        var showCaret = IsFocused && (Environment.TickCount64 - _lastActivity) / CARET_BLINK_MS % 2 == 0;
        if (showCaret)
            batch.AddRect(inner + new float2(XOf(_caret) - _scroll, 0), new float2(1, Font.LineHeight), TextColor);

        batch.PopClip();
    }

    public override void FocusGained(bool byKeyboard)
    {
        // Tabbing in selects the lot, so typing replaces it; clicking places the caret instead.
        if (byKeyboard)
            SelectAll();
        _lastActivity = Environment.TickCount64;
    }

    public override void PointerDown(float2 point, UiModifiers modifiers)
    {
        var index = IndexAtPoint(point);
        var now = Environment.TickCount64;
        // Shift+click always extends the selection, however soon it follows the last click.
        var continuesRun =
            (modifiers & UiModifiers.Shift) == 0
            && now - _lastClickTime <= DOUBLE_CLICK_MS
            && float2.Distance(point, _lastClickPoint) <= DOUBLE_CLICK_DISTANCE;
        _clickCount = continuesRun ? _clickCount + 1 : 1;
        _lastClickTime = now;
        _lastClickPoint = point;
        _dragWord = null;
        _dragAll = false;
        _lastEdit = EditKind.None; // Typing somewhere else is a new undo step.

        if (_clickCount >= 3)
        {
            // A fourth click and on keep everything selected, rather than cycling back to a caret.
            SelectAll();
            _dragAll = true;
        }
        else if (_clickCount == 2)
        {
            var word = WordAt(index);
            Select(word.Start, word.End - word.Start);
            _dragWord = word;
        }
        else if ((modifiers & UiModifiers.Shift) != 0)
            _caret = index;
        else
            CaretIndex = index;

        _lastActivity = now;
    }

    public override void PointerDrag(float2 point)
    {
        if (_dragAll)
            return;

        var index = IndexAtPoint(point);
        if (_dragWord is { } word)
        {
            // Grow by whole words, keeping the double-clicked word selected.
            var other = WordAt(index);
            (_anchor, _caret) = index < word.Start ? (word.End, other.Start) : (word.Start, Math.Max(word.End, other.End));
        }
        else
            _caret = index;
        _lastActivity = Environment.TickCount64;
    }

    int IndexAtPoint(float2 point) => IndexAt(point.X - (ScreenPosition.X + Inset) + _scroll);

    /// <summary>
    /// The caret position nearest <paramref name="x"/> pixels from the start of the text.
    /// </summary>
    int IndexAt(float x)
    {
        float pen = 0;
        var index = 0;
        foreach (var rune in _text.EnumerateRunes())
        {
            var advance = Font.GetGlyph(rune.Value).Advance;
            if (x < pen + advance / 2)
                return index;
            pen += advance;
            index += rune.Utf16SequenceLength;
        }
        return _text.Length;
    }

    public override bool KeyDown(UiKey key, UiModifiers modifiers)
    {
        var byWord = modifiers.HasWordModifier();
        var shortcut = modifiers.HasShortcutModifier();
        var extend = (modifiers & UiModifiers.Shift) != 0;
        var handled = true;
        var edits = false;

        switch (key)
        {
            // Without Shift, Left and Right first collapse a selection to the side they point at.
            case UiKey.Left when HasSelection && !extend && !byWord:
                CaretIndex = SelectionStart;
                break;
            case UiKey.Right when HasSelection && !extend && !byWord:
                CaretIndex = SelectionStart + SelectionLength;
                break;
            case UiKey.Left:
                MoveCaret(byWord ? PreviousWord(_caret) : PreviousCharacter(_caret), extend);
                break;
            case UiKey.Right:
                MoveCaret(byWord ? NextWord(_caret) : NextCharacter(_caret), extend);
                break;
            case UiKey.Home:
                MoveCaret(0, extend);
                break;
            case UiKey.End:
                MoveCaret(_text.Length, extend);
                break;

            case UiKey.Backspace when HasSelection:
            case UiKey.Delete when HasSelection:
                DeleteSelection();
                edits = true;
                break;
            case UiKey.Backspace:
                Remove(byWord ? PreviousWord(_caret) : PreviousCharacter(_caret), _caret, EditKind.Deleting);
                edits = true;
                break;
            case UiKey.Delete:
                Remove(_caret, byWord ? NextWord(_caret) : NextCharacter(_caret), EditKind.Deleting);
                edits = true;
                break;

            case UiKey.A when shortcut:
                SelectAll();
                break;
            case UiKey.C when shortcut:
                if (HasSelection)
                    UiClipboard.SetText(SelectedText);
                break;
            case UiKey.X when shortcut:
                if (HasSelection)
                {
                    UiClipboard.SetText(SelectedText);
                    DeleteSelection();
                }
                break;
            case UiKey.V when shortcut:
                Insert(UiClipboard.GetText() ?? "", EditKind.None);
                edits = true;
                break;

            case UiKey.Z when shortcut && extend:
            case UiKey.Y when shortcut && !OperatingSystem.IsMacOS():
                Redo();
                break;
            case UiKey.Z when shortcut:
                Undo();
                break;

            case UiKey.Enter:
                Submitted?.Invoke(this);
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
            _lastActivity = Environment.TickCount64;

        // Moving the caret or selecting ends a run, so the next typing is a step of its own.
        if (handled && !edits)
            _lastEdit = EditKind.None;
        return handled;
    }

    public override void TextInput(Rune character)
    {
        if (Rune.IsControl(character))
            return;

        // Each new word is its own undo step; a space stays with the word before it.
        var startsWord = !Rune.IsWhiteSpace(character) && _caret > 0 && char.IsWhiteSpace(_text[_caret - 1]);
        Insert(character.ToString(), EditKind.Typing, startsWord);
    }

    void MoveCaret(int index, bool extend)
    {
        _caret = index;
        if (!extend)
            _anchor = index;
    }

    /// <summary>
    /// Replaces the selection, or inserts at the caret, with <paramref name="text"/>.
    /// </summary>
    void Insert(string text, EditKind kind, bool startsWord = false)
    {
        // Single line: pasted line breaks and tabs become spaces, other control characters go.
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\r' or '\n' or '\t')
                builder.Append(' ');
            else if (!Rune.IsControl(rune))
                builder.Append(rune.ToString());
        }
        if (builder.Length == 0)
            return;

        var room = MaxLength - (_text.Length - SelectionLength);
        if (room <= 0)
            return;

        var insert = builder.ToString();
        if (insert.Length > room)
        {
            // Don't split a surrogate pair at the cut.
            var cut = char.IsHighSurrogate(insert[room - 1]) ? room - 1 : room;
            insert = insert[..cut];
        }
        if (insert.Length == 0)
            return;

        RecordEdit(kind, startsWord);
        var start = SelectionStart;
        _text = _text.Remove(start, SelectionLength).Insert(start, insert);
        CaretIndex = start + insert.Length;
        _lastActivity = Environment.TickCount64;
        TextChanged?.Invoke(this);
    }

    void DeleteSelection() => Remove(SelectionStart, SelectionStart + SelectionLength, EditKind.None);

    void Remove(int start, int end, EditKind kind)
    {
        if (end <= start)
            return;
        RecordEdit(kind);
        _text = _text.Remove(start, end - start);
        CaretIndex = start;
        TextChanged?.Invoke(this);
    }

    // Caret moves step over whole code points, so emoji and other surrogate pairs stay intact.
    int PreviousCharacter(int index) =>
        index >= 2 && char.IsLowSurrogate(_text[index - 1]) && char.IsHighSurrogate(_text[index - 2]) ? index - 2
        : Math.Max(0, index - 1);

    int NextCharacter(int index) =>
        index + 1 < _text.Length && char.IsHighSurrogate(_text[index]) && char.IsLowSurrogate(_text[index + 1])
            ? index + 2
            : Math.Min(_text.Length, index + 1);

    /// <summary>
    /// The start of the word before <paramref name="index"/>, skipping any spaces first.
    /// </summary>
    int PreviousWord(int index)
    {
        while (index > 0 && char.IsWhiteSpace(_text[index - 1]))
            index--;
        while (index > 0 && !char.IsWhiteSpace(_text[index - 1]))
            index--;
        return index;
    }

    /// <summary>
    /// The end of the word after <paramref name="index"/>, skipping any spaces first.
    /// </summary>
    int NextWord(int index)
    {
        while (index < _text.Length && char.IsWhiteSpace(_text[index]))
            index++;
        while (index < _text.Length && !char.IsWhiteSpace(_text[index]))
            index++;
        return index;
    }

    /// <summary>
    /// The run of word characters, or of spaces, around <paramref name="index"/>, as double-click selects it.
    /// </summary>
    (int Start, int End) WordAt(int index)
    {
        if (_text.Length == 0)
            return (0, 0);

        // Clicks land on character boundaries, so look at both neighbours and prefer a word over a space.
        var probe =
            index < _text.Length && !char.IsWhiteSpace(_text[index]) ? index
            : index > 0 && !char.IsWhiteSpace(_text[index - 1]) ? index - 1
            : Math.Min(index, _text.Length - 1);

        var space = char.IsWhiteSpace(_text[probe]);
        int start = probe, end = probe + 1;
        while (start > 0 && char.IsWhiteSpace(_text[start - 1]) == space)
            start--;
        while (end < _text.Length && char.IsWhiteSpace(_text[end]) == space)
            end++;
        return (start, end);
    }
}
