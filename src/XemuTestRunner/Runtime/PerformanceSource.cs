using System.Security.Cryptography;
using System.Text;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

public sealed record PerformanceSourceReceipt(string Path, long Bytes, string Sha256, int Records, int IgnoredRecords);

/// <summary>One bounded, hashed read of an existing artifact. Used only after the target has stopped.</summary>
internal sealed class PerformanceSource : IDisposable
{
    private const int MaximumRecordCharacters = 65536;
    private readonly FileStream _file;
    private readonly SHA256 _hash;
    private readonly CryptoStream _hashed;
    private readonly StreamReader _reader;
    private readonly CancellationToken _cancellation;
    private readonly string _path;
    private readonly string _relative;
    private readonly long _length;
    private readonly DateTime _modified;
    private int _characters;
    private int _records;

    public PerformanceSource(string resultDirectory, string relative, CancellationToken cancellation)
    {
        _relative = relative;
        _path = new EvidenceCatalog(Path.GetDirectoryName(Path.GetFullPath(resultDirectory))!)
            .Resolve(Path.GetFileName(resultDirectory), relative);
        var info = new FileInfo(_path);
        if (!info.Exists) throw new FileNotFoundException("Analysis source is missing: " + relative);
        if (info.Length > 64L * 1024 * 1024) throw new InvalidDataException("Analysis source exceeds 64 MiB: " + relative);
        _length = info.Length;
        _modified = info.LastWriteTimeUtc;
        _file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        _hash = SHA256.Create();
        _hashed = new CryptoStream(_file, _hash, CryptoStreamMode.Read, leaveOpen: true);
        _reader = new StreamReader(_hashed, new UTF8Encoding(false, true), true, 16384, leaveOpen: true);
        _cancellation = cancellation;
    }

    private int Read()
    {
        if ((++_characters & 4095) == 0) _cancellation.ThrowIfCancellationRequested();
        return _reader.Read();
    }

    public IEnumerable<string> Lines()
    {
        var line = new StringBuilder();
        while (true)
        {
            var value = Read();
            if (value < 0)
            {
                if (line.Length > 0) { CountRecord(); yield return line.ToString().TrimEnd('\r'); }
                yield break;
            }
            if (value == '\n')
            {
                CountRecord();
                yield return line.ToString().TrimEnd('\r');
                line.Clear();
            }
            else
            {
                if (line.Length >= MaximumRecordCharacters) throw new InvalidDataException("Analysis line exceeds 64 KiB.");
                line.Append((char)value);
            }
        }
    }

    public IEnumerable<string[]> CsvRows()
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var afterQuote = false;
        var started = false;
        var recordCharacters = 0;
        while (true)
        {
            var value = Read();
            if (value < 0)
            {
                if (quoted) throw new InvalidDataException("CSV ends inside a quoted field.");
                if (started || field.Length > 0 || fields.Count > 0)
                { fields.Add(field.ToString()); CountRecord(); yield return fields.ToArray(); }
                yield break;
            }
            if (++recordCharacters > MaximumRecordCharacters) throw new InvalidDataException("CSV record exceeds 64 KiB.");
            var c = (char)value;
            if (quoted)
            {
                if (c == '"')
                {
                    if (_reader.Peek() == '"') { Read(); field.Append('"'); }
                    else { quoted = false; afterQuote = true; }
                }
                else field.Append(c);
                continue;
            }
            if (c == ',' || c == '\r' || c == '\n')
            {
                fields.Add(field.ToString()); field.Clear(); afterQuote = false; started = false;
                if (fields.Count > 128) throw new InvalidDataException("CSV has more than 128 columns.");
                if (c == ',') { started = true; continue; }
                if (c == '\r' && _reader.Peek() == '\n') Read();
                CountRecord(); yield return fields.ToArray(); fields.Clear(); recordCharacters = 0;
            }
            else if (c == '"' && !started && field.Length == 0) { quoted = true; started = true; }
            else
            {
                if (afterQuote || c == '"') throw new InvalidDataException("Malformed CSV quoting.");
                field.Append(c); started = true;
            }
        }
    }

    private void CountRecord()
    {
        if (++_records > 1000000) throw new InvalidDataException("Analysis source exceeds one million records.");
    }

    public PerformanceSourceReceipt Finish(int ignored = 0)
    {
        if (_reader.Peek() >= 0) throw new InvalidDataException("Analysis source was not completely read.");
        var info = new FileInfo(_path);
        if (_file.Length != _length || info.Length != _length || info.LastWriteTimeUtc != _modified)
            throw new InvalidDataException("Analysis source changed while reading.");
        return new(_relative, _length, Convert.ToHexString(_hash.Hash ?? throw new InvalidDataException("Source hash not finalized.")).ToLowerInvariant(), _records, ignored);
    }

    public void Dispose() { _reader.Dispose(); _hashed.Dispose(); _hash.Dispose(); _file.Dispose(); }
}
