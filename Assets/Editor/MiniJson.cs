// MiniJson (unity-build, for LodTexRepoint): tiny JSON reader, Editor only. Parse -> Dictionary<string, object> / List<object> / string / double / long / bool / null.
using System; using System.Collections.Generic; using System.Globalization; using System.Text;
public static class MiniJson
{
    public static object Parse(string s) { int i = 0; var v = Val(s, ref i); return v; }
    static void Ws(string s, ref int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r' || s[i] == '﻿')) i++; }
    static object Val(string s, ref int i)
    {
        Ws(s, ref i); if (i >= s.Length) throw new FormatException("MiniJson: unexpected end");
        char c = s[i];
        if (c == '{')
        {
            var d = new Dictionary<string, object>(); i++; Ws(s, ref i); if (s[i] == '}') { i++; return d; }
            while (true) { Ws(s, ref i); string k = Str(s, ref i); Ws(s, ref i); if (s[i++] != ':') throw new FormatException("MiniJson: ':' expected at " + i); d[k] = Val(s, ref i); Ws(s, ref i); if (s[i] == ',') { i++; continue; } if (s[i] == '}') { i++; return d; } throw new FormatException("MiniJson: ',' or '}' expected at " + i); }
        }
        if (c == '[')
        {
            var l = new List<object>(); i++; Ws(s, ref i); if (s[i] == ']') { i++; return l; }
            while (true) { l.Add(Val(s, ref i)); Ws(s, ref i); if (s[i] == ',') { i++; continue; } if (s[i] == ']') { i++; return l; } throw new FormatException("MiniJson: ',' or ']' expected at " + i); }
        }
        if (c == '"') return Str(s, ref i);
        if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
        if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
        if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
        int st = i; while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        string num = s.Substring(st, i - st); if (num.Length == 0) throw new FormatException("MiniJson: bad token at " + st);
        if (num.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lv)) return lv;
        return double.Parse(num, CultureInfo.InvariantCulture);
    }
    static string Str(string s, ref int i)
    {
        if (s[i] != '"') throw new FormatException("MiniJson: string expected at " + i);
        i++; var sb = new StringBuilder();
        while (true)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            char e = s[i++];
            switch (e) { case 'n': sb.Append('\n'); break; case 't': sb.Append('\t'); break; case 'r': sb.Append('\r'); break; case 'b': sb.Append('\b'); break; case 'f': sb.Append('\f'); break;
                case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break; default: sb.Append(e); break; }
        }
    }
}
