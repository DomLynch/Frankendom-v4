using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// LANE GEAR2 v79-80. A tiny JSON reader for the ONE thing JsonUtility cannot do: objects with free keys (gear.json "materials.byName" and items[].materials, keyed by material name).
// Everything else in gear.json is still read by JsonUtility (GearTable.Load). Plain C# (no UnityEngine), so it can be unit-tested outside Unity.
// Result types: Dictionary<string, object> (object), List<object> (array), string, double, bool, null. NEVER throws: Parse returns null on any syntax problem (and reports it in error).
public static class GearJson
{
    public static string LastError = "";

    public static object Parse(string text)
    {
        LastError = "";
        if (string.IsNullOrEmpty(text)) { LastError = "empty"; return null; }
        try
        {
            int i = 0; if (text.Length > 0 && text[0] == '﻿') i = 1;
            object v = Value(text, ref i); Skip(text, ref i);
            if (i < text.Length) { LastError = "trailing text at " + i; return null; }
            return v;
        }
        catch (Exception e) { LastError = e.Message; return null; }
    }

    // typed helpers (null / default when the shape differs)
    public static Dictionary<string, object> Obj(object o, string key)
    {
        var d = o as Dictionary<string, object>; if (d == null) return null;
        object v; return d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
    }
    public static List<object> Arr(object o, string key)
    {
        var d = o as Dictionary<string, object>; if (d == null) return null;
        object v; return d.TryGetValue(key, out v) ? v as List<object> : null;
    }
    public static string Str(Dictionary<string, object> d, string key, string def = "")
    {
        object v; if (d != null && d.TryGetValue(key, out v) && v is string) return (string)v; return def;
    }
    public static float Num(Dictionary<string, object> d, string key, float def)
    {
        object v; if (d != null && d.TryGetValue(key, out v) && v is double) return (float)(double)v; return def;
    }

    static void Skip(string s, ref int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++; }

    static object Value(string s, ref int i)
    {
        Skip(s, ref i); if (i >= s.Length) throw new FormatException("unexpected end");
        char c = s[i];
        if (c == '{')
        {
            var d = new Dictionary<string, object>(); i++; Skip(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                Skip(s, ref i); if (i >= s.Length || s[i] != '"') throw new FormatException("key expected at " + i);
                string k = Str(s, ref i); Skip(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("':' expected at " + i);
                i++; d[k] = Value(s, ref i); Skip(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end in object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("',' or '}' expected at " + i);
            }
        }
        if (c == '[')
        {
            var l = new List<object>(); i++; Skip(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(Value(s, ref i)); Skip(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end in array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("',' or ']' expected at " + i);
            }
        }
        if (c == '"') return Str(s, ref i);
        if (Lit(s, ref i, "true")) return true;
        if (Lit(s, ref i, "false")) return false;
        if (Lit(s, ref i, "null")) return null;
        int st = i;
        while (i < s.Length && ("+-0123456789.eE".IndexOf(s[i]) >= 0)) i++;
        double n;
        if (i == st || !double.TryParse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) throw new FormatException("bad value at " + st);
        return n;
    }

    static bool Lit(string s, ref int i, string w)
    {
        if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
        i += w.Length; return true;
    }

    static string Str(string s, ref int i)
    {
        var sb = new StringBuilder(); i++;   // opening quote
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) break;
            char e = s[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                    sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i += 4; break;
                default: sb.Append(e); break;   // " \ /
            }
        }
        throw new FormatException("unterminated string");
    }
}
