using System.Text;
using Brutal.Numerics;

namespace Brutality.UI;

/// <summary>
/// A single-line text field. Click or Tab into it, then type: arrows, Home and End move the caret, Backspace and
/// Delete remove a character (or a word, with Ctrl, or Option on macOS), Ctrl+V (Cmd+V) pastes and Enter raises
/// <see cref="Submitted"/>. Text wider than the box scrolls sideways to keep the caret in view.
/// </summary>
sealed class TextBox(Font font) : UiElement
{
    const long CARET_BLINK_MS = 530;

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

    /// <summary>
    /// Enter was pressed. Handlers often read <see cref="Text"/> and then clear it.
    /// </summary>
    public event Action<TextBox>? Submitted;

    /// <summary>
    /// The text changed by typing, deleting or pasting (not by setting <see cref="Text"/>).
    /// </summary>
    public event Action<TextBox>? TextChanged;

    string _text = "";
    int _caret;
    float _scroll;
    long _lastActivity = Environment.TickCount64;

    public string Text
    {
        get => _text;
        set
        {
            _text = value.Length > MaxLength ? value[..MaxLength] : value;
            _caret = Math.Min(_caret, _text.Length);
        }
    }

    /// <summary>
    /// The caret's position as an index into <see cref="Text"/>.
    /// </summary>
    public int CaretIndex
    {
        get => _caret;
        set => _caret = Math.Clamp(value, 0, _text.Length);
    }

    public override bool IsInteractive => true;
    public override bool IsFocusable => true;
    public override bool CapturesKeyboard => true;
    public override UiCursor Cursor => UiCursor.Text;

    const float BORDER = 1;
    float Inset => Padding + BORDER;
    float InnerWidth => MathF.Max(0, Size.X - 2 * Inset);

    public override float2 Measure() => Size = new float2(Width, Font.LineHeight + 2 * Inset);

    public override void Arrange(float2 position)
    {
        base.Arrange(position);

        // Scroll just enough to keep the caret in view, leaving a pixel for the caret itself.
        var caretX = MathF.Round(Font.Advance(_text.AsSpan(0, _caret)));
        var textWidth = MathF.Round(Font.Advance(_text));
        if (caretX - _scroll > InnerWidth - 1)
            _scroll = caretX - InnerWidth + 1;
        if (caretX - _scroll < 0)
            _scroll = caretX;
        _scroll = Math.Clamp(_scroll, 0, MathF.Max(0, textWidth + 1 - InnerWidth));
    }

    public override void Draw(UiBatch batch)
    {
        batch.AddRect(ScreenPosition, Size, Background);
        batch.AddBorder(ScreenPosition, Size, IsFocused ? 2 : BORDER, IsFocused ? FocusColor : BorderColor);

        var inner = ScreenPosition + new float2(Inset, Inset);
        batch.PushClip(inner, new float2(InnerWidth, Font.LineHeight));

        if (_text.Length == 0)
            batch.AddText(Font, Placeholder, inner, PlaceholderColor);
        else
            batch.AddText(Font, _text, inner - new float2(_scroll, 0), TextColor);

        // Solid while typing, blinking once idle.
        var showCaret = IsFocused && (Environment.TickCount64 - _lastActivity) / CARET_BLINK_MS % 2 == 0;
        if (showCaret)
        {
            var caretX = MathF.Round(Font.Advance(_text.AsSpan(0, _caret))) - _scroll;
            batch.AddRect(inner + new float2(caretX, 0), new float2(1, Font.LineHeight), TextColor);
        }

        batch.PopClip();
    }

    public override void PointerDown(float2 point)
    {
        _caret = IndexAt(point.X - (ScreenPosition.X + Inset) + _scroll);
        _lastActivity = Environment.TickCount64;
    }

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
        var handled = true;
        switch (key)
        {
            case UiKey.Left:
                _caret = byWord ? PreviousWord(_caret) : PreviousCharacter(_caret);
                break;
            case UiKey.Right:
                _caret = byWord ? NextWord(_caret) : NextCharacter(_caret);
                break;
            case UiKey.Home:
                _caret = 0;
                break;
            case UiKey.End:
                _caret = _text.Length;
                break;
            case UiKey.Backspace:
                Remove(byWord ? PreviousWord(_caret) : PreviousCharacter(_caret), _caret);
                break;
            case UiKey.Delete:
                Remove(_caret, byWord ? NextWord(_caret) : NextCharacter(_caret));
                break;
            case UiKey.Enter:
                Submitted?.Invoke(this);
                break;
            case UiKey.V when modifiers.HasShortcutModifier():
                Insert(UiClipboard.GetText() ?? "");
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
            _lastActivity = Environment.TickCount64;
        return handled;
    }

    public override void TextInput(Rune character)
    {
        if (!Rune.IsControl(character))
            Insert(character.ToString());
    }

    void Insert(string text)
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

        var room = MaxLength - _text.Length;
        if (room <= 0 || builder.Length == 0)
            return;

        var insert = builder.ToString();
        if (insert.Length > room)
        {
            // Don't split a surrogate pair at the cut.
            var cut = char.IsHighSurrogate(insert[room - 1]) ? room - 1 : room;
            insert = insert[..cut];
        }

        _text = _text.Insert(_caret, insert);
        _caret += insert.Length;
        _lastActivity = Environment.TickCount64;
        TextChanged?.Invoke(this);
    }

    void Remove(int start, int end)
    {
        if (end <= start)
            return;
        _text = _text.Remove(start, end - start);
        _caret = start;
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
}
