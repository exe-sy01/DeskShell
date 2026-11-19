using System.IO.Enumeration;
using System.Text;

namespace DeskShell.Search;

/// <summary>Which kinds of entries a search shows.</summary>
public enum SearchKind { All, Files, Folders }

/// <summary>
/// What you typed, compiled into a fast test on index entries. Words separated by spaces must all
/// appear in the name (any order, any case). Also:
/// "two words" (a phrase), !word (must not appear), a|b (either), *.log or report?.doc (wildcards
/// against the whole name), ext:jpg;png, file: / folder:, and anything with a \ is matched
/// against the whole path (e.g. "projects\ readme").
/// </summary>
public sealed class SearchQuery
{
    readonly List<Term> _terms = new();

    public SearchQuery(string text, SearchKind kind)
    {
        Text = text;
        Kind = kind;
        foreach (var word in Split(text))
        {
            var lower = word.ToLowerInvariant();
            if (lower == "file:") { Kind = SearchKind.Files; continue; }
            if (lower is "folder:" or "dir:") { Kind = SearchKind.Folders; continue; }
            var term = Parse(word);
            if (term != null) _terms.Add(term);
        }
        // Cheap tests first; path tests (which build the path) last.
        _terms.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        Plain = _terms.All(t => t is Contains) && !text.Contains('"');
    }

    public string Text { get; }
    public SearchKind Kind { get; }
    /// <summary>Nothing to search for (no words and no filters).</summary>
    public bool IsEmpty => _terms.Count == 0;
    /// <summary>Only plain words: typing more can only narrow the results.</summary>
    public bool Plain { get; }

    /// <summary>True if this query can only find a subset of <paramref name="previous"/>'s results (so those are enough to search).</summary>
    public bool Narrows(SearchQuery? previous) =>
        previous != null && Plain && previous.Plain && !previous.IsEmpty && Kind == previous.Kind &&
        Text.StartsWith(previous.Text, StringComparison.OrdinalIgnoreCase);

    public bool Matches(VolumeIndex volume, int slot)
    {
        var flags = volume.Flags(slot);
        if ((flags & VolumeIndex.Recycled) != 0) return false;
        if (Kind == SearchKind.Files && (flags & VolumeIndex.IsDir) != 0) return false;
        if (Kind == SearchKind.Folders && (flags & VolumeIndex.IsDir) == 0) return false;
        var name = volume.NameBytes(slot);
        if (name.Length == 0) return false; // the drive's root
        foreach (var term in _terms)
            if (!term.Test(volume, slot, name)) return false;
        return true;
    }

    static IEnumerable<string> Split(string text)
    {
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    static Term? Parse(string word)
    {
        if (word.StartsWith('!') && word.Length > 1)
            return Parse(word[1..]) is { } inner ? new Not(inner) : null;
        if (word.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
        {
            var exts = word[4..].Split(';', ',', '|').Select(e => e.Trim().TrimStart('*').TrimStart('.')).Where(e => e.Length > 0).ToArray();
            return exts.Length == 0 ? null : new Extension(exts);
        }
        if (word.Contains('|'))
        {
            var options = word.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(Parse).OfType<Term>().ToArray();
            return options.Length == 0 ? null : new AnyOf(options);
        }
        if (word.Contains('\\') || word.Contains('/'))
            return new PathTerm(word.Replace('/', '\\'));
        if (word.Contains('*') || word.Contains('?'))
        {
            // The common shapes have faster tests: *.ext, *text*.
            var inner = word.Trim('*');
            if (word.StartsWith("*.") && word.LastIndexOfAny(['*', '?', '.']) == 1 && word.Length > 2) return new Extension([word[2..]]);
            if (word.Length > 2 && word[0] == '*' && word[^1] == '*' && inner.IndexOfAny(['*', '?']) < 0) return new Contains(inner);
            return new Wildcard(word);
        }
        return new Contains(word);
    }

    // ---------- terms ----------

    abstract class Term
    {
        public virtual int Cost => 1;
        public abstract bool Test(VolumeIndex volume, int slot, ReadOnlySpan<byte> name);
    }

    /// <summary>The name contains the text, ignoring case. ASCII text is matched on the UTF-8 bytes directly.</summary>
    sealed class Contains : Term
    {
        readonly byte[]? _ascii;      // lower-case
        readonly byte _first, _firstUpper;
        readonly string _text;

        public Contains(string text)
        {
            _text = text;
            if (text.All(c => c < 128))
            {
                _ascii = Encoding.ASCII.GetBytes(text.ToLowerInvariant());
                _first = _ascii[0];
                _firstUpper = _first is >= (byte)'a' and <= (byte)'z' ? (byte)(_first - 32) : _first;
            }
        }

        public override int Cost => _ascii != null ? 1 : 3;

        public override bool Test(VolumeIndex volume, int slot, ReadOnlySpan<byte> name) =>
            _ascii != null ? ContainsAscii(name, _ascii, _first, _firstUpper)
                // Text with letters beyond ASCII can only be in names that have some too (most don't).
                : name.IndexOfAnyInRange((byte)0x80, (byte)0xFF) >= 0 && Decode(name, n => n.Contains(_text, StringComparison.OrdinalIgnoreCase));
    }

    static bool ContainsAscii(ReadOnlySpan<byte> name, byte[] needle, byte first, byte firstUpper)
    {
        var last = name.Length - needle.Length;
        var i = 0;
        while (i <= last)
        {
            var found = name.Slice(i, last - i + 1).IndexOfAny(first, firstUpper);
            if (found < 0) return false;
            i += found;
            var k = 1;
            while (k < needle.Length && Lower(name[i + k]) == needle[k]) k++;
            if (k == needle.Length) return true;
            i++;
        }
        return false;
    }

    static byte Lower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    /// <summary>A wildcard pattern against the whole name.</summary>
    sealed class Wildcard : Term
    {
        readonly string _pattern;
        readonly Contains[] _literals; // the plain parts must all appear: a fast test before the real one

        public Wildcard(string pattern)
        {
            _pattern = pattern;
            _literals = pattern.Split('*', '?').Where(p => p.Length > 0).Select(p => new Contains(p)).ToArray();
        }

        public override int Cost => 2;

        public override bool Test(VolumeIndex volume, int slot, ReadOnlySpan<byte> name)
        {
            foreach (var literal in _literals)
                if (!literal.Test(volume, slot, name)) return false;
            return Decode(name, n => FileSystemName.MatchesSimpleExpression(_pattern, n, ignoreCase: true));
        }
    }

    /// <summary>The name ends with one of these extensions.</summary>
    sealed class Extension : Term
    {
        readonly byte[][] _endings;

        public Extension(string[] extensions) =>
            _endings = extensions.Select(e => Encoding.UTF8.GetBytes("." + e.ToLowerInvariant())).ToArray();

        public override bool Test(VolumeIndex volume, int slot, ReadOnlySpan<byte> name)
        {
            foreach (var ending in _endings)
            {
                if (name.Length <= ending.Length) continue;
                var tail = name[^ending.Length..];
                var same = true;
                for (var i = 0; i < ending.Length && same; i++) same = Lower(tail[i]) == ending[i];
                if (same) return true;
            }
            return false;
        }
    }

    /// <summary>Text (or a wildcard pattern) matched against the full path.</summary>
    sealed class PathTerm(string text) : Term
    {
        readonly bool _wild = text.Contains('*') || text.Contains('?');
        public override int Cost => 10;

        public override bool Test(VolumeIndex volume, int slot, ReadOnlySpan<byte> name)
        {
            Span<char> buffer = stackalloc char[2048];
            var length = volume.WritePath(slot, buffer);
            if (length < 0) return false;
            var path = buffer[..length];
            return _wild ? FileSystemName.MatchesSimpleExpression(text.AsSpan().TrimStart('\\'), path, ignoreCase: true)
                : path.Contains(text, StringComparison.OrdinalIgnoreCase);
        }
    }

    sealed class Not(Term inner) : Term
    {
        public override int Cost => inner.Cost + 1;
        public override bool Test(VolumeIndex volume, int slot, ReadOnlySpan<byte> name) => !inner.Test(volume, slot, name);
    }

    sealed class AnyOf(Term[] options) : Term
    {
        public override int Cost => options.Max(o => o.Cost) + 1;

        public override bool Test(VolumeIndex volume, int slot, ReadOnlySpan<byte> name)
        {
            foreach (var option in options)
                if (option.Test(volume, slot, name)) return true;
            return false;
        }
    }

    delegate bool CharTest(ReadOnlySpan<char> name);

    static bool Decode(ReadOnlySpan<byte> name, CharTest test)
    {
        Span<char> chars = stackalloc char[name.Length];
        var count = Encoding.UTF8.GetChars(name, chars);
        return test(chars[..count]);
    }
}
