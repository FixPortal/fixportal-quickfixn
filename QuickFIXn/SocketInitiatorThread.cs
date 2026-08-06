using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuickFix.Logger;

namespace QuickFix;

/// <summary>
/// Handles a connection with an acceptor.
/// </summary>
public class SocketInitiatorThread : IResponder
{
    public Session Session { get; }
    public Transport.SocketInitiator Initiator { get; }
    public ILogger NonSessionLog { get; }

    public const int BUF_SIZE = 512;

    private Thread? _thread;
    private readonly object _completionSync = new();
    private bool _exited;
    private Action? _exitCallback;
    private readonly byte[] _readBuffer = new byte[BUF_SIZE];
    private readonly Parser _parser = new();
    private Stream? _stream;
    private readonly CancellationTokenSource _readCancellationTokenSource = new();
    private readonly IPEndPoint _socketEndPoint;
    private readonly SocketSettings _socketSettings;
    private readonly IQuickFixLoggerFactory _loggerFactory;

    /// <summary>
    /// Keep a task for handling async read
    /// </summary>
    private Task<int>? _currentReadTask;

    internal SocketInitiatorThread(
        Transport.SocketInitiator initiator,
        Session session,
        IPEndPoint socketEndPoint,
        SocketSettings socketSettings,
        IQuickFixLoggerFactory loggerFactory)
    {
        Initiator = initiator;
        Session = session;
        _loggerFactory = loggerFactory;
        NonSessionLog = _loggerFactory.CreateNonSessionLogger<SocketInitiatorThread>();
        _socketEndPoint = socketEndPoint;
        _socketSettings = socketSettings;
    }

    public void Start()
    {
        _thread = new Thread(Transport.SocketInitiator.SocketInitiatorThreadStart);
        _thread.Start(this);
    }

    public void Join()
    {
        if (_thread is null)
            return;
        Disconnect();
        // Make sure session's socket reader thread doesn't try to do a Join on itself!
        if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
            _thread.Join(2000);
        _thread = null;
    }

    internal bool TryRunWhenExited(Action callback)
    {
        lock (_completionSync)
        {
            if (_exited)
                return false;
            _exitCallback += callback;
            return true;
        }
    }

    internal void SignalExited()
    {
        Action? callback;
        lock (_completionSync)
        {
            _exited = true;
            _thread = null;
            callback = _exitCallback;
            _exitCallback = null;
        }
        callback?.Invoke();
    }

    public void Connect()
    {
        Debug.Assert(_stream == null);

        Stream stream = SetupStream();
        // FP Enhancement: 2026-08-06 — reject connection setup that completes after disconnect won the race.
        lock (_disconnectSync)
        {
            if (_disconnected)
            {
                stream.Dispose();
                throw new OperationCanceledException("Connection setup was cancelled");
            }
            _stream = stream;
            try
            {
                Session.SetResponder(this);
            }
            catch
            {
                _stream = null;
                stream.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Setup/Connect to the other party.
    /// Override this in order to setup other types of streams with other settings
    /// </summary>
    /// <returns>Stream representing the (network)connection to the other party</returns>
    protected virtual Stream SetupStream()
    {
        CancellationToken cancellationToken = _readCancellationTokenSource.Token;
        try
        {
            return Transport.StreamFactory.CreateClientStream(
                _socketEndPoint, _socketSettings, _loggerFactory, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public bool Read()
    {
        try
        {
            int bytesRead = ReadSome(_readBuffer, 1000);
            if (bytesRead > 0)
                _parser.AddToStream(new ReadOnlySpan<byte>(_readBuffer, 0, bytesRead));
            else
                Session.Next();

            ProcessStream();
            return true;
        }
        catch (Exception e)
        {
            Session.Disconnect(e.ToString()); // also calls this instance's Disconect()
        }
        return false;
    }

    /// <summary>
    /// Reads data from the network into the specified buffer.
    /// It will wait up to the specified number of milliseconds for data to arrive,
    /// if no data has arrived after the specified number of milliseconds then the function returns 0
    /// </summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="timeoutMilliseconds">The timeout milliseconds.</param>
    /// <returns>The number of bytes read into the buffer</returns>
    /// <exception cref="System.Net.Sockets.SocketException">On connection reset</exception>
    protected virtual int ReadSome(byte[] buffer, int timeoutMilliseconds)
    {
        if (_stream is null) {
            throw new ApplicationException("Initiator is not connected (uninitialized stream)");
        }

        // NOTE: FROM HERE, THIS FUNCTION IS EXACTLY THE SAME AS THE ONE IN SocketReader.
        // Any changes made here should also be performed there.
        try
        {
            // Begin read if it is not already started
            _currentReadTask ??= _stream.ReadAsync(buffer, 0, buffer.Length, _readCancellationTokenSource.Token);

            if (_currentReadTask.Wait(timeoutMilliseconds)) {
                // Dispose/nullify currentReadTask *before* retrieving .Result.
                //   Accessing .Result can throw an exception, so we need to reset currentReadTask
                //   first, to set us up for the next read even if an exception is thrown.
                Task<int>? request = _currentReadTask;
                _currentReadTask = null;

                int bytesRead = request.Result; // (As mentioned above, this can throw an exception!)
                if (0 == bytesRead)
                    throw new SocketException(Convert.ToInt32(SocketError.Shutdown));

                return bytesRead;
            }

            return 0;
        }
        catch (AggregateException ex) // Timeout
        {
            _currentReadTask = null;

            if (ex.InnerException is OperationCanceledException) {
                // Nothing read 
                return 0;
            }
            
            var ioException = ex.InnerException as IOException;
            var inner = ioException?.InnerException as SocketException;
            if (inner is not null && inner.SocketErrorCode == SocketError.TimedOut) {
                // Nothing read 
                return 0;
            }

            if (inner is not null) {
                throw inner; //rethrow SocketException part (which we have exception logic for)
            }

            throw; //rethrow original exception
        }
    }

    private void ProcessStream()
    {
        while (_parser.ReadFixMessage(out var msg))
        {
            Session.Next(msg);
        }
    }

    #region Responder Members

    public bool Send(string data)
    {
        if (_stream is null) {
            throw new ApplicationException("Initiator is not connected (uninitialized stream)");
        }

        using ValueDisposable _ = CharEncoding.GetBytes(data, out ReadOnlySpan<byte> rawData);
        _stream.Write(rawData);

        return true;
    }

    private readonly object _disconnectSync = new();
    private bool _disconnected = false;

    public void Disconnect()
    {
        lock (_disconnectSync)
        {
            if (_disconnected)
                return;
            _disconnected = true;
        }

        // FP Enhancement: 2026-06-01 — Cancel before waiting; dispose CTS only after the in-flight ReadAsync unwinds to prevent ObjectDisposedException killing the reader thread.
        try
        {
            _readCancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException) { }

        if (_currentReadTask is { IsCompleted: false })
            NonSessionLog.Log(LogLevel.Warning,
                "SocketInitiatorThread: disconnecting with an in-flight ReadAsync; cancelling gracefully (ObjectDisposedException would have killed the reader thread without this fix).");

        try
        {
            if (_currentReadTask is not null)
            {
                bool completed = _currentReadTask.ContinueWith(_ => { }).Wait(1000);
                if (completed && _currentReadTask.IsCompleted)
                {
                    _currentReadTask.Dispose();
                }
                _currentReadTask = null;
            }
        }
        catch (Exception ex)
        {
            NonSessionLog.Log(LogLevel.Warning, ex, "SocketInitiatorThread: error during current read task cleanup.");
        }

        try
        {
            _readCancellationTokenSource.Dispose();
        }
        catch (ObjectDisposedException) { }

        _stream?.Close();
    }

    #endregion
}

