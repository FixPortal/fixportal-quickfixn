using System.Threading;
using System.Collections.Generic;
using System;
using Microsoft.Extensions.Logging;
using QuickFix.Logger;
using QuickFix.Store;

namespace QuickFix;

public abstract class AbstractInitiator : IInitiator
{
    // from constructor
    private readonly SessionSettings _settings;

    private readonly object _sync = new();
    private readonly object _lifecycleSync = new();
    private readonly Dictionary<SessionID, Session> _sessions = new();
    private readonly HashSet<SessionID> _sessionIDs = [];
    private readonly HashSet<SessionID> _pending = [];
    private readonly HashSet<SessionID> _connected = [];
    private readonly HashSet<SessionID> _disconnected = [];
    private readonly HashSet<SessionID> _removing = [];
    private readonly HashSet<SessionID> _creating = [];
    private readonly SessionFactory _sessionFactory;
    private Thread? _thread;

    internal readonly IQuickFixLoggerFactory QfLoggerFactory;
    private readonly LogFactoryAdapter? _logFactoryAdapter;

    public bool IsStopped { get; private set; } = true;

    protected AbstractInitiator(
        IApplication app,
        IMessageStoreFactory storeFactory,
        SessionSettings settings,
        ILogFactory? logFactoryNullable = null,
        IMessageFactory? messageFactoryNullable = null,
        IFixWireTap? wireTap = null)
        : this(
            app,
            storeFactory,
            settings,
            logFactoryNullable is null
                ? NullQuickFixLoggerFactory.Instance
                : new LogFactoryAdapter(logFactoryNullable),
            messageFactoryNullable,
            wireTap)
    { }

    protected AbstractInitiator(
        IApplication app,
        IMessageStoreFactory storeFactory,
        SessionSettings settings,
        ILoggerFactory? loggerFactoryNullable = null,
        IMessageFactory? messageFactoryNullable = null,
        IFixWireTap? wireTap = null)
        : this(
            app,
            storeFactory,
            settings,
            loggerFactoryNullable is null
                ? NullQuickFixLoggerFactory.Instance
                : new MelQuickFixLoggerFactory(loggerFactoryNullable),
            messageFactoryNullable,
            wireTap)
    { }

    private AbstractInitiator(
        IApplication app,
        IMessageStoreFactory storeFactory,
        SessionSettings settings,
        IQuickFixLoggerFactory qfLoggerFactory,
        IMessageFactory? messageFactoryNullable = null,
        IFixWireTap? wireTap = null)
    {
        _settings = settings;
        if (qfLoggerFactory is LogFactoryAdapter lfa)
        {
            // LogFactoryAdapter is only created in the constructor that takes ILogFactory,
            // which means we own it and must save a ref to it so we can dispose it later.
            // Any other loggerFactory is owned by someone else
            // so we'll leave the dispose up to them.
            _logFactoryAdapter = lfa;
        }
        var msgFactory = messageFactoryNullable ?? new DefaultMessageFactory();
        _sessionFactory = new SessionFactory(app, storeFactory, qfLoggerFactory, msgFactory, wireTap);
        QfLoggerFactory = qfLoggerFactory;

        HashSet<SessionID> definedSessions = _settings.GetSessions();
        if (0 == definedSessions.Count)
            throw new ConfigError("No sessions defined");
    }

    public void Start()
    {
        // FP Enhancement: 2026-08-06 — refuse restart while any stop lifecycle still owns cleanup.
        if (!Monitor.TryEnter(_lifecycleSync))
            return;
        try
        {
            if (_disposed)
                throw new ObjectDisposedException(this.GetType().Name);

            Thread worker;
            lock (_settings)
            {
                lock (_sync)
                {
                    if (_thread is not null)
                        return;
                }

                foreach (SessionID sessionId in _settings.GetSessions())
                {
                    SettingsDictionary dict = _settings.Get(sessionId);
                    CreateSession(sessionId, dict);
                }

                lock (_sync)
                {
                    if (0 == _sessions.Count)
                        throw new ConfigError("No sessions defined for initiator");
                }

                // Transport configuration has its own admission lock; do not invert it with _sync.
                OnConfigure(_settings);
                worker = new Thread(OnStart);
                lock (_sync)
                {
                    IsStopped = false;
                    _thread = worker;
                }
            }
            worker.Start();
        }
        finally
        {
            Monitor.Exit(_lifecycleSync);
        }
    }

    /// <summary>
    /// Add new session as an ad-hoc (dynamic) operation
    /// </summary>
    /// <param name="sessionId">ID of new session</param>
    /// <param name="dict">config settings for new session</param>
    /// <returns>true if session added successfully, false if session already exists or is not an initiator</returns>
    public bool AddSession(SessionID sessionId, SettingsDictionary dict)
    {
        lock (_settings)
            if (!_settings.Has(sessionId)) // session won't be in settings if ad-hoc creation after startup
                _settings.Set(sessionId, dict); // need to to this here to merge in default config settings
            else
                return false; // session already exists

        if (CreateSession(sessionId, dict))
        {
            // FP Enhancement: 2026-08-06 — let transports schedule newly added sessions without connecting on the caller thread.
            OnAdd(sessionId);
            return true;
        }

        lock (_settings) // failed to create new session
            _settings.Remove(sessionId);
        return false;
    }

    /// <summary>
    /// Create session, either at start-up or as an ad-hoc operation
    /// </summary>
    /// <param name="sessionId">ID of new session</param>
    /// <param name="dict">config settings for new session</param>
    /// <returns>true if session added successfully, false if session already exists or is not an initiator</returns>
    private bool CreateSession(SessionID sessionId, SettingsDictionary dict)
    {
        if (dict.GetString(SessionSettings.CONNECTION_TYPE) != "initiator")
            return false;

        lock (_sync)
        {
            if (_sessionIDs.Contains(sessionId) || _removing.Contains(sessionId) || !_creating.Add(sessionId))
                return false;
        }

        Session session;
        try
        {
            // FP Enhancement: 2026-08-23 — run factory creation OUTSIDE _sync (adversarial
            // finding R8). SessionFactory.Create parses data-dictionary XML, opens the message
            // store and log, and invokes the public Application.OnCreate callback — blocking
            // I/O and third-party code whose duration the engine does not control. Holding the
            // core state lock across it stalled every connection-state transition and could
            // deadlock on an OnCreate that coordinates with a thread needing _sync. The ID is
            // reserved in _creating instead, and the reservation is rolled back on failure.
            session = _sessionFactory.Create(sessionId, dict);
        }
        catch
        {
            lock (_sync)
                _creating.Remove(sessionId);
            throw;
        }

        lock (_sync)
        {
            _creating.Remove(sessionId);
            _sessionIDs.Add(sessionId);
            _sessions[sessionId] = session;
            SetDisconnected(sessionId);
            return true;
        }
    }

    /// <summary>
    /// Ad-hoc removal of an existing session
    /// </summary>
    /// <param name="sessionId">ID of session to be removed</param>
    /// <param name="terminateActiveSession">if true, force disconnection and removal of session even if it has an active connection</param>
    /// <returns>true if session removed or not already present; false if could not be removed due to an active connection</returns>
    /// <remarks>A duplicate call returns true while cleanup is still in progress; the same ID cannot be added again until cleanup completes.</remarks>
    /// <exception cref="Exception">Session disposal failures, including message-store disposal failures, are propagated
    /// when removal completes synchronously. When the transport defers completion until its reader thread exits,
    /// disposal happens after this method has returned; such failures are logged, not propagated.</exception>
    public bool RemoveSession(SessionID sessionId, bool terminateActiveSession)
    {
        Session? session = null;
        SettingsDictionary? sessionSettings = null;
        bool disconnectRequired = false;
        lock (_settings)
        {
            lock (_sync)
            {
                if (_removing.Contains(sessionId))
                    return true;

                if (_sessionIDs.Contains(sessionId))
                {
                    session = _sessions[sessionId];
                    if (session.IsLoggedOn && !terminateActiveSession)
                        return false;
                    _sessions.Remove(sessionId);
                    disconnectRequired = IsConnected(sessionId) || IsPending(sessionId);
                    if (disconnectRequired)
                        SetDisconnected(sessionId);
                    _disconnected.Remove(sessionId);
                    _sessionIDs.Remove(sessionId);
                }
                // FP Enhancement: 2026-08-06 — reserve removal while the old transport can still
                // run application callbacks, and remember exactly which generation owns settings.
                _removing.Add(sessionId);
                if (_settings.Has(sessionId))
                    sessionSettings = _settings.Get(sessionId);
            }
        }
        try
        {
            if (disconnectRequired)
                session?.Disconnect("Dynamic session removal");
            OnRemove(sessionId); // ensure session's reader thread is gone before we dispose session
        }
        finally
        {
            // FP Enhancement: 2026-08-23 — a throw out of Disconnect (e.g. from a user
            // Application.OnLogout, invoked inside Session.Disconnect with no containment)
            // must not strand the _removing reservation: without this finally the ID stays
            // reserved for the initiator's lifetime, blocking CreateSession/AddSession, and
            // the Session is never disposed. Run completion — or hand it to the transport's
            // deferred path — before any exception propagates.
            // A transport whose bounded shutdown returned before its old worker exited owns completion.
            if (!TryDeferRemovalUntilQuiesced(sessionId, CompleteRemoval))
                CompleteRemoval();
        }

        void CompleteRemoval()
        {
            try
            {
                session?.Dispose();
            }
            finally
            {
                // FP Enhancement: 2026-08-06 — detach only the settings captured by this generation. A
                // same-ID replacement inserted while a missing-session removal was quiescing must survive.
                lock (_settings)
                {
                    lock (_sync)
                    {
                        if (sessionSettings is not null
                            && _settings.Has(sessionId)
                            && ReferenceEquals(_settings.Get(sessionId), sessionSettings))
                            _settings.Remove(sessionId);
                        _removing.Remove(sessionId);
                    }
                }
            }
        }

        return true;
    }

    internal virtual bool TryDeferRemovalUntilQuiesced(SessionID sessionId, Action completion) => false;

    /// <summary>
    /// Logout existing session and close connection.  Attempt graceful disconnect first.
    /// </summary>
    public void Stop()
    {
        Stop(false);
    }

    /// <summary>
    /// Logout existing session and close connection
    /// </summary>
    /// <param name="force">If true, terminate immediately.  </param>
    public void Stop(bool force)
    {
        lock (_lifecycleSync)
        {
            if (_disposed)
                throw new ObjectDisposedException(this.GetType().Name);

            if (IsStopped)
                return;

            // FP Enhancement: 2026-08-23 — refuse new admissions for the WHOLE shutdown window
            // (adversarial finding R6). The transport's shutdown flag was previously set inside
            // OnStop(), which runs after the logout sweep, the grace wait and the disconnect
            // sweep below — a pending connection completing in that window could still activate
            // and send a Logon from an engine that is shutting down.
            OnStopping();

            lock (_sync)
            {
                foreach (SessionID sessionId in _connected)
                {
                    Session? session = Session.LookupSession(sessionId);
                    if (session is not null && session.IsEnabled)
                    {
                        session.Logout();
                    }
                }
            }

            if (!force)
            {
                // TODO change this duration to always exceed LogoutTimeout setting
                for (int second = 0; (second < 10) && IsLoggedOn; ++second)
                    Thread.Sleep(1000);
            }

            lock (_sync)
            {
                HashSet<SessionID> connectedSessionIDs = new HashSet<SessionID>(_connected);
                foreach (SessionID sessionId in connectedSessionIDs) {
                    Session? session = Session.LookupSession(sessionId);
                    if (session is not null)
                        SetDisconnected(session.SessionID);
                }
            }

            IsStopped = true;
            OnStop();

            // Give OnStop() time to finish its business
            _thread?.Join(5000);

            lock (_sync)
            {
                _thread = null;

                foreach (Session s in _sessions.Values)
                    s.Dispose();

                _sessions.Clear();
                _sessionIDs.Clear();
                _pending.Clear();
                _connected.Clear();
                _disconnected.Clear();
                // FP Enhancement: 2026-08-23 — safety net for deferred session removals
                // (adversarial finding R4). A removal whose reader outlived the bounded join
                // keeps its ID in _removing, which blocks CreateSession on the next Start().
                // Transports bound-wait deferred readers in OnStop(); anything still
                // outstanding here is released so restart is not blocked. The deferred
                // completion remains safe: its _removing.Remove is a no-op and its settings
                // detach is ReferenceEquals-guarded against a same-ID replacement.
                _removing.Clear();
            }
        }
    }

    public bool IsLoggedOn
    {
        get
        {
            lock (_sync)
            {
                foreach (SessionID sessionId in _connected)
                {
                    Session? session = Session.LookupSession(sessionId);
                    if (session is not null && session.IsLoggedOn)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    #region Virtual Methods

    /// <summary>
    /// Override this to configure additional implemenation-specific settings
    /// </summary>
    /// <param name="settings"></param>
    protected virtual void OnConfigure(SessionSettings settings)
    { }

    /// <summary>
    /// Implement this to react to a successfully added ad-hoc session.
    /// </summary>
    /// <param name="sessionId">ID of the added session</param>
    protected virtual void OnAdd(SessionID sessionId)
    { }

    /// <summary>
    /// Implement this to provide custom reaction behavior to an ad-hoc session removal.
    /// (This is called after the session is removed.)
    /// </summary>
    /// <param name="sessionId">ID of session that was removed</param>
    protected virtual void OnRemove(SessionID sessionId)
    { }

    #endregion

    #region Abstract Methods

    /// <summary>
    /// Implemented to start connecting to targets.
    /// </summary>
    protected abstract void OnStart();
    /// <summary>
    /// Implemented to connect and poll for events.
    /// </summary>
    /// <param name="timeout"></param>
    /// <returns></returns>
    protected abstract bool OnPoll(double timeout);
    /// <summary>
    /// Implemented to stop a running initiator.
    /// </summary>
    protected abstract void OnStop();

    /// <summary>
    /// Pre-stop hook, invoked at the very start of <see cref="Stop(bool)"/> — before the logout
    /// sweep, the grace wait and the disconnect sweep. Transports override this to refuse new
    /// admissions (e.g. pending connection activations) for the entire shutdown window rather
    /// than only from <see cref="OnStop"/> onwards.
    /// </summary>
    protected virtual void OnStopping()
    { }

    /// <summary>
    /// Implemented to connect a session to its target.
    /// </summary>
    /// <param name="session"></param>
    /// <param name="settings"></param>
    protected abstract void DoConnect(Session session, QuickFix.SettingsDictionary settings);

    #endregion

    #region Protected Methods

    protected void Connect()
    {
        HashSet<SessionID> disconnectedSessions;
        lock (_sync)
            disconnectedSessions = new HashSet<SessionID>(_disconnected);

        foreach (SessionID sessionId in disconnectedSessions)
            Connect(sessionId);
    }

    // FP Enhancement: 2026-08-06 — connect a newly added session without advancing or bypassing other sessions' retry cadence.
    protected void Connect(SessionID sessionId)
    {
        Session session;
        SettingsDictionary settings;
        lock (_settings)
        {
            lock (_sync)
            {
                if (!_disconnected.Contains(sessionId)
                    || !_sessions.TryGetValue(sessionId, out Session? currentSession)
                    || !currentSession.IsEnabled)
                    return;

                session = currentSession;
                if (session.IsNewSession)
                    session.Reset("New session");
                if (!session.IsSessionTime)
                    return;
                settings = _settings.Get(sessionId);
            }
        }

        DoConnect(session, settings);
    }

    protected void SetPending(SessionID sessionId)
    {
        lock (_sync)
        {
            _pending.Add(sessionId);
            _connected.Remove(sessionId);
            _disconnected.Remove(sessionId);
        }
    }

    protected void SetConnected(SessionID sessionId)
    {
        lock (_sync)
        {
            _pending.Remove(sessionId);
            _connected.Add(sessionId);
            _disconnected.Remove(sessionId);
        }
    }

    // FP Enhancement: 2026-08-06 — admit pending state only for the current session generation.
    protected bool TrySetPending(Session session)
    {
        lock (_sync)
        {
            if (!_disconnected.Contains(session.SessionID)
                || !_sessions.TryGetValue(session.SessionID, out Session? currentSession)
                || !ReferenceEquals(currentSession, session))
                return false;

            _pending.Add(session.SessionID);
            _connected.Remove(session.SessionID);
            _disconnected.Remove(session.SessionID);
            return true;
        }
    }

    // FP Enhancement: 2026-08-06 — validate and activate one session generation atomically.
    protected bool TrySetConnected(Session session)
    {
        lock (_sync)
        {
            if (!_pending.Contains(session.SessionID)
                || !_sessions.TryGetValue(session.SessionID, out Session? pendingSession)
                || !ReferenceEquals(pendingSession, session))
                return false;

            _pending.Remove(session.SessionID);
            _connected.Add(session.SessionID);
            _disconnected.Remove(session.SessionID);
            return true;
        }
    }

    protected void SetDisconnected(SessionID sessionId)
    {
        lock (_sync)
        {
            if (_sessionIDs.Contains(sessionId))
            {
                _pending.Remove(sessionId);
                _connected.Remove(sessionId);
                _disconnected.Add(sessionId);
            }
        }
    }

    protected bool IsPending(SessionID sessionId)
    {
        lock (_sync)
        {
            return _pending.Contains(sessionId);
        }
    }

    // FP Enhancement: 2026-08-06 — a stale worker cannot disconnect a newer same-ID generation.
    protected void SetDisconnected(Session session)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(session.SessionID, out Session? currentSession)
                || !ReferenceEquals(currentSession, session))
                return;

            _pending.Remove(session.SessionID);
            _connected.Remove(session.SessionID);
            _disconnected.Add(session.SessionID);
        }
    }

    protected bool IsConnected(SessionID sessionId)
    {
        lock (_sync)
        {
            return _connected.Contains(sessionId);
        }
    }

    protected bool IsDisconnected(SessionID sessionId)
    {
        lock (_sync)
        {
            return _disconnected.Contains(sessionId);
        }
    }

    #endregion


    /// <summary>
    /// Get the SessionIDs for the sessions managed by this initiator.
    /// </summary>
    /// <returns>the SessionIDs for the sessions managed by this initiator</returns>
    public HashSet<SessionID> GetSessionIDs()
    {
        lock (_sync)
        {
            return new HashSet<SessionID>(_sessions.Keys);
        }
    }

    private bool _disposed = false;
    /// <summary>
    /// Any subclasses of AbstractInitiator should override this if they have resources to dispose
    /// that aren't already covered in its OnStop() handler.
    /// Any override should call base.Dispose(disposing).
    /// </summary>
    /// <param name="disposing"></param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            _disposed = true;
            return;
        }

        lock (_lifecycleSync)
        {
            if (_disposed) return;
            this.Stop();
            _logFactoryAdapter?.Dispose();
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~AbstractInitiator() => Dispose(false);
}
