using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Apus.AssetPort
{
    /// <summary>
    /// Minimal JSON reader/writer. JsonUtility cannot express dictionaries (presigned headers)
    /// or a JSON <c>null</c> string (manifest gitCommit), so the package uses this instead.
    /// Objects parse to <see cref="Dictionary{TKey,TValue}"/>, arrays to <see cref="List{T}"/>,
    /// numbers to <see cref="double"/>.
    /// </summary>
    internal static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var p = new Parser(text);
            p.SkipWs();
            var value = p.ReadValue();
            p.SkipWs();
            if (!p.End) throw new FormatException("Unexpected trailing characters in JSON at " + p.Pos);
            return value;
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        public static string Quote(string s)
        {
            var sb = new StringBuilder();
            WriteString(sb, s);
            return sb.ToString();
        }

        // ---- typed accessors for parsed objects ------------------------------------------

        public static Dictionary<string, object> AsObject(object o) => o as Dictionary<string, object>;

        public static List<object> AsArray(object o) => o as List<object>;

        public static string GetString(Dictionary<string, object> o, string key)
        {
            return o != null && o.TryGetValue(key, out var v) ? v as string : null;
        }

        /// <summary>Integer value (JSON number without fraction, within ±2^53); anything else returns <paramref name="fallback"/>.</summary>
        public static long GetLong(Dictionary<string, object> o, string key, long fallback = 0)
        {
            if (o != null && o.TryGetValue(key, out var v) && v is double d &&
                !double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d && Math.Abs(d) <= 9007199254740992d)
                return (long)d;
            return fallback;
        }

        public static List<string> GetStringList(Dictionary<string, object> o, string key)
        {
            var result = new List<string>();
            if (o != null && o.TryGetValue(key, out var v) && v is List<object> list)
                foreach (var item in list)
                    if (item is string s) result.Add(s);
            return result;
        }

        // ---- writer ---------------------------------------------------------------------

        static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case int _:
                case long _:
                case uint _:
                case ulong _:
                case short _:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case float f:
                    sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case IDictionary dict:
                {
                    sb.Append('{');
                    var first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        Write(sb, e.Value);
                    }
                    sb.Append('}');
                    break;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    var first = true;
                    foreach (var item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    break;
                }
                default:
                    throw new ArgumentException("Cannot serialize " + value.GetType());
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            if (s == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- parser ---------------------------------------------------------------------

        sealed class Parser
        {
            readonly string m_Text;
            int m_Pos;

            public Parser(string text) { m_Text = text; }

            public bool End => m_Pos >= m_Text.Length;
            public int Pos => m_Pos;

            public void SkipWs()
            {
                // JSON whitespace only (RFC 8259): space, tab, LF, CR.
                while (m_Pos < m_Text.Length)
                {
                    var c = m_Text[m_Pos];
                    if (c != ' ' && c != '\t' && c != '\n' && c != '\r') break;
                    m_Pos++;
                }
            }

            public object ReadValue()
            {
                if (End) throw Error("Unexpected end of JSON");
                var c = m_Text[m_Pos];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Error("Unexpected character '" + c + "'");
                }
            }

            Dictionary<string, object> ReadObject()
            {
                var result = new Dictionary<string, object>();
                m_Pos++;
                SkipWs();
                if (Peek('}')) { m_Pos++; return result; }
                while (true)
                {
                    SkipWs();
                    if (!Peek('"')) throw Error("Expected property name");
                    var key = ReadString();
                    SkipWs();
                    if (!Peek(':')) throw Error("Expected ':'");
                    m_Pos++;
                    SkipWs();
                    result[key] = ReadValue();
                    SkipWs();
                    if (Peek(',')) { m_Pos++; continue; }
                    if (Peek('}')) { m_Pos++; return result; }
                    throw Error("Expected ',' or '}'");
                }
            }

            List<object> ReadArray()
            {
                var result = new List<object>();
                m_Pos++;
                SkipWs();
                if (Peek(']')) { m_Pos++; return result; }
                while (true)
                {
                    SkipWs();
                    result.Add(ReadValue());
                    SkipWs();
                    if (Peek(',')) { m_Pos++; continue; }
                    if (Peek(']')) { m_Pos++; return result; }
                    throw Error("Expected ',' or ']'");
                }
            }

            string ReadString()
            {
                m_Pos++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (End) throw Error("Unterminated string");
                    var c = m_Text[m_Pos++];
                    if (c == '"') return sb.ToString();
                    if (c < 0x20) throw Error("Control character in string");
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw Error("Unterminated escape");
                    var e = m_Text[m_Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (m_Pos + 4 > m_Text.Length) throw Error("Bad unicode escape");
                            var code = 0;
                            for (var k = 0; k < 4; k++)
                            {
                                var h = HexValue(m_Text[m_Pos + k]);
                                if (h < 0) throw Error("Bad unicode escape");
                                code = (code << 4) | h;
                            }
                            sb.Append((char)code);
                            m_Pos += 4;
                            break;
                        default: throw Error("Bad escape '\\" + e + "'");
                    }
                }
            }

            static int HexValue(char c)
            {
                if (c >= '0' && c <= '9') return c - '0';
                if (c >= 'a' && c <= 'f') return c - 'a' + 10;
                if (c >= 'A' && c <= 'F') return c - 'A' + 10;
                return -1;
            }

            double ReadNumber()
            {
                var start = m_Pos;
                while (m_Pos < m_Text.Length && "+-0123456789.eE".IndexOf(m_Text[m_Pos]) >= 0) m_Pos++;
                var s = m_Text.Substring(start, m_Pos - start);
                if (!k_Number.IsMatch(s) || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw Error("Bad number '" + s + "'");
                return d;
            }

            // RFC 8259 number grammar (no leading '+', no leading zeros, digits on both sides of '.').
            static readonly System.Text.RegularExpressions.Regex k_Number =
                new System.Text.RegularExpressions.Regex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            void Expect(string word)
            {
                if (string.CompareOrdinal(m_Text, m_Pos, word, 0, word.Length) != 0) throw Error("Expected '" + word + "'");
                m_Pos += word.Length;
            }

            bool Peek(char c) => m_Pos < m_Text.Length && m_Text[m_Pos] == c;

            FormatException Error(string message) => new FormatException(message + " (JSON position " + m_Pos + ")");
        }
    }
}
