namespace AngleSharp.Renderer.Skia.Svg;

using System.Globalization;

using SkiaSharp;

/// <summary>
/// Parses an SVG path `d` attribute into an <see cref="SKPath"/>. Supports the full set of path
/// commands (move, line, horizontal/vertical line, cubic and smooth cubic, quadratic and smooth
/// quadratic, elliptical arc, close), both absolute and relative forms.
/// </summary>
internal static class SvgPathDataParser
{
    public static SKPath Parse(string d)
    {
        var path = new SKPath();
        var cursor = new Cursor(d);

        var current = new SKPoint(0, 0);
        var subpathStart = new SKPoint(0, 0);
        var previousCommand = '\0';
        var previousCubicControl = new SKPoint(0, 0);
        var previousQuadraticControl = new SKPoint(0, 0);

        while (true)
        {
            cursor.SkipSeparators();

            if (cursor.AtEnd)
            {
                break;
            }

            var command = cursor.PeekCommandLetter() ?? previousCommand;

            if (cursor.PeekCommandLetter() is not null)
            {
                cursor.Advance();
            }
            else if (previousCommand is '\0' or 'Z' or 'z')
            {
                // No active command to implicitly repeat and no letter to read - malformed data.
                break;
            }

            var isRelative = char.IsLower(command);
            var upperCommand = char.ToUpperInvariant(command);

            switch (upperCommand)
            {
                case 'M':
                {
                    if (!cursor.TryReadNumber(out var x) || !cursor.TryReadNumber(out var y))
                    {
                        return path;
                    }

                    var point = isRelative ? Offset(current, x, y) : new SKPoint(x, y);
                    path.MoveTo(point);
                    current = point;
                    subpathStart = point;
                    // A subsequent coordinate pair without a new command letter is an implicit lineto.
                    command = isRelative ? 'l' : 'L';
                    break;
                }
                case 'L':
                {
                    if (!cursor.TryReadNumber(out var x) || !cursor.TryReadNumber(out var y))
                    {
                        return path;
                    }

                    var point = isRelative ? Offset(current, x, y) : new SKPoint(x, y);
                    path.LineTo(point);
                    current = point;
                    break;
                }
                case 'H':
                {
                    if (!cursor.TryReadNumber(out var x))
                    {
                        return path;
                    }

                    var point = isRelative ? new SKPoint(current.X + x, current.Y) : new SKPoint(x, current.Y);
                    path.LineTo(point);
                    current = point;
                    break;
                }
                case 'V':
                {
                    if (!cursor.TryReadNumber(out var y))
                    {
                        return path;
                    }

                    var point = isRelative ? new SKPoint(current.X, current.Y + y) : new SKPoint(current.X, y);
                    path.LineTo(point);
                    current = point;
                    break;
                }
                case 'C':
                {
                    if (!cursor.TryReadNumber(out var x1) || !cursor.TryReadNumber(out var y1) ||
                        !cursor.TryReadNumber(out var x2) || !cursor.TryReadNumber(out var y2) ||
                        !cursor.TryReadNumber(out var x) || !cursor.TryReadNumber(out var y))
                    {
                        return path;
                    }

                    var c1 = isRelative ? Offset(current, x1, y1) : new SKPoint(x1, y1);
                    var c2 = isRelative ? Offset(current, x2, y2) : new SKPoint(x2, y2);
                    var end = isRelative ? Offset(current, x, y) : new SKPoint(x, y);
                    path.CubicTo(c1, c2, end);
                    previousCubicControl = c2;
                    current = end;
                    break;
                }
                case 'S':
                {
                    if (!cursor.TryReadNumber(out var x2) || !cursor.TryReadNumber(out var y2) ||
                        !cursor.TryReadNumber(out var x) || !cursor.TryReadNumber(out var y))
                    {
                        return path;
                    }

                    var reflectedControl = char.ToUpperInvariant(previousCommand) is 'C' or 'S'
                        ? Reflect(previousCubicControl, current)
                        : current;
                    var c2 = isRelative ? Offset(current, x2, y2) : new SKPoint(x2, y2);
                    var end = isRelative ? Offset(current, x, y) : new SKPoint(x, y);
                    path.CubicTo(reflectedControl, c2, end);
                    previousCubicControl = c2;
                    current = end;
                    break;
                }
                case 'Q':
                {
                    if (!cursor.TryReadNumber(out var x1) || !cursor.TryReadNumber(out var y1) ||
                        !cursor.TryReadNumber(out var x) || !cursor.TryReadNumber(out var y))
                    {
                        return path;
                    }

                    var control = isRelative ? Offset(current, x1, y1) : new SKPoint(x1, y1);
                    var end = isRelative ? Offset(current, x, y) : new SKPoint(x, y);
                    path.QuadTo(control, end);
                    previousQuadraticControl = control;
                    current = end;
                    break;
                }
                case 'T':
                {
                    if (!cursor.TryReadNumber(out var x) || !cursor.TryReadNumber(out var y))
                    {
                        return path;
                    }

                    var reflectedControl = char.ToUpperInvariant(previousCommand) is 'Q' or 'T'
                        ? Reflect(previousQuadraticControl, current)
                        : current;
                    var end = isRelative ? Offset(current, x, y) : new SKPoint(x, y);
                    path.QuadTo(reflectedControl, end);
                    previousQuadraticControl = reflectedControl;
                    current = end;
                    break;
                }
                case 'A':
                {
                    if (!cursor.TryReadNumber(out var rx) || !cursor.TryReadNumber(out var ry) ||
                        !cursor.TryReadNumber(out var xAxisRotation) ||
                        !cursor.TryReadFlag(out var largeArc) ||
                        !cursor.TryReadFlag(out var sweep) ||
                        !cursor.TryReadNumber(out var x) || !cursor.TryReadNumber(out var y))
                    {
                        return path;
                    }

                    var end = isRelative ? Offset(current, x, y) : new SKPoint(x, y);
                    path.ArcTo(
                        rx,
                        ry,
                        xAxisRotation,
                        largeArc ? SKPathArcSize.Large : SKPathArcSize.Small,
                        sweep ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise,
                        end.X,
                        end.Y);
                    current = end;
                    break;
                }
                case 'Z':
                    path.Close();
                    current = subpathStart;
                    break;
                default:
                    // Unsupported/unknown command - stop parsing the remainder rather than guess.
                    return path;
            }

            previousCommand = command;
        }

        return path;
    }

    private static SKPoint Offset(SKPoint origin, float dx, float dy) => new(origin.X + dx, origin.Y + dy);

    private static SKPoint Reflect(SKPoint control, SKPoint around) => new((2 * around.X) - control.X, (2 * around.Y) - control.Y);

    private ref struct Cursor
    {
        private readonly string _text;
        private int _index;

        public Cursor(string text)
        {
            _text = text;
            _index = 0;
        }

        public readonly bool AtEnd => _index >= _text.Length;

        public void Advance() => _index++;

        public void SkipSeparators()
        {
            while (_index < _text.Length && (char.IsWhiteSpace(_text[_index]) || _text[_index] == ','))
            {
                _index++;
            }
        }

        public readonly char? PeekCommandLetter()
        {
            if (_index >= _text.Length)
            {
                return null;
            }

            var c = _text[_index];
            return "MmLlHhVvCcSsQqTtAaZz".IndexOf(c) >= 0 ? c : null;
        }

        public bool TryReadNumber(out float value)
        {
            SkipSeparators();
            value = 0f;

            var start = _index;

            if (_index < _text.Length && (_text[_index] == '+' || _text[_index] == '-'))
            {
                _index++;
            }

            var sawDigitOrDot = false;

            while (_index < _text.Length && char.IsAsciiDigit(_text[_index]))
            {
                _index++;
                sawDigitOrDot = true;
            }

            if (_index < _text.Length && _text[_index] == '.')
            {
                _index++;

                while (_index < _text.Length && char.IsAsciiDigit(_text[_index]))
                {
                    _index++;
                    sawDigitOrDot = true;
                }
            }

            if (!sawDigitOrDot)
            {
                _index = start;
                return false;
            }

            if (_index < _text.Length && (_text[_index] == 'e' || _text[_index] == 'E'))
            {
                var expStart = _index;
                _index++;

                if (_index < _text.Length && (_text[_index] == '+' || _text[_index] == '-'))
                {
                    _index++;
                }

                if (_index < _text.Length && char.IsAsciiDigit(_text[_index]))
                {
                    while (_index < _text.Length && char.IsAsciiDigit(_text[_index]))
                    {
                        _index++;
                    }
                }
                else
                {
                    _index = expStart;
                }
            }

            return float.TryParse(_text.AsSpan(start, _index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public bool TryReadFlag(out bool value)
        {
            SkipSeparators();
            value = false;

            if (_index >= _text.Length)
            {
                return false;
            }

            var c = _text[_index];

            if (c != '0' && c != '1')
            {
                return false;
            }

            value = c == '1';
            _index++;
            return true;
        }
    }
}
