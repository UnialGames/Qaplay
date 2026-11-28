using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Qaplay
{
    /// <summary>
    /// 프로토콜 메시지를 읽고 쓰는 최소한의 JSON. 게임에 다른 패키지를 끌어오지 않으려고 직접 가지고 있다.
    /// 읽은 값은 Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, double, bool, null 중 하나다.
    /// </summary>
    public static class Json
    {
        // 바깥에서 온 글자를 읽으므로, 깊게 겹친 입력으로 스택이 넘치지 않게 막는다
        const int MaxDepth = 64;

        /// <summary>JSON 값 하나를 읽는다. 형식이 틀리면 FormatException.</summary>
        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("JSON is null");
            int i = 0;
            object value = Value(text, ref i, 0);
            SkipSpace(text, ref i);
            if (i != text.Length) throw Bad(i, "unexpected characters after the value");
            return value;
        }

        static object Value(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw Bad(i, "nested too deeply");
            SkipSpace(s, ref i);
            if (i >= s.Length) throw Bad(i, "unexpected end");
            char c = s[i];
            if (c == '{') return ReadObject(s, ref i, depth);
            if (c == '[') return ReadArray(s, ref i, depth);
            if (c == '"') return ReadString(s, ref i);
            if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            throw Bad(i, "unexpected character '" + c + "'");
        }

        static Dictionary<string, object> ReadObject(string s, ref int i, int depth)
        {
            var map = new Dictionary<string, object>();
            i++; // {
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return map; }
            while (true)
            {
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw Bad(i, "expected a string key");
                string key = ReadString(s, ref i);
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw Bad(i, "expected ':'");
                i++;
                map[key] = Value(s, ref i, depth + 1); // 같은 키가 두 번 나오면 뒤의 값
                SkipSpace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return map; }
                throw Bad(i, "expected ',' or '}'");
            }
        }

        static List<object> ReadArray(string s, ref int i, int depth)
        {
            var list = new List<object>();
            i++; // [
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(Value(s, ref i, depth + 1));
                SkipSpace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return list; }
                throw Bad(i, "expected ',' or ']'");
            }
        }

        static string ReadString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // 여는 따옴표
            while (true)
            {
                if (i >= s.Length) throw Bad(i, "unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c < ' ') throw Bad(i - 1, "control character in string");
                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) throw Bad(i, "unterminated string");
                char e = s[i++];
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
                        if (i + 4 > s.Length || !ushort.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier,
                                CultureInfo.InvariantCulture, out ushort code))
                            throw Bad(i, "bad \\u escape");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: throw Bad(i - 1, "bad escape '\\" + e + "'");
                }
            }
        }

        static double ReadNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            // 기기 로캘과 무관하게 소수점은 '.'
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double value) || double.IsInfinity(value))
                throw Bad(start, "bad number");
            return value;
        }

        static bool Word(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static FormatException Bad(int at, string what) => new FormatException("bad JSON at " + at + ": " + what);

        /// <summary>
        /// 값을 JSON으로 쓴다. 쓸 수 있는 것: null, 문자열, 불리언, 숫자(int, long, float, double),
        /// IDictionary&lt;string, object&gt;, 그리고 그 밖의 IEnumerable(배열로 쓴다).
        /// </summary>
        public static string Write(object value) => Write(value, false);

        /// <summary>
        /// indented이면 사람이 읽고 고치기 좋게 두 칸 들여 쓴다(설정 파일). 끝에 줄바꿈을 붙인다.
        /// 숫자나 글자만 든 배열은 한 줄로 쓴다.
        /// </summary>
        public static string Write(object value, bool indented)
        {
            var sb = new StringBuilder();
            Write(sb, value, indented ? 0 : -1);
            if (indented) sb.Append('\n');
            return sb.ToString();
        }

        // depth가 -1이면 한 줄로 쓴다
        static void Write(StringBuilder sb, object value, int depth)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case long n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                // float는 화면 좌표에 쓴다. 소수 넷째 자리까지면 충분하고, 계산 오차(0.8450001)가 드러나지 않는다
                case float f: WriteNumber(sb, Math.Round(f, 4), "0.####"); break;
                case double d: WriteNumber(sb, d, "R"); break;
                case IDictionary<string, object> map:
                {
                    if (map.Count == 0)
                    {
                        sb.Append("{}");
                        break;
                    }
                    sb.Append('{');
                    bool first = true;
                    foreach (KeyValuePair<string, object> pair in map)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        NewLine(sb, depth + 1, depth);
                        WriteString(sb, pair.Key);
                        sb.Append(depth < 0 ? ":" : ": ");
                        Write(sb, pair.Value, depth < 0 ? -1 : depth + 1);
                    }
                    NewLine(sb, depth, depth);
                    sb.Append('}');
                    break;
                }
                case IEnumerable list:
                {
                    // 글자와 숫자만 든 배열은 들여 써도 한 줄이다: ["a", "b"]
                    bool flat = depth < 0;
                    if (!flat)
                    {
                        flat = true;
                        foreach (object item in list)
                            if (item is IDictionary<string, object> || item is IEnumerable && !(item is string)) flat = false;
                    }
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in list)
                    {
                        if (!first) sb.Append(depth >= 0 && flat ? ", " : ",");
                        first = false;
                        if (!flat) NewLine(sb, depth + 1, depth);
                        Write(sb, item, depth < 0 ? -1 : depth + 1);
                    }
                    if (!flat && !first) NewLine(sb, depth, depth);
                    sb.Append(']');
                    break;
                }
                default: throw new ArgumentException("cannot write " + value.GetType().Name + " as JSON");
            }
        }

        static void NewLine(StringBuilder sb, int indent, int depth)
        {
            if (depth < 0) return;
            sb.Append('\n').Append(' ', indent * 2);
        }

        // JSON에는 NaN과 무한대가 없다. null로 쓴다
        static void WriteNumber(StringBuilder sb, double v, string format) =>
            sb.Append(double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString(format, CultureInfo.InvariantCulture));

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
