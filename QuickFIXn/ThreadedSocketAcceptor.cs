using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging;
using QuickFix.Logger;
using QuickFix.Store;

namespace QuickFix;

/// <summary>
/// Acceptor implementation - with threads
/// Creates a ThreadedSocketReactor for every listening endpoint.
/// </summary>
public class ThreadedSocketAcceptor : IAcceptor
{
    private const int TenSecondsInTicks = 10000;

    private readonly Dictionary<SessionID, Session> _sessions = new();
    private readonly SessionSettings _settings;
    private readonly Dictionary<IPEndPoint, AcceptorSocketDescriptor> _socketDescriptorForAddress = new();
    private readonly SessionFactory _sessionFactory;
    private bool _disposed = false;
    private readonly object _sync = new();
    private readonly IQuickFixLoggerFactory _qfLoggerFactory;
    private readonly LogFactoryAdapter? _logFactoryAdapter;

    // FP Enhancement: 2026-09-01 — expose the optional durable outbound journal through acceptor construction.
    /// <summary>
    /// Create a ThreadedSocketAcceptor (with a legacy ILogFactory)
    /// </summary>
    /// <param name="application"></param>
    /// <param name="storeFactory"></param>
    /// <param name="settings"></param>
    /// <param name="logFactory">If null, a NullQuickFixLoggerFactory (which produces no logs) will be used.</param>
    /// <param name="messageFactory">If null, a DefaultMessageFactory will be created (using settings parameters)</param>
    /// <param name="wireTap">Optional observer for raw FIX wire traffic.</param>
    /// <param name="outboundSendJournal">Optional durable journal for outbound FIX frames.</param>
    public ThreadedSocketAcceptor(
        IApplication application,
        IMessageStoreFactory storeFactory,
        SessionSettings settings,
        ILogFactory? logFactory = null,
        IMessageFactory? messageFactory = null,
        IFixWireTap? wireTap = null,
        IOutboundSendJournal? outboundSendJournal = null)
        : this(
            application,
            storeFactory,
            settings,
            logFactory is null ? NullQuickFixLoggerFactory.Instance : new LogFactoryAdapter(logFactory),
            messageFactory,
            wireTap,
            outboundSendJournal)
    { }

    /// <summary>
    /// Create a ThreadedSocketAcceptor
    /// </summary>
    /// <param name="application"></param>
    /// <param name="storeFactory"></param>
    /// <param name="settings"></param>
    /// <param name="loggerFactory">If null, a NullQuickFixLoggerFactory (which produces no logs) will be used.</param>
    /// <param name="messageFactory">If null, a DefaultMessageFactory will be created (using settings parameters)</param>
    /// <param name="wireTap">Optional observer for raw FIX wire traffic.</param>
    /// <param name="outboundSendJournal">Optional durable journal for outbound FIX frames.</param>
    public ThreadedSocketAcceptor(
        IApplication application,
        IMessageStoreFactory storeFactory,
        SessionSettings settings,
        ILoggerFactory? loggerFactory = null,
        IMessageFactory? messageFactory = null,
        IFixWireTap? wireTap = null,
        IOutboundSendJournal? outboundSendJournal = null)
        : this(
            application,
            storeFactory,
            settings,
            loggerFactory is null
                ? NullQuickFixLoggerFactory.Instance
                : new MelQuickFixLoggerFactory(loggerFactory),
            messageFactory,
            wireTap,
            outboundSendJournal)
    { }

    private ThreadedSocketAcceptor(
        IApplication application,
        IMessageStoreFactory storeFactory,
        SessionSettings settings,
        IQuickFixLoggerFactory qfLoggerFactory,
        IMessageFactory? messageFactory = null,
        IFixWireTap? wireTap = null,
        IOutboundSendJournal? outboundSendJournal = null)
    {
        if (qfLoggerFactory is LogFactoryAdapter lfa)
        {
            // LogFactoryAdapter is only ever created in the constructor marked obsolete, which means we own it and
            // must save a ref to it so we can dispose it later. Any other loggerFactory is owned by someone else
            // so we'll leave the dispose up to them. This should be removed eventually together with the old ILog
            // and ILogFactory.
            _logFactoryAdapter = lfa;
        }
        IMessageFactory mf = messageFactory ?? new DefaultMessageFactory();
        _settings = settings;
        _sessionFactory = new SessionFactory(application, storeFactory, qfLoggerFactory, mf, wireTap, outboundSendJournal);
        _qfLoggerFactory = qfLoggerFactory;

        try
        {
            foreach (SessionID sessionId in settings.GetSessions())
            {
                SettingsDictionary dict = settings.Get(sessionId);
                CreateSession(sessionId, dict);
            }
        }
        catch (Exception e)
        {
            throw new ConfigError(e.Message, e);
        }
    }

    #region Private Methods

    private AcceptorSocketDescriptor GetAcceptorSocketDescriptor(SettingsDictionary dict)
    {
        int port = Convert.ToInt32(dict.GetLong(SessionSettings.SOCKET_ACCEPT_PORT));
        SocketSettings socketSettings = new SocketSettings();

        IPEndPoint socketEndPoint;
        if (dict.Has(SessionSettings.SOCKET_ACCEPT_HOST))
        {
            string host = dict.GetString(SessionSettings.SOCKET_ACCEPT_HOST);

            if (IPAddress.TryParse(host, out IPAddress? parsedAddr) && IsAnyIpAddress(host))
            {
                socketEndPoint = new IPEndPoint(parsedAddr, port);
            }
            else
            {
                IPAddress[] addrs = Dns.GetHostAddresses(host);
                socketEndPoint = new IPEndPoint(addrs[0], port);
                // Set hostname (if it is not already configured)
                socketSettings.ServerCommonName ??= host;
            }
        }
        else
        {
            socketEndPoint = new IPEndPoint(IPAddress.Any, port);
        }

        socketSettings.Configure(dict);

        if (!_socketDescriptorForAddress.TryGetValue(socketEndPoint, out var descriptor))
        {
            descriptor = new AcceptorSocketDescriptor(socketEndPoint, socketSettings, _qfLoggerFactory);
            _socketDescriptorForAddress[socketEndPoint] = descriptor;
        }

        return descriptor;
    }

    /// <summary>
    /// Checks if the host address is the "any" address in either IPv4 (0.0.0.0) or IPv6 (::)
    /// </summary>
    /// <param name="host"></param>
    /// <returns></returns>
    private static bool IsAnyIpAddress(string host)
    {
        return IPAddress.TryParse(host, out IPAddress? address) &&
               (address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.IPv6Any));
    }

    /// <summary>
    /// Create session, either at start-up or as an ad-hoc operation
    /// </summary>
    /// <param name="sessionId">ID of new session</param>
    /// <param name="dict">config settings for new session</param>
    /// <returns>true if session added successfully, false if session already exists or is not an acceptor</returns>
    private bool CreateSession(SessionID sessionId, SettingsDictionary dict)
    {
        lock (_sync)
        {
            if (!_sessions.ContainsKey(sessionId))
            {
                string connectionType = dict.GetString(SessionSettings.CONNECTION_TYPE);
                if ("acceptor" == connectionType)
                {
                    AcceptorSocketDescriptor descriptor = GetAcceptorSocketDescriptor(dict);
                    Session session = _sessionFactory.Create(sessionId, dict);
                    descriptor.AcceptSession(session);
                    _sessions[sessionId] = session;

                    // start SocketReactor if it was created via AddSession call
                    // and if acceptor is already started
                    if (IsStarted && !_disposed)
                    {
                        descriptor.SocketReactor.Start();
                    }

                    return true;
                }
            }
            return false;
        }
    }

    private void StartAcceptingConnections()
    {
        lock (_sync)
        {
            foreach (AcceptorSocketDescriptor socketDescriptor in _socketDescriptorForAddress.Values)
            {
                socketDescriptor.SocketReactor.Start();
            }
        }
    }

    private void StopAcceptingConnections()
    {
        lock (_sync)
        {
            foreach (AcceptorSocketDescriptor socketDescriptor in _socketDescriptorForAddress.Values)
            {
                socketDescriptor.SocketReactor.StopAccepting();
            }
        }
    }

    private void ShutdownReactors()
    {
        lock (_sync)
        {
            foreach (AcceptorSocketDescriptor socketDescriptor in _socketDescriptorForAddress.Values)
            {
                socketDescriptor.SocketReactor.Shutdown();
            }
        }
    }

    private void LogonAllSessions()
    {
        // FP Enhancement: snapshot under _sync before iterating. CreateSession/RemoveSession mutate
        // _sessions under _sync, so a concurrent ad-hoc add/remove during start would otherwise throw
        // InvalidOperationException out of this lock-free enumeration. Iterate the snapshot outside the
        // lock so a slow/re-entrant session.Logon() callback never runs under the engine lock.
        List<Session> sessions;
        lock (_sync) { sessions = _sessions.Values.ToList(); }
        foreach (Session session in sessions)
        {
            try
            {
                session.Logon();
            }
            catch (Exception e)
            {
                session.Log.Log(LogLevel.Error, e, "Error during logon of Session {SessionID}: {Message}",
                    session.SessionID, e.Message);
            }
        }
    }

    private void LogoutAllSessions(bool force)
    {
        // FP Enhancement: snapshot under _sync (see LogonAllSessions) — avoids the collection-modified
        // race against a concurrent CreateSession/RemoveSession during Stop.
        List<Session> sessions;
        lock (_sync) { sessions = _sessions.Values.ToList(); }
        foreach (Session session in sessions)
        {
            try
            {
                session.Logout();
            }
            catch (Exception e)
            {
                session.Log.Log(LogLevel.Error, e, "Error during logout of Session {SessionID}: {Message}",
                    session.SessionID, e.Message);
            }
        }

        if (force && IsLoggedOn)
        {
            DisconnectSessions("Forcibly disconnecting sessions");
        }

        if (!force)
            WaitForLogout();
    }

    private void WaitForLogout()
    {
        int start = Environment.TickCount;
        using( var resetEvent = new ManualResetEvent( false ) )
        {
            while (IsLoggedOn && (Environment.TickCount - start) < TenSecondsInTicks)
            {
                resetEvent.WaitOne( 100 );
            }
        }
        DisconnectSessions("Logout timeout, force disconnect");
    }

    private void DisconnectSessions(string disconnectMessage)
    {
        // FP Enhancement: snapshot under _sync (see LogonAllSessions) — avoids the collection-modified
        // race against a concurrent CreateSession/RemoveSession.
        List<Session> sessions;
        lock (_sync) { sessions = _sessions.Values.ToList(); }
        foreach (Session session in sessions)
        {
            try
            {
                if (session.IsLoggedOn)
                    session.Disconnect(disconnectMessage);
            }
            catch (Exception e)
            {
                session.Log.Log(LogLevel.Error, e, "Error during disconnect of Session {SessionID}, {Message}",
                    session.SessionID, e.Message);
            }
        }
    }

    private void DisposeSessions()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
    }

    #endregion

    // FP Enhancement: 2026-05-24 — expose the listening endpoints so operations/tests can observe which ports the acceptor is bound to.
    public IEnumerable<IPEndPoint> EndPoints()
    {
        lock (_sync)
        {
            return _socketDescriptorForAddress.Values.Select(socketDescriptor => socketDescriptor.Address).ToList();
        }
    }

    #region Acceptor Members

    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);

        lock (_sync)
        {
            if (!IsStarted)
            {
                LogonAllSessions();
                StartAcceptingConnections();
                IsStarted = true;
            }
        }
    }

    public void Stop()
    {
        Stop(false);
    }

    public void Stop(bool force)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock( _sync )
        {
            if( IsStarted )
            {
                IsStarted = false;
                StopAcceptingConnections();
                LogoutAllSessions(force);
                ShutdownReactors();
            }
        }
    }

    /// <summary>
    /// Check whether any sessions are logged on
    /// </summary>
    /// <returns>true if any session is logged on, else false</returns>
    public bool IsLoggedOn
    {
        get
        {
            lock (_sync)
            {
                return _sessions.Values.Any(session => session.IsLoggedOn);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether this instance is started.
    /// </summary>
    /// <value>
    /// <c>true</c> if this instance is started; otherwise, <c>false</c>.
    /// </value>
    public bool IsStarted { get; private set; } = false;

    /// <summary>
    /// (For use by Unit Tests)
    /// Gets a value indicating whether this instance is started.
    /// </summary>
    /// <value>
    /// <c>true</c> if this instance is started; otherwise, <c>false</c>.
    /// </value>
    internal bool AreSocketsRunning
    {
        get
        {
            return _socketDescriptorForAddress.All( s => s.Value.SocketReactor.IsRunning );
        }
    }

    /// <summary>
    /// Get the SessionIDs for the sessions managed by this acceptor.
    /// </summary>
    /// <returns>the SessionIDs for the sessions managed by this acceptor</returns>
    public HashSet<SessionID> GetSessionIDs()
    {
        lock (_sync)
        {
            return new HashSet<SessionID>(_sessions.Keys);
        }
    }

    /// <summary>
    /// TODO: not yet implemented
    /// </summary>
    /// <returns></returns>
    public Dictionary<SessionID, IPEndPoint> GetAcceptorAddresses()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Add new session as an ad-oc (dynamic) operation
    /// </summary>
    /// <param name="sessionId">ID of new session</param>
    /// <param name="dict">config settings for new session</param>
    /// <returns>true if session added successfully, false if session already exists or is not an acceptor</returns>
    public bool AddSession(SessionID sessionId, SettingsDictionary dict)
    {
        lock (_settings)
            if (!_settings.Has(sessionId)) // session won't be in settings if ad-hoc creation after startup
                _settings.Set(sessionId, dict); // need to to this here to merge in default config settings
            else
                return false; // session already exists

        if (CreateSession(sessionId, dict))
            return true;

        lock (_settings) // failed to create session, so remove from settings
            _settings.Remove(sessionId);
        return false;
    }


    // FP Enhancement: 2026-05-24 — free the listening port when the last session on it is removed. Upstream's RemoveSession leaks the AcceptorSocketDescriptor and its reactor, so the bound port stays held — a long-running acceptor that dynamically cycles sessions eventually starves on ports. We track which descriptor became empty during the loop and shut it down after the iteration to avoid mutating the dictionary while traversing it.
    /// <summary>
    /// Ad-hoc removal of an existing session
    /// </summary>
    /// <param name="sessionId">ID of session to be removed</param>
    /// <param name="terminateActiveSession">if true, force disconnection and removal of session even if it has an active connection</param>
    /// <returns>true if session removed or not already present; false if could not be removed due to an active connection</returns>
    public bool RemoveSession(SessionID sessionId, bool terminateActiveSession)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                if (session.IsLoggedOn && !terminateActiveSession)
                    return false;
                session.Disconnect("Dynamic session removal");

                // Track a descriptor that this removal empties, so its listening port can be freed.
                AcceptorSocketDescriptor? emptiedDescriptor = null;
                foreach (AcceptorSocketDescriptor descriptor in _socketDescriptorForAddress.Values)
                {
                    if (descriptor.RemoveSession(sessionId))
                    {
                        if (descriptor.GetAcceptedSessions().Count == 0)
                            emptiedDescriptor = descriptor;
                        break;
                    }
                }

                _sessions.Remove(sessionId);
                session.Dispose();
                lock (_settings)
                    _settings.Remove(sessionId);

                // The removed session was the last one on its port: shut the reactor down and drop
                // the descriptor, so the port is released and a later AddSession on it builds a fresh
                // descriptor + reactor. Done after the loop to avoid mutating the dictionary mid-iteration.
                if (emptiedDescriptor is not null)
                {
                    emptiedDescriptor.SocketReactor.Shutdown();
                    _socketDescriptorForAddress.Remove(emptiedDescriptor.Address);
                }
            }
            return true;
        }
    }

    #endregion

    /// <summary>
    /// Any subclasses of ThreadedSocketAcceptor should override this if they have resources to dispose
    /// Any override should call base.Dispose(disposing).
    /// </summary>
    /// <param name="disposing">true if called from Dispose()</param>
    protected virtual void Dispose(bool disposing)
    {
        if(_disposed) { return; }
        try
        {
            if (disposing)
            {
                Stop();
                // Take _sync around the session-collection teardown so a concurrent
                // CreateSession/RemoveSession (which lock _sync) cannot mutate _sessions
                // mid-enumeration. Stop() is invoked outside the lock — the acceptor lock is
                // non-reentrant and Stop() takes it internally.
                lock (_sync)
                {
                    DisposeSessions();
                    _sessions.Clear();
                }
                _logFactoryAdapter?.Dispose();
            }
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
        finally
        {
            _disposed = true;
        }
    }
    /// <summary>
    /// Disposes created sessions
    /// </summary>
    /// <remarks>
    /// To simply stop the acceptor without disposing sessions, use Stop() or Stop(bool)
    /// </remarks>
    public void Dispose()
    {
        Dispose( true );
        GC.SuppressFinalize( this );
    }

    ~ThreadedSocketAcceptor() => Dispose(false);
}
