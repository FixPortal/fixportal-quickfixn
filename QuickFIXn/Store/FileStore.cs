using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using QuickFix.ObjectPooling;
using QuickFix.Util;

namespace QuickFix.Store;

/// <summary>
/// File store implementation
/// </summary>
public class FileStore : IMessageStore
{
    private readonly struct MsgDef(long index, int size)
    {
        public long Index { get; } = index;

        public int Size { get; } = size;
    }

    private readonly string _seqNumsFileName;
    private readonly string _seqNumsShadowFileName;
    private readonly string _msgFileName;
    private readonly string _headerFileName;
    private readonly string _sessionFileName;

    private System.IO.FileStream _seqNumsFile;
    private System.IO.FileStream _seqNumsShadowFile;
    private System.IO.FileStream _msgFile;
    private System.IO.Stream _headerFile;

    private readonly MemoryStore _cache = new();

    private readonly Dictionary<SeqNumType, MsgDef> _offsets = new();

    public static string Prefix(SessionID sessionId)
    {
        using PooledStringBuilder pooledSb = new PooledStringBuilder();
        StringBuilder prefix = pooledSb.Builder.Append(sessionId.BeginString)
            .Append('-').Append(sessionId.SenderCompID);
        if (SessionID.IsSet(sessionId.SenderSubID))
            prefix.Append('_').Append(sessionId.SenderSubID);
        if (SessionID.IsSet(sessionId.SenderLocationID))
            prefix.Append('_').Append(sessionId.SenderLocationID);
        prefix.Append('-').Append(sessionId.TargetCompID);
        if (SessionID.IsSet(sessionId.TargetSubID))
            prefix.Append('_').Append(sessionId.TargetSubID);
        if (SessionID.IsSet(sessionId.TargetLocationID))
            prefix.Append('_').Append(sessionId.TargetLocationID);

        if (SessionID.IsSet(sessionId.SessionQualifier))
            prefix.Append('-').Append(sessionId.SessionQualifier);

        return prefix.ToString();
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="path">
    /// All back or forward slashes in this path will be converted as needed to the running platform's preferred
    /// path separator (i.e. "/" will become "\" on windows, else "\" will become "/" on all other platforms)
    /// </param>
    /// <param name="sessionId"></param>
    public FileStore(string path, SessionID sessionId)
    {
        // FP Enhancement: 2026-05-24 — normalise the message-store directory via ParsePath so a `.\` prefix resolves against the app base directory.
        string normalizedPath = Enhancements.Utility.ParsePath(StringUtil.FixSlashes(path));

        if (!System.IO.Directory.Exists(normalizedPath))
            System.IO.Directory.CreateDirectory(normalizedPath);

        string prefix = Prefix(sessionId);

        _seqNumsFileName = System.IO.Path.Combine(normalizedPath, prefix + ".seqnums");
        _seqNumsShadowFileName = _seqNumsFileName + ".shadow";
        _msgFileName = System.IO.Path.Combine(normalizedPath, prefix + ".body");
        _headerFileName = System.IO.Path.Combine(normalizedPath, prefix + ".header");
        _sessionFileName = System.IO.Path.Combine(normalizedPath, prefix + ".session");

        // The compiler isn't smart enough to see that Open() initializes these 3 vars,
        // but we can use "= null!" to make it accept that these are non-null
        _seqNumsFile = null!;
        _seqNumsShadowFile = null!;
        _msgFile = null!;
        _headerFile = null!;
        Open();
    }

    private void Open()
    {
        Close();

        ConstructFromFileCache();
        InitializeSessionCreateTime();

        // FP Enhancement: 2026-09-27 — unbuffered, like the header: a buffered stream keeps the
        // bytes of a failed write and emits them later.
        _seqNumsFile = new System.IO.FileStream(_seqNumsFileName, System.IO.FileMode.OpenOrCreate,
            System.IO.FileAccess.ReadWrite, System.IO.FileShare.Read, bufferSize: 0);
        _seqNumsShadowFile = new System.IO.FileStream(_seqNumsShadowFileName, System.IO.FileMode.OpenOrCreate,
            System.IO.FileAccess.ReadWrite, System.IO.FileShare.Read, bufferSize: 0);
        _msgFile = new System.IO.FileStream(_msgFileName, System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite);
        _headerFile = OpenHeader();
    }

    /// <summary>
    /// FP Enhancement: 2026-09-27 — unbuffered header stream (same mode, access and share as
    /// StreamWriter(path, append: true)). A buffered stream keeps the bytes of a failed write
    /// and emits them on the next flush or Close, which recorded a header entry for a sequence
    /// number the session never sent and had already gap-filled.
    /// </summary>
    private System.IO.Stream OpenHeader()
    {
        System.IO.FileStream fs = new System.IO.FileStream(
            _headerFileName, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.Read, bufferSize: 0);
        // Only ever opened on a line boundary: after the load truncation in Open(), or after
        // the repair in AppendHeader.
        _headerCleanLength = fs.Length;
        return HeaderStreamDecorator?.Invoke(fs) ?? fs;
    }

    /// <summary>
    /// FP Enhancement: 2026-09-27 — length of the header file up to its last entry known to be
    /// complete and deliberately written. A failed header write is cut back to this length.
    /// </summary>
    private long _headerCleanLength;

    private static void TruncateTo(string path, long length)
    {
        // Missing after a Reset() whose OpenHeader failed: nothing to cut, and OpenHeader recreates it.
        if (!System.IO.File.Exists(path))
            return;

        using System.IO.FileStream fs = new System.IO.FileStream(
            path, System.IO.FileMode.Open, System.IO.FileAccess.Write, System.IO.FileShare.Read);
        if (fs.Length > length)
            fs.SetLength(length);
    }

    /// <summary>
    /// Test-only fault seam: wraps the header stream when it is opened, so a test can make a
    /// write land partially and then fail (a short write followed by ENOSPC), which no real
    /// fault can produce on demand on either platform.
    /// </summary>
    internal Func<System.IO.Stream, System.IO.Stream>? HeaderStreamDecorator { get; set; }

    private void Close()
    {
        // these vars will be null only during construction (ctor()->Open()->Close())
        _seqNumsFile?.Dispose();
        _seqNumsShadowFile?.Dispose();
        _msgFile?.Dispose();
        _headerFile?.Dispose();
    }

    private static void PurgeSingleFile(System.IO.Stream stream, string filename)
    {
        stream.Close();
        if (System.IO.File.Exists(filename))
            System.IO.File.Delete(filename);
    }

    private static void PurgeSingleFile(string filename)
    {
        if (System.IO.File.Exists(filename))
            System.IO.File.Delete(filename);
    }

    private void PurgeFileCache()
    {
        PurgeSingleFile(_seqNumsFile, _seqNumsFileName);
        PurgeSingleFile(_seqNumsShadowFile, _seqNumsShadowFileName);
        PurgeSingleFile(_msgFile, _msgFileName);
        PurgeSingleFile(_headerFile, _headerFileName);
        PurgeSingleFile(_sessionFileName);
    }


    /// <summary>
    /// FP Enhancement: 2026-09-27 — cut a header file back to its last complete line. A crash
    /// or a short write (ENOSPC mid-write) can leave a torn last line: cut inside its size
    /// digits it parses as a valid, truncated entry, and the next append is glued onto it.
    /// Every complete entry ends in '\n', so everything after the last '\n' is torn.
    /// </summary>
    private static void TruncateToLastCompleteLine(string path)
    {
        if (!System.IO.File.Exists(path))
            return;

        using System.IO.FileStream fs = new System.IO.FileStream(
            path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.Read);
        // ponytail: byte-wise backward scan; a torn tail is at most one ~53-byte line. A file with
        // no '\n' at all is scanned once, then emptied.
        long keep = fs.Length;
        Span<byte> one = stackalloc byte[1];
        while (keep > 0)
        {
            fs.Position = keep - 1;
            fs.ReadExactly(one);
            if (one[0] == (byte)'\n')
                break;
            keep--;
        }
        if (keep < fs.Length)
            fs.SetLength(keep);
    }

    private void ConstructFromFileCache()
    {
        _offsets.Clear();
        TruncateToLastCompleteLine(_headerFileName);
        if (System.IO.File.Exists(_headerFileName))
        {
            // FP Enhancement: 2026-09-26 — skip a header entry whose body bytes are not all on disk
            // (a torn or truncated write). Get() would otherwise fail reading it and abandon the
            // whole resend range; without the entry the resend gap-fills over that one number.
            long bodyLength = System.IO.File.Exists(_msgFileName)
                ? new System.IO.FileInfo(_msgFileName).Length
                : 0;
            using (System.IO.StreamReader reader = new System.IO.StreamReader(_headerFileName))
            {
                while (reader.ReadLine() is { } line)
                {
                    string[] headerParts = line.Split(',');
                    if (headerParts.Length == 3
                        && SeqNumType.TryParse(headerParts[0], out SeqNumType seqNum)
                        && long.TryParse(headerParts[1], out long index)
                        && int.TryParse(headerParts[2], out int size)
                        && index >= 0 && size >= 0 && index + size <= bodyLength)
                    {
                        _offsets[seqNum] = new MsgDef(index, size);
                    }
                }
            }
        }

        if (System.IO.File.Exists(_seqNumsFileName))
        {
            string onDisk = System.IO.File.ReadAllText(_seqNumsFileName);
            _seqNumsOnDisk = onDisk;

            string record = onDisk;
            if (TryReadSeqNumsShadow(_seqNumsShadowFileName, out string previous, out string next)
                && IsInterruptedOverwrite(onDisk, previous, next))
            {
                record = next;
            }

            string[] parts = record.Split(':');
            if (parts.Length == 2)
            {
                _cache.NextSenderMsgSeqNum = Convert.ToUInt64(parts[0]);
                _cache.NextTargetMsgSeqNum = Convert.ToUInt64(parts[1]);
            }
        }
        else
        {
            // Missing (a fresh store, or a Reset interrupted between its deletes): any shadow
            // left behind is stale.
            _seqNumsOnDisk = "";
        }
    }

    private void InitializeSessionCreateTime()
    {
        if (System.IO.File.Exists(_sessionFileName) && new System.IO.FileInfo(_sessionFileName).Length > 0)
        {
            using (System.IO.StreamReader reader = new System.IO.StreamReader(_sessionFileName))
            {
                string s = reader.ReadToEnd();
                _cache.CreationTime = UtcDateTimeSerializer.FromString(s);
            }
        }
        else
        {
            using (System.IO.StreamWriter writer = new System.IO.StreamWriter(_sessionFileName, false)) {
                writer.Write(UtcDateTimeSerializer.ToString(_cache.CreationTime ?? new DateTime()));
            }
        }
    }


    #region MessageStore Members

    /// <summary>
    /// Get messages within the range of sequence numbers
    /// </summary>
    /// <param name="startSeqNum"></param>
    /// <param name="endSeqNum"></param>
    /// <param name="messages"></param>
    public void Get(SeqNumType startSeqNum, SeqNumType endSeqNum, List<string> messages)
    {
        for (SeqNumType i = startSeqNum; i <= endSeqNum; i++)
        {
            if (_offsets.TryGetValue(i, out MsgDef msgDef))
            {
                _msgFile.Seek(msgDef.Index, System.IO.SeekOrigin.Begin);
                byte[] msgBytes = ArrayPool<byte>.Shared.Rent(msgDef.Size);
                try
                {
                    _msgFile.ReadExactly(new Span<byte>(msgBytes, 0, msgDef.Size));
                    messages.Add(CharEncoding.SelectedEncoding.GetString(new ReadOnlySpan<byte>(msgBytes, 0, msgDef.Size)));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(msgBytes);
                }
            }
        }

    }

    /// <summary>
    /// Store a message
    /// </summary>
    /// <param name="msgSeqNum"></param>
    /// <param name="msg"></param>
    /// <returns></returns>
    public bool Set(SeqNumType msgSeqNum, string msg)
    {
        // FP Enhancement: 2026-09-26 — body before header, so a header entry always names
        // bytes that are already on disk. The upstream order (header first) left a header
        // pointing at a torn or missing body after a crash between the two writes.
        MsgDef def = AppendBody(msg);
        AppendHeader(msgSeqNum, def);
        return true;
    }

    /// <summary>
    /// FP Enhancement: 2026-09-26 — the upstream default performs Set() then
    /// IncrNextSenderMsgSeqNum(), separately flushed, so a crash between them left a message
    /// recorded at a sequence number the .seqnums file never advanced past: the next send
    /// reused that number and replaced the recorded message. Order the three durable writes so
    /// every interruption point leaves a coherent store: body, then the advanced sequence
    /// number, then the header entry that makes the message visible. Session.Persist runs
    /// before the send, so a message the store never recorded was never transmitted either;
    /// a crash after the sequence write leaves a gap that resend fills with a SequenceReset.
    /// </summary>
    public bool SetAndIncrNextSenderMsgSeqNum(SeqNumType msgSeqNum, string msg)
    {
        MsgDef def = AppendBody(msg);
        IncrNextSenderMsgSeqNum();
        AppendHeader(msgSeqNum, def);
        return true;
    }

    /// <summary>
    /// Test-only fault seam: invoked after each flushed write (body, header, sequence numbers).
    /// Throwing from it models the process dying at that point.
    /// </summary>
    internal Action? AfterDurableWrite { get; set; }

    private MsgDef AppendBody(string msg)
    {
        _msgFile.Seek(0, System.IO.SeekOrigin.End);
        long offset = _msgFile.Position;

        using ValueDisposable _ = CharEncoding.GetBytes(msg.AsSpan(), out ReadOnlySpan<byte> msgBytes);
        _msgFile.Write(msgBytes);
        _msgFile.Flush();
        AfterDurableWrite?.Invoke();

        return new MsgDef(offset, msgBytes.Length);
    }

    private void AppendHeader(SeqNumType msgSeqNum, MsgDef def)
    {
        using PooledStringBuilder pooledSb = new PooledStringBuilder();
        StringBuilder b = pooledSb.Builder.Append(msgSeqNum).Append(',').Append(def.Index).Append(',').Append(def.Size)
            .Append(Environment.NewLine);
        byte[] line = Encoding.ASCII.GetBytes(b.ToString());
        try
        {
            _headerFile.Write(line, 0, line.Length);
            _headerFile.Flush();
        }
        catch (Exception writeFailure)
        {
            // FP Enhancement: 2026-09-27 — a failed write may have landed partially (a short
            // write, then ENOSPC) or even completely while still reporting failure. Session
            // treats the number as unsent either way, so cut the header back to its length
            // before this write and reopen: nothing of this entry survives, and the next append
            // starts on a line boundary. If the repair fails, the header stream is left
            // disposed, so this write and every later one throw (the session stops persisting,
            // and therefore sending) and each later write retries the repair.
            try
            {
                _headerFile.Dispose();
                TruncateTo(_headerFileName, _headerCleanLength);
                _headerFile = OpenHeader();
            }
            catch (Exception repairFailure)
            {
                throw new System.IO.IOException(
                    "Header write failed and the header could not be cut back to its last complete entry.",
                    new AggregateException(writeFailure, repairFailure));
            }
            throw;
        }
        _headerCleanLength += line.Length;
        _offsets[msgSeqNum] = def;
        AfterDurableWrite?.Invoke();
    }

    public SeqNumType NextSenderMsgSeqNum {
        get => _cache.NextSenderMsgSeqNum;
        set {
            _cache.NextSenderMsgSeqNum = value;
            SetSeqNum();
        }
    }

    public SeqNumType NextTargetMsgSeqNum {
        get => _cache.NextTargetMsgSeqNum;
        set {
            _cache.NextTargetMsgSeqNum = value;
            SetSeqNum();
        }
    }

    public void IncrNextSenderMsgSeqNum()
    {
        _cache.IncrNextSenderMsgSeqNum();
        SetSeqNum();
    }

    public void IncrNextTargetMsgSeqNum()
    {
        _cache.IncrNextTargetMsgSeqNum();
        SetSeqNum();
    }

    /// <summary>
    /// FP Enhancement: 2026-09-27 — .seqnums is one record overwritten in place. A partial
    /// overwrite leaves the new record's leading bytes in front of the old one's trailing bytes
    /// (sender 99 -> 100 torn to 199); the record still parses, so a torn sender number forces
    /// a gap fill and a torn target number a Logout loop. Each update therefore first writes
    /// .seqnums.shadow: the record about to be written, the bytes it is about to overwrite, and
    /// a checksum over both. Then it writes the unchanged upstream record.
    /// On load, the shadow's record is used only when .seqnums is exactly an interruption of
    /// that overwrite: some leading bytes of the new record followed by the rest of the old
    /// bytes (this includes the untouched old record and the completed new one). Anything else
    /// means another writer rewrote .seqnums (an engine that predates the shadow, an operator's
    /// deliberate edit), and .seqnums wins as upstream reads it. A torn shadow fails its
    /// checksum, which means the interruption came before .seqnums was touched.
    /// </summary>
    private void SetSeqNum()
    {
        string next = NextSenderMsgSeqNum.ToString("D20") + " : " + NextTargetMsgSeqNum.ToString("D20") + "  ";
        WriteRecord(_seqNumsShadowFile, SeqNumsShadowRecord(_seqNumsOnDisk, next));
        try
        {
            WriteRecord(_seqNumsFile, next);
        }
        catch
        {
            // Whatever landed is now what the next shadow must describe as "about to overwrite".
            _seqNumsOnDisk = ReadAllTextOrEmpty(_seqNumsFileName);
            throw;
        }
        _seqNumsOnDisk = next;
        AfterDurableWrite?.Invoke();
    }

    /// <summary>The .seqnums bytes currently on disk, as far as this store knows.</summary>
    private string _seqNumsOnDisk = "";

    private static string ReadAllTextOrEmpty(string path)
    {
        try
        {
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
        }
        catch (System.IO.IOException)
        {
            return "";
        }
    }

    private static void WriteRecord(System.IO.FileStream fs, string record)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(record);
        fs.Seek(0, System.IO.SeekOrigin.Begin);
        fs.Write(bytes, 0, bytes.Length);
        if (fs.Length > bytes.Length)
            fs.SetLength(bytes.Length);
        fs.Flush();
    }

    // Shadow layout: <next: fixed 45-byte upstream record><previous: 0..n bytes> <checksum>\n
    private static readonly int SeqNumsRecordLength = (0UL.ToString("D20") + " : " + 0UL.ToString("D20") + "  ").Length;

    private static string SeqNumsShadowRecord(string previous, string next) =>
        next + previous + " " + SeqNumsChecksum(next + previous) + "\n";

    private static string SeqNumsChecksum(string payload) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload)), 0, 8);

    private static bool TryReadSeqNumsShadow(string path, out string previous, out string next)
    {
        previous = next = "";
        if (!System.IO.File.Exists(path))
            return false;

        string shadow = System.IO.File.ReadAllText(path);
        const int trailer = 1 + 16 + 1; // " " + checksum + "\n"
        if (shadow.Length < SeqNumsRecordLength + trailer
            || !shadow.EndsWith('\n')
            || shadow[shadow.Length - trailer] != ' ')
            return false;

        string payload = shadow.Substring(0, shadow.Length - trailer);
        if (SeqNumsChecksum(payload) != shadow.Substring(shadow.Length - trailer + 1, 16))
            return false;

        next = payload.Substring(0, SeqNumsRecordLength);
        previous = payload.Substring(SeqNumsRecordLength);
        return true;
    }

    /// <summary>
    /// True when <paramref name="onDisk"/> is what an overwrite of <paramref name="previous"/>
    /// by <paramref name="next"/> leaves if interrupted after any number of bytes.
    /// </summary>
    private static bool IsInterruptedOverwrite(string onDisk, string previous, string next)
    {
        int k = 0;
        while (k < onDisk.Length && k < next.Length && onDisk[k] == next[k])
            k++;
        string rest = k < previous.Length ? previous.Substring(k) : "";
        return onDisk == next.Substring(0, k) + rest;
    }

    public DateTime? CreationTime => _cache.CreationTime;

    public void Reset()
    {
        _cache.Reset();
        PurgeFileCache();
        Open();
    }

    public void Refresh()
    {
        _cache.Reset();
        Open();
    }

    #endregion

    #region IDisposable Members

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    private bool _disposed;
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            Close();
        }
        _disposed = true;
    }

    ~FileStore() => Dispose(false);
    #endregion
}
