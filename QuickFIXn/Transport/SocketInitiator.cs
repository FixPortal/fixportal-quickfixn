using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using QuickFix.Logger;
using QuickFix.Store;

namespace QuickFix.Transport;

/// <summary>
/// Initiates connections and uses a single thread to process messages for all sessions.
/// </summary>
public class SocketInitiator : AbstractInitiator
{
    private volatile bool _shutdownRequested = false;
    private DateTime _lastConnectTimeDt = DateTime.MinValue;
    private int _reconnectInterval = 30;
    private readonly SocketSettings _socketSettings = new();
    private readonly Dictionary<SessionID, SocketInitiatorThread> _threads = new();
    private readonly Dictionary<SessionID, int> _sessionToHostNum = new();
    private readonly object _sync = new();
    // FP Enhancement: 2026-08-06 — wake the worker for dynamic sessions while preserving established retry pacing.
    private readonly object _connectRequestSync = new();
    private readonly HashSet<SessionID> _connectRequests = [];
    private readonly ILogger _nonSessionLog;

    public SocketInitiator(
        IApplication application,
        IMessageStoreFactory storeFactory,
        SessionSettings settings,
        ILogFactory? logFactoryNullable = null,
        IMessageFactory? messageFactoryNullable = null,
        IFixWireTap? wireTap = null)
        : base(application, storeFactory, settings, logFactoryNullable, messageFactoryNullable, wireTap)
    {
        _nonSessionLog = QfLoggerFactory.CreateNonSessionLogger<SocketInitiator>();
    }

    public SocketInitiator(
        IApplication application,
        IMessageStoreFactory storeFactory,
        SessionSettings settings,
        ILoggerFactory? loggerFactoryNullable = null,
        IMessageFactory? messageFactoryNullable = null,
        IFixWireTap? wireTap = null)
        : base(application, storeFactory, settings, loggerFactoryNullable, messageFactoryNullable, wireTap)
    {
        _nonSessionLog = QfLoggerFactory.CreateNonSessionLogger<SocketInitiator>();
    }

    public static void SocketInitiatorThreadStart(object? socketInitiatorThread)
    {
        SocketInitiatorThread? t = socketInitiatorThread as SocketInitiatorThread;
        if (t == null) return;

        try
        {
            t.Connect();
            if (t.Initiator.TryActivate(t))
            {
                while (t.Read()) {
                    // Read dispatches each complete message and returns whether the stream remains open.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when shutdown interrupts connection setup.
        }
        catch (IOException ex) // Can be exception when connecting, during ssl authentication or when reading
        {
            LogThreadStartConnectionFailed(t, ex);
        }
        catch (SocketException ex)
        {
            LogThreadStartConnectionFailed(t, ex);
        }
        catch (System.Security.Authentication.AuthenticationException ex) // some certificate problems
        {
            LogThreadStartConnectionFailed(t, ex);
        }
        catch (Exception ex)
        {
            LogThreadStartConnectionFailed(t, ex);
        }

        t.Initiator.RemoveThread(t);
        t.Initiator.SetDisconnected(t.Session.SessionID);
    }

    // FP Enhancement: 2026-08-06 — make connection activation atomic with initiator shutdown.
    private bool TryActivate(SocketInitiatorThread thread)
    {
        // Lock order: admission monitor, then AbstractInitiator state/session.
        lock (_connectRequestSync)
        {
            if (_shutdownRequested || !IsPending(thread.Session))
                return false;

            SetConnected(thread.Session.SessionID);
            thread.Session.Log.Log(LogLevel.Information, "Connection succeeded");
            thread.Session.Next();
            return true;
        }
    }

    private static void LogThreadStartConnectionFailed(SocketInitiatorThread t, Exception e) {
        if (t.Session.Disposed)
        {
            t.NonSessionLog.Log(LogLevel.Error, e, "Connection failed [session {SessionID}]: {Message}",
                t.Session.SessionID, e.Message);
            return;
        }
        t.Session.Log.Log(LogLevel.Error, e, "Connection failed: {Message}", e.Message);
    }

    private void AddThread(SocketInitiatorThread thread)
    {
        lock (_sync)
        {
            _threads[thread.Session.SessionID] = thread;
        }
    }

    private void RemoveThread(SocketInitiatorThread thread)
    {
        // FP Enhancement: 2026-08-06 — a stale worker must not detach a newer same-ID generation.
        lock (_sync)
        {
            if (!_threads.TryGetValue(thread.Session.SessionID, out SocketInitiatorThread? registeredThread)
                || !ReferenceEquals(registeredThread, thread))
                return;
            _threads.Remove(thread.Session.SessionID);
        }

        try
        {
            thread.Join();
        }
        catch { }
    }

    // FP Enhancement: 2026-08-06 — detach socket workers under lock and join them after releasing it.
    private void RemoveThread(SessionID sessionId)
    {
        SocketInitiatorThread? thread;
        lock (_sync)
        {
            if (!_threads.Remove(sessionId, out thread))
                return;
        }

        try
        {
            thread.Join();
        }
        catch { }
    }

    private IPEndPoint GetNextSocketEndPoint(SessionID sessionId, SettingsDictionary settings)
    {
        if (!_sessionToHostNum.TryGetValue(sessionId, out var num))
            num = 0;

        string hostKey = SessionSettings.SOCKET_CONNECT_HOST + num;
        string portKey = SessionSettings.SOCKET_CONNECT_PORT + num;
        if (!settings.Has(hostKey) || !settings.Has(portKey))
        {
            num = 0;
            hostKey = SessionSettings.SOCKET_CONNECT_HOST;
            portKey = SessionSettings.SOCKET_CONNECT_PORT;
        }

        try
        {
            var hostName = settings.GetString(hostKey);
            IPAddress[] addrs = Dns.GetHostAddresses(hostName);
            int port = System.Convert.ToInt32(settings.GetLong(portKey));
            _sessionToHostNum[sessionId] = ++num;

            _socketSettings.ServerCommonName = hostName;
            IPAddress? addr = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                              ?? addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
            if (addr is null)
                throw new Exception("No IPv4 or IPv6 address found");

            return new IPEndPoint(addr, port);
        }
        catch (Exception e)
        {
            throw new ConfigError(e.Message, e);
        }
    }

    #region Initiator Methods

    /// <summary>
    /// handle other socket options like TCP_NO_DELAY here
    /// </summary>
    /// <param name="settings"></param>
    protected override void OnConfigure(SessionSettings settings)
    {
        lock (_connectRequestSync)
            _shutdownRequested = false;

        try
        {
            _reconnectInterval = Convert.ToInt32(settings.Get().GetLong(SessionSettings.RECONNECT_INTERVAL));
        }
        catch (Exception)
        { }

        // TODO: Don't know if this is required in order to handle settings in the general section
        _socketSettings.Configure(settings.Get());
    }

    protected override void OnStart()
    {
        while (true)
        {
            try
            {
                // Lock order: admission monitor, AbstractInitiator session lock (inside Connect),
                // then this transport's thread lock (inside AddThread).
                lock (_connectRequestSync)
                {
                    if (_shutdownRequested)
                        return;

                    while (_connectRequests.Count > 0)
                    {
                        SessionID sessionId = _connectRequests.First();
                        _connectRequests.Remove(sessionId);
                        Connect(sessionId);
                    }

                    double reconnectIntervalAsMilliseconds = 1000.0 * _reconnectInterval;
                    DateTime nowDt = DateTime.UtcNow;

                    if (nowDt.Subtract(_lastConnectTimeDt).TotalMilliseconds >= reconnectIntervalAsMilliseconds)
                    {
                        Connect();
                        _lastConnectTimeDt = nowDt;
                    }

                    if (!_shutdownRequested && _connectRequests.Count == 0)
                        Monitor.Wait(_connectRequestSync, 1000);
                }
            }
            catch (Exception e)
            {
                _nonSessionLog.Log(LogLevel.Error, e, "Failed to start: {Message}", e.Message);
            }
        }
    }

    protected override void OnAdd(SessionID sessionId)
    {
        lock (_connectRequestSync)
        {
            _connectRequests.Add(sessionId);
            Monitor.Pulse(_connectRequestSync);
        }
    }

    /// <summary>
    /// Ad-hoc session removal
    /// </summary>
    /// <param name="sessionId">ID of session being removed</param>
    protected override void OnRemove(SessionID sessionId)
    {
        lock (_connectRequestSync)
            _connectRequests.Remove(sessionId);
        RemoveThread(sessionId);
    }

    protected override bool OnPoll(double timeout)
    {
        throw new NotImplementedException("FIXME - SocketInitiator.OnPoll not implemented!");
    }

    protected override void OnStop()
    {
        List<SocketInitiatorThread> threads;
        lock (_connectRequestSync)
        {
            _shutdownRequested = true;
            lock (_sync)
            {
                threads = [.. _threads.Values];
                _threads.Clear();
            }
            Monitor.PulseAll(_connectRequestSync);
        }

        foreach (SocketInitiatorThread thread in threads)
        {
            try
            {
                thread.Join();
            }
            catch { }
        }
    }

    protected override void DoConnect(Session session, SettingsDictionary settings)
    {
        try
        {
            if (!session.IsSessionTime)
                return;

            IPEndPoint socketEndPoint = GetNextSocketEndPoint(session.SessionID, settings);
            SetPending(session.SessionID);
            session.Log.Log(LogLevel.Information, "Connecting to {Address} on port {Port}",
                socketEndPoint.Address, socketEndPoint.Port);

            //Setup socket settings based on current section
            var socketSettings = _socketSettings.Clone();
            socketSettings.Configure(settings);

            // Create a Ssl-SocketInitiatorThread if a certificate is given
            SocketInitiatorThread t = new SocketInitiatorThread(
                this, session, socketEndPoint, socketSettings, QfLoggerFactory);
            AddThread(t);
            try
            {
                t.Start();
            }
            catch (Exception)
            {
                lock (_sync)
                {
                    _threads.Remove(session.SessionID);
                }
                throw;
            }
        }
        catch (Exception e) {
            session.Log.Log(LogLevel.Error, e, "Connection error: {Message}", e.Message);
        }
    }

    #endregion

    protected override void Dispose(bool disposing)
    {
        // nothing additional to do for this subclass
        base.Dispose(disposing);
    }
}
