using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Multiplayer.Core.Util
{
    /// <summary>
    /// A small JSON reader, enough for the Paradox Mods API answers. Objects become
    /// Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double, plus string, bool and null.
    /// Kept dependency-free because the same sources compile into the game DLL and the server window.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string text)
        {
            var reader = new Reader(text ?? string.Empty);
            reader.SkipWhitespace();
            object value = reader.ReadValue();
            reader.SkipWhitespace();
            if (!reader.AtEnd)
            {
                throw new FormatException("Unexpected trailing characters at " + reader.Position);
            }

            return value;
        }

        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static List<object> AsArray(object value)
        {
            return value as List<object>;
        }

        /// <summary>Property of an object, or null when the value is not an object or lacks the key.</summary>
        public static object Get(object value, string key)
        {
            Dictionary<string, object> obj = AsObject(value);
            return obj != null && obj.TryGetValue(key, out object result) ? result : null;
        }

        public static string GetString(object value, string key, string fallback = "")
        {
            object item = Get(value, key);
            if (item == null)
            {
                return fallback;
            }

            if (item is string s)
            {
                return s;
            }

            if (item is double d)
            {
                return d.ToString(CultureInfo.InvariantCulture);
            }

            if (item is bool b)
            {
                return b ? "true" : "false";
            }

            return fallback;
        }

        public static int GetInt(object value, string key, int fallback = 0)
        {
            object item = Get(value, key);
            if (item is double d)
            {
                return (int)Math.Round(d);
            }

            if (item is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }

            return fallback;
        }

        public static bool GetBool(object value, string key, bool fallback = false)
        {
            object item = Get(value, key);
            return item is bool b ? b : fallback;
        }

        private sealed class Reader
        {
            private readonly string _text;
            private int _index;

            public Reader(string text)
            {
                _text = text;
            }

            public int Position => _index;

            public bool AtEnd => _index >= _text.Length;

            public void SkipWhitespace()
            {
                while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                {
                    _index++;
                }
            }

            public object ReadValue()
            {
                if (AtEnd)
                {
                    throw new FormatException("Unexpected end of JSON");
                }

                char c = _text[_index];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ReadNumber();
                        }

                        throw new FormatException("Unexpected character '" + c + "' at " + _index);
                }
            }

            private Dictionary<string, object> ReadObject()
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                _index++; // {
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _index++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                    {
                        throw new FormatException("Expected a property name at " + _index);
                    }

                    string key = ReadString();
                    SkipWhitespace();
                    if (Peek() != ':')
                    {
                        throw new FormatException("Expected ':' at " + _index);
                    }

                    _index++;
                    SkipWhitespace();
                    result[key] = ReadValue();
                    SkipWhitespace();
                    char next = Peek();
                    if (next == ',')
                    {
                        _index++;
                        continue;
                    }

                    if (next == '}')
                    {
                        _index++;
                        return result;
                    }

                    throw new FormatException("Expected ',' or '}' at " + _index);
                }
            }

            private List<object> ReadArray()
            {
                var result = new List<object>();
                _index++; // [
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _index++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    result.Add(ReadValue());
                    SkipWhitespace();
                    char next = Peek();
                    if (next == ',')
                    {
                        _index++;
                        continue;
                    }

                    if (next == ']')
                    {
                        _index++;
                        return result;
                    }

                    throw new FormatException("Expected ',' or ']' at " + _index);
                }
            }

            private string ReadString()
            {
                _index++; // opening quote
                var builder = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                    {
                        throw new FormatException("Unterminated string");
                    }

                    char c = _text[_index++];
                    if (c == '"')
                    {
                        return builder.ToString();
                    }

                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }

                    if (AtEnd)
                    {
                        throw new FormatException("Unterminated escape");
                    }

                    char e = _text[_index++];
                    switch (e)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (_index + 4 > _text.Length)
                            {
                                throw new FormatException("Bad unicode escape");
                            }

                            builder.Append((char)int.Parse(_text.Substring(_index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _index += 4;
                            break;
                        default:
                            throw new FormatException("Bad escape '\\" + e + "'");
                    }
                }
            }

            private double ReadNumber()
            {
                int start = _index;
                while (!AtEnd)
                {
                    char c = _text[_index];
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                    {
                        _index++;
                    }
                    else
                    {
                        break;
                    }
                }

                string token = _text.Substring(start, _index - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    throw new FormatException("Bad number '" + token + "'");
                }

                return value;
            }

            private char Peek()
            {
                return AtEnd ? '\0' : _text[_index];
            }

            private void Expect(string literal)
            {
                if (string.CompareOrdinal(_text, _index, literal, 0, literal.Length) != 0)
                {
                    throw new FormatException("Expected '" + literal + "' at " + _index);
                }

                _index += literal.Length;
            }
        }
    }
}
