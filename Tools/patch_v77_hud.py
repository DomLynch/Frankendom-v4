P=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\HudDraw.cs"
b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b; s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'v77 minor' in s: print('ALREADY'); raise SystemExit
a="            char ch = up[i]; int sz = (wordStart || big == small) ? big : small; wordStart = ch == ' ';"
assert s.count(a)==1
s=s.replace(a,"            char ch = up[i]; int sz = ((wordStart && !MinorWord(text, i)) || big == small) ? big : small; wordStart = ch == ' ';   // v77 minor: 'of' / 'the' keep the small cap size")
a2="    static CapsLayout Layout(string text, string kind, int big, int small, float tracking, bool bold)"
assert s.count(a2)==1
s=s.replace(a2,"    static bool MinorWord(string t, int i) { if (i <= 0) return false; foreach (var w in new[] { \"of \", \"the \", \"de \", \"von \" }) if (string.CompareOrdinal(t, i, w, 0, w.Length) == 0) return true; return false; }\n"+a2)
s=s.replace('\n','\r\n') if crlf else s; o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o; open(P,'wb').write(o); print('patched')
