using System;
using System.Windows.Forms;
using ScintillaNET;

namespace MarkMpn.Sql4Cds.XTB
{
    /// <summary>Vim-style editing for a single SQL query editor.</summary>
    internal sealed class VimEditor
    {
        private readonly Scintilla _editor;
        private readonly Action<string> _showMode;
        private readonly CaretStyle _originalCaretStyle;
        private readonly int _originalCaretWidth;
        private string _mode = "NORMAL";
        private char _pending;
        private char _pendingOperator;
        private int _visualAnchor;
        private string _register;
        private bool _lineRegister;
        private int _count;
        private int _operatorCount = 1;
        private int _visualPosition;
        private char _lastFind;
        private char _lastFindCharacter;

        public VimEditor(Scintilla editor, Action<string> showMode)
        {
            _editor = editor;
            _showMode = showMode;
            _originalCaretStyle = editor.CaretStyle;
            _originalCaretWidth = editor.CaretWidth;
            editor.KeyDown += OnKeyDown;
            SetEnabled(Settings.Instance.VimMode);
        }

        public void SetEnabled(bool enabled)
        {
            if (Enabled == enabled)
                return;

            Enabled = enabled;
            _pending = '\0';
            _count = 0;
            _mode = "NORMAL";
            _editor.SetSelection(_editor.CurrentPosition, _editor.CurrentPosition);
            if (enabled)
                UpdateCaret();
            else
            {
                _editor.CaretStyle = _originalCaretStyle;
                _editor.CaretWidth = _originalCaretWidth;
            }
            _showMode(enabled ? _mode : null);
        }

        public bool Enabled { get; private set; }

        public bool IsInsertMode => _mode == "INSERT";

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!Enabled)
                return;

            if (e.KeyCode == Keys.Escape)
            {
                if (_mode == "INSERT" && _editor.CurrentPosition > _editor.Lines[_editor.LineFromPosition(_editor.CurrentPosition)].Position)
                    Move(-1);
                SetMode("NORMAL");
                ResetCommand();
                Consume(e);
                return;
            }

            if (e.Control && !e.Alt && _mode != "INSERT")
            {
                if (e.KeyCode == Keys.R)
                {
                    _editor.Redo();
                    Consume(e);
                }
                else if (e.KeyCode == Keys.D || e.KeyCode == Keys.U)
                {
                    MoveLine((e.KeyCode == Keys.D ? 1 : -1) * Math.Max(1, _editor.LinesOnScreen / 2));
                    Consume(e);
                }
                return;
            }

            if (_mode == "INSERT")
                return;

            // Keep application shortcuts and function keys available in every mode.
            if (e.Control || e.Alt || e.KeyCode >= Keys.F1 && e.KeyCode <= Keys.F24)
                return;

            var key = KeyCharacter(e);
            if (key == '\0')
            {
                // Navigation keys and mouse selection can still be used.
                if (e.KeyCode >= Keys.Left && e.KeyCode <= Keys.Down || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
                    return;
                Consume(e);
                return;
            }

            Consume(e);

            if (_pending == 'r' || _pending == 'f' || _pending == 'F' || _pending == 't' || _pending == 'T')
            {
                if (_pending == 'r' && _editor.CurrentPosition < ContentEnd(_editor.Lines[_editor.LineFromPosition(_editor.CurrentPosition)]))
                {
                    var position = _editor.CurrentPosition;
                    var length = Math.Min(CountOrOne(), ContentEnd(_editor.Lines[_editor.LineFromPosition(position)]) - position);
                    _editor.BeginUndoAction();
                    try
                    {
                        _editor.DeleteRange(position, length);
                        _editor.InsertText(position, new String(key, length));
                    }
                    finally { _editor.EndUndoAction(); }
                }
                else if (_pending != 'r')
                {
                    _lastFind = _pending;
                    _lastFindCharacter = key;
                    FindCharacter(_pending, key, CountOrOne());
                }
                ResetCommand();
                return;
            }

            if (_pending != '\0')
            {
                var pending = _pending;
                if (pending == 'q')
                {
                    _pending = '\0';
                    if (key == 'g')
                        MotionOperation(_pendingOperator, 'g', Math.Min(10000, _operatorCount * CountOrOne()));
                    ResetCommand();
                    return;
                }
                if (Char.IsDigit(key) && (key != '0' || _count != 0) && pending != 'g')
                {
                    AddCount(key);
                    return;
                }

                _pending = '\0';
                if (pending == 'g' && key == 'g')
                    GoToLine(Math.Min((_count == 0 ? 1 : _count) - 1, _editor.Lines.Count - 1));
                else if ((pending == 'd' || pending == 'y' || pending == 'c') && key == 'g')
                {
                    _pending = 'q';
                    _pendingOperator = pending;
                    return;
                }
                else if ((pending == 'd' || pending == 'y' || pending == 'c') && key == pending)
                    LineOperation(pending, Math.Min(10000, _operatorCount * CountOrOne()));
                else if (pending == 'd' || pending == 'y' || pending == 'c')
                    MotionOperation(pending, key, Math.Min(10000, _operatorCount * CountOrOne()));
                ResetCommand();
                return;
            }

            if (Char.IsDigit(key) && (key != '0' || _count != 0))
            {
                AddCount(key);
                return;
            }

            if (key == 'g' || _mode == "NORMAL" && (key == 'd' || key == 'y' || key == 'c'))
            {
                _pending = key;
                if (key != 'g')
                {
                    _operatorCount = CountOrOne();
                    _count = 0;
                }
                return;
            }

            if ((_mode == "VISUAL" || _mode == "VISUAL LINE") && (key == 'd' || key == 'y' || key == 'c'))
            {
                var start = _editor.SelectionStart;
                _register = _editor.SelectedText;
                _lineRegister = _mode == "VISUAL LINE";
                if (key != 'y')
                    _editor.ReplaceSelection("");
                _editor.SetEmptySelection(Math.Min(start, _editor.TextLength));
                _mode = "NORMAL";
                SetMode(key == 'c' ? "INSERT" : "NORMAL");
                ResetCommand();
                return;
            }

            var count = CountOrOne();
            switch (key)
            {
                case 'h': Move(-count); break;
                case 'l': Move(count); break;
                case 'j': MoveLine(count); break;
                case 'k': MoveLine(-count); break;
                case 'w': MoveWord(count); break;
                case 'b': MoveWord(-count); break;
                case 'e': MoveWordEnd(count); break;
                case 'W': MoveWord(count, true); break;
                case 'B': MoveWord(-count, true); break;
                case 'E': MoveWordEnd(count, true); break;
                case '0': MoveToLineBoundary(false); break;
                case '$': MoveToLineBoundary(true); break;
                case '^': MoveToFirstNonBlank(); break;
                case 'G': GoToLine(_count == 0 ? _editor.Lines.Count - 1 : Math.Min(_count - 1, _editor.Lines.Count - 1)); break;
                case 'H': GoToVisibleLine(_editor.FirstVisibleLine); break;
                case 'M': GoToVisibleLine(_editor.FirstVisibleLine + _editor.LinesOnScreen / 2); break;
                case 'L': GoToVisibleLine(_editor.FirstVisibleLine + _editor.LinesOnScreen - 1); break;
                case '%': MatchBracket(); break;
                case 'i': SetMode("INSERT"); break;
                case 'a': SetPosition(Math.Min(ContentEnd(_editor.Lines[_editor.LineFromPosition(Position)]), Position + 1)); SetMode("INSERT"); break;
                case 'I': MoveToFirstNonBlank(); SetMode("INSERT"); break;
                case 'A': SetPosition(ContentEnd(_editor.Lines[_editor.LineFromPosition(Position)])); SetMode("INSERT"); break;
                case 'o': OpenLine(false); break;
                case 'O': OpenLine(true); break;
                case 'v': SetMode(_mode == "VISUAL" ? "NORMAL" : "VISUAL"); break;
                case 'V': SetMode(_mode == "VISUAL LINE" ? "NORMAL" : "VISUAL LINE"); break;
                case 'x': DeleteCharacters(count); break;
                case 'X': DeleteBefore(count); break;
                case 's': DeleteCharacters(count); SetMode("INSERT"); break;
                case 'p': Paste(false, count); break;
                case 'P': Paste(true, count); break;
                case 'u': for (var i = 0; i < count; i++) _editor.Undo(); break;
                case 'r': _pending = 'r'; return;
                case 'D': MotionOperation('d', '$', 1); break;
                case 'C': MotionOperation('c', '$', 1); break;
                case 'Y': LineOperation('y', count); break;
                case 'S': LineOperation('c', count); break;
                case 'f': case 'F': case 't': case 'T': _pending = key; return;
                case ';': if (_lastFind != '\0') FindCharacter(_lastFind, _lastFindCharacter, count); break;
                case ',': if (_lastFind != '\0') FindCharacter(ReverseFind(_lastFind), _lastFindCharacter, count); break;
                case 'J': JoinLines(count); break;
                case '~': ToggleCase(count); break;
            }
            ResetCommand();
        }

        private void ResetCommand()
        {
            _pending = '\0';
            _pendingOperator = '\0';
            _count = 0;
            _operatorCount = 1;
        }

        private int CountOrOne() => Math.Max(1, _count);

        private void AddCount(char key) => _count = Math.Min(10000, _count * 10 + key - '0');

        private static void Consume(KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private static char KeyCharacter(KeyEventArgs e)
        {
            if (e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
                return e.Shift ? (char)e.KeyCode : Char.ToLowerInvariant((char)e.KeyCode);
            if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9)
                return e.KeyCode == Keys.D4 && e.Shift ? '$' : e.KeyCode == Keys.D5 && e.Shift ? '%' : e.KeyCode == Keys.D6 && e.Shift ? '^' : e.KeyCode == Keys.D9 && e.Shift ? '(' : e.KeyCode == Keys.D0 && e.Shift ? ')' : e.Shift ? '\0' : (char)e.KeyCode;
            if (e.KeyCode == Keys.Space) return ' ';
            if (e.KeyCode == Keys.Oem1) return e.Shift ? ':' : ';';
            if (e.KeyCode == Keys.Oemcomma) return e.Shift ? '<' : ',';
            if (e.KeyCode == Keys.OemPeriod) return e.Shift ? '>' : '.';
            if (e.KeyCode == Keys.OemQuestion) return e.Shift ? '?' : '/';
            if (e.KeyCode == Keys.OemOpenBrackets) return e.Shift ? '{' : '[';
            if (e.KeyCode == Keys.OemCloseBrackets) return e.Shift ? '}' : ']';
            if (e.KeyCode == Keys.OemQuotes) return e.Shift ? '"' : '\'';
            if (e.KeyCode == Keys.OemMinus) return e.Shift ? '_' : '-';
            if (e.KeyCode == Keys.Oemplus) return e.Shift ? '+' : '=';
            if (e.KeyCode == Keys.OemPipe) return e.Shift ? '|' : '\\';
            if (e.KeyCode == Keys.Oem3 && e.Shift) return '~';
            return '\0';
        }

        private void SetMode(string mode)
        {
            if ((mode == "VISUAL" || mode == "VISUAL LINE") && _mode != "VISUAL" && _mode != "VISUAL LINE")
            {
                _visualAnchor = Position;
                _visualPosition = Position;
            }
            else if (_mode == "VISUAL" || _mode == "VISUAL LINE")
                _editor.SetSelection(_visualPosition, _visualPosition);

            _mode = mode;
            if (mode == "VISUAL" || mode == "VISUAL LINE")
                UpdateVisualSelection();
            else
                _editor.SetSelection(_editor.CurrentPosition, _editor.CurrentPosition);
            UpdateCaret();
            _showMode(mode);
        }

        private void UpdateCaret()
        {
            _editor.CaretStyle = _mode == "INSERT" ? CaretStyle.Line : CaretStyle.Block;
            _editor.CaretWidth = _mode == "INSERT" ? 2 : 1;
        }

        private int Position => _mode == "VISUAL" || _mode == "VISUAL LINE" ? _visualPosition : _editor.CurrentPosition;

        private void SetPosition(int position)
        {
            position = Math.Max(0, Math.Min(position, _editor.TextLength));
            if (_mode == "VISUAL" || _mode == "VISUAL LINE")
            {
                _visualPosition = position;
                UpdateVisualSelection();
            }
            else
                _editor.SetSelection(position, position);
            _editor.ScrollCaret();
        }

        private void UpdateVisualSelection()
        {
            if (_mode == "VISUAL LINE")
            {
                var first = Math.Min(_editor.LineFromPosition(_visualAnchor), _editor.LineFromPosition(_visualPosition));
                var last = Math.Max(_editor.LineFromPosition(_visualAnchor), _editor.LineFromPosition(_visualPosition));
                var line = _editor.Lines[last];
                var start = _editor.Lines[first].Position;
                var end = line.Position + line.Length;
                _editor.SetSelection(_visualPosition >= _visualAnchor ? end : start,
                    _visualPosition >= _visualAnchor ? start : end);
            }
            else
            {
                var start = Math.Min(_visualAnchor, _visualPosition);
                var end = Math.Min(_editor.TextLength, Math.Max(_visualAnchor, _visualPosition) + 1);
                _editor.SetSelection(_visualPosition >= _visualAnchor ? end : start,
                    _visualPosition >= _visualAnchor ? start : end);
            }
        }

        private void Move(int distance)
        {
            var line = _editor.Lines[_editor.LineFromPosition(Position)];
            SetPosition(Math.Max(line.Position, Math.Min(LastCharacterPosition(line), Position + distance)));
        }

        private void MoveLine(int distance)
        {
            var lineIndex = _editor.LineFromPosition(Position);
            var column = Position - _editor.Lines[lineIndex].Position;
            lineIndex = Math.Max(0, Math.Min(_editor.Lines.Count - 1, lineIndex + distance));
            var line = _editor.Lines[lineIndex];
            SetPosition(Math.Min(LastCharacterPosition(line), line.Position + column));
        }

        private void MoveWord(int direction, bool bigWord = false)
        {
            var position = Position;
            for (var step = 0; step < Math.Abs(direction); step++)
            {
                if (direction > 0)
                {
                    if (position < _editor.TextLength && IsWordAt(position, bigWord))
                        while (position < _editor.TextLength && IsWordAt(position, bigWord)) position++;
                    while (position < _editor.TextLength && !IsWordAt(position, bigWord)) position++;
                }
                else
                {
                    position = Math.Max(0, position - 1);
                    while (position > 0 && !IsWordAt(position, bigWord)) position--;
                    while (position > 0 && IsWordAt(position - 1, bigWord)) position--;
                }
            }
            SetPosition(position);
        }

        private void MoveWordEnd(int count, bool bigWord = false)
        {
            var position = Position;
            for (var step = 0; step < count; step++)
            {
                if (position < _editor.TextLength && IsWordAt(position, bigWord)) position++;
                while (position < _editor.TextLength && !IsWordAt(position, bigWord)) position++;
                while (position + 1 < _editor.TextLength && IsWordAt(position + 1, bigWord)) position++;
            }
            SetPosition(position);
        }

        private static bool IsWordCharacter(char character)
        {
            return Char.IsLetterOrDigit(character) || character == '_';
        }

        private bool IsWordAt(int position, bool bigWord)
        {
            var character = (char)_editor.GetCharAt(position);
            return bigWord ? !Char.IsWhiteSpace(character) : IsWordCharacter(character);
        }

        private void MoveToLineBoundary(bool end)
        {
            var line = _editor.Lines[_editor.LineFromPosition(Position)];
            SetPosition(end ? LastCharacterPosition(line) : line.Position);
        }

        private int ContentEnd(Line line)
        {
            var end = line.EndPosition;
            while (end > line.Position && (_editor.GetCharAt(end - 1) == '\r' || _editor.GetCharAt(end - 1) == '\n'))
                end--;
            return end;
        }

        private int LastCharacterPosition(Line line)
        {
            return Math.Max(line.Position, ContentEnd(line) - 1);
        }

        private void MoveToFirstNonBlank()
        {
            var line = _editor.Lines[_editor.LineFromPosition(Position)];
            var position = line.Position;
            while (position < ContentEnd(line) && (_editor.GetCharAt(position) == ' ' || _editor.GetCharAt(position) == '\t'))
                position++;
            SetPosition(position);
        }

        private void MatchBracket()
        {
            var line = _editor.Lines[_editor.LineFromPosition(Position)];
            var position = Position;
            while (position < ContentEnd(line) && "()[]{}".IndexOf((char)_editor.GetCharAt(position)) < 0)
                position++;
            if (position < ContentEnd(line))
            {
                var match = _editor.BraceMatch(position);
                if (match >= 0)
                    SetPosition(match);
            }
        }

        private void FindCharacter(char command, char character, int count)
        {
            var forward = command == 'f' || command == 't';
            var line = _editor.Lines[_editor.LineFromPosition(Position)];
            var position = Position;
            for (var n = 0; n < count; n++)
            {
                do { position += forward ? 1 : -1; }
                while ((forward ? position < ContentEnd(line) : position >= line.Position) && _editor.GetCharAt(position) != character);
                if (forward ? position >= ContentEnd(line) : position < line.Position)
                    return;
            }
            if (command == 't') position--;
            if (command == 'T') position++;
            SetPosition(position);
        }

        private static char ReverseFind(char command)
        {
            return command == 'f' ? 'F' : command == 'F' ? 'f' : command == 't' ? 'T' : 't';
        }

        private void JoinLines(int count)
        {
            _editor.BeginUndoAction();
            try
            {
                for (var n = 0; n < Math.Max(1, count - 1); n++)
                {
                    var line = _editor.Lines[_editor.LineFromPosition(Position)];
                    if (line.Index + 1 >= _editor.Lines.Count)
                        break;
                    var next = _editor.Lines[line.Index + 1];
                    var whitespace = 0;
                    while (next.Position + whitespace < ContentEnd(next) &&
                        (_editor.GetCharAt(next.Position + whitespace) == ' ' || _editor.GetCharAt(next.Position + whitespace) == '\t'))
                        whitespace++;
                    var end = ContentEnd(line);
                    _editor.DeleteRange(end, next.Position - end + whitespace);
                    _editor.InsertText(end, " ");
                }
            }
            finally { _editor.EndUndoAction(); }
        }

        private void ToggleCase(int count)
        {
            var position = Position;
            var end = Math.Min(ContentEnd(_editor.Lines[_editor.LineFromPosition(position)]), position + count);
            _editor.BeginUndoAction();
            try
            {
                for (var i = position; i < end; i++)
                {
                    var character = (char)_editor.GetCharAt(i);
                    var replacement = Char.IsUpper(character) ? Char.ToLowerInvariant(character) : Char.ToUpperInvariant(character);
                    if (replacement != character)
                    {
                        _editor.DeleteRange(i, 1);
                        _editor.InsertText(i, replacement.ToString());
                    }
                }
            }
            finally { _editor.EndUndoAction(); }
            SetPosition(end);
        }

        private void GoToLine(int index)
        {
            SetPosition(_editor.Lines[index].Position);
        }

        private void GoToVisibleLine(int index)
        {
            GoToLine(Math.Max(0, Math.Min(_editor.DocLineFromVisible(index), _editor.Lines.Count - 1)));
        }

        private void OpenLine(bool above)
        {
            var line = _editor.Lines[_editor.LineFromPosition(_editor.CurrentPosition)];
            var position = above ? line.Position : ContentEnd(line);
            var newline = _editor.EolMode == Eol.CrLf ? "\r\n" : _editor.EolMode == Eol.Cr ? "\r" : "\n";
            _editor.InsertText(position, newline);
            SetPosition(above ? position : position + newline.Length);
            SetMode("INSERT");
        }

        private void DeleteCharacters(int count)
        {
            var line = _editor.Lines[_editor.LineFromPosition(Position)];
            var start = Position;
            var length = Math.Min(count, ContentEnd(line) - start);
            if (length <= 0)
                return;
            _register = _editor.GetTextRange(start, length);
            _lineRegister = false;
            _editor.DeleteRange(start, length);
            SetPosition(Math.Min(start, _editor.TextLength));
        }

        private void MotionOperation(char operation, char motion, int count)
        {
            var start = _editor.CurrentPosition;
            var linewise = motion == 'j' || motion == 'k' || motion == 'G' || motion == 'g';
            var inclusive = motion == '$' || motion == 'e' || motion == 'E';
            var changeWord = operation == 'c' && (motion == 'w' || motion == 'W') &&
                start < _editor.TextLength && !Char.IsWhiteSpace((char)_editor.GetCharAt(start));
            if (changeWord)
            {
                if (count > 1)
                    MoveWord(count - 1, motion == 'W');

                var position = _editor.CurrentPosition;
                if (position < _editor.TextLength)
                {
                    var wordCharacter = IsWordCharacter((char)_editor.GetCharAt(position));
                    while (position < _editor.TextLength &&
                        (motion == 'W' ? !Char.IsWhiteSpace((char)_editor.GetCharAt(position)) :
                            !Char.IsWhiteSpace((char)_editor.GetCharAt(position)) &&
                            IsWordCharacter((char)_editor.GetCharAt(position)) == wordCharacter))
                        position++;
                }
                SetPosition(position);
            }
            else
            {
                switch (motion)
                {
                    case 'h': Move(-count); break;
                    case 'l': Move(count); break;
                    case 'j': MoveLine(count); break;
                    case 'k': MoveLine(-count); break;
                    case 'w': MoveWord(count); break;
                    case 'b': MoveWord(-count); break;
                    case 'e': MoveWordEnd(count); break;
                    case 'W': MoveWord(count, true); break;
                    case 'B': MoveWord(-count, true); break;
                    case 'E': MoveWordEnd(count, true); break;
                    case '0': MoveToLineBoundary(false); break;
                    case '^': MoveToFirstNonBlank(); break;
                    case '$': MoveToLineBoundary(true); break;
                    case 'G': GoToLine(_editor.Lines.Count - 1); break;
                    case 'g': GoToLine(0); break;
                    default: return;
                }
            }

            var target = _editor.CurrentPosition;
            var first = Math.Min(start, target);
            var last = Math.Max(start, target);
            if (linewise)
            {
                var firstLine = _editor.Lines[_editor.LineFromPosition(first)];
                var lastLine = _editor.Lines[_editor.LineFromPosition(last)];
                first = firstLine.Position;
                last = lastLine.Position + lastLine.Length;
            }
            else if (inclusive && last < _editor.TextLength)
                last++;

            if (first == last)
                return;

            _register = _editor.GetTextRange(first, last - first);
            _lineRegister = linewise;
            if (operation == 'y')
            {
                _editor.SetSelection(start, start);
                return;
            }

            _editor.DeleteRange(first, last - first);
            SetPosition(Math.Min(first, _editor.TextLength));
            if (operation == 'c')
                SetMode("INSERT");
        }

        private void LineOperation(char operation, int count)
        {
            var firstIndex = _editor.LineFromPosition(Position);
            var lastIndex = Math.Min(_editor.Lines.Count - 1, firstIndex + count - 1);
            var first = _editor.Lines[firstIndex].Position;
            var lastLine = _editor.Lines[lastIndex];
            var length = lastLine.Position + lastLine.Length - first;
            _register = _editor.GetTextRange(first, length);
            _lineRegister = true;
            if (operation != 'y')
            {
                var deleteStart = first;
                if (operation == 'd' && firstIndex > 0 && lastIndex == _editor.Lines.Count - 1 &&
                    ContentEnd(lastLine) == lastLine.EndPosition)
                    deleteStart = ContentEnd(_editor.Lines[firstIndex - 1]);
                _editor.DeleteRange(deleteStart, first + length - deleteStart);
                SetPosition(Math.Min(deleteStart, _editor.TextLength));
                if (operation == 'c')
                    SetMode("INSERT");
            }
        }

        private void DeleteBefore(int count)
        {
            var line = _editor.Lines[_editor.LineFromPosition(Position)];
            var start = Math.Max(line.Position, Position - count);
            if (start == Position)
                return;
            _register = _editor.GetTextRange(start, Position - start);
            _lineRegister = false;
            _editor.DeleteRange(start, Position - start);
            SetPosition(start);
        }

        private void Paste(bool before, int count)
        {
            if (String.IsNullOrEmpty(_register))
                return;
            var position = Position;
            var newline = _editor.EolMode == Eol.CrLf ? "\r\n" : _editor.EolMode == Eol.Cr ? "\r" : "\n";
            var unterminatedLine = _lineRegister && !_register.EndsWith("\n") && !_register.EndsWith("\r");
            var text = unterminatedLine
                ? String.Join(newline, System.Linq.Enumerable.Repeat(_register, count))
                : String.Concat(System.Linq.Enumerable.Repeat(_register, count));
            _editor.BeginUndoAction();
            try
            {
                if (_lineRegister)
                {
                    var line = _editor.Lines[_editor.LineFromPosition(position)];
                    position = before ? line.Position : line.Position + line.Length;
                    if (!before && !line.Text.EndsWith("\n") && !line.Text.EndsWith("\r"))
                    {
                        _editor.InsertText(position, newline);
                        position += newline.Length;
                    }
                    if (before && unterminatedLine)
                        text += newline;
                }
                else if (!before)
                    position = Math.Min(position + 1, _editor.TextLength);
                _editor.InsertText(position, text);
            }
            finally { _editor.EndUndoAction(); }
            SetPosition(position);
        }
    }
}
