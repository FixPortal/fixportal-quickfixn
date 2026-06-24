using System;
using System.Collections.Generic;

namespace QuickFix.Logger;

/// <summary>
/// File log implementation
/// </summary>
internal class CompositeLog : ILog
{
    private readonly ILog[] _logs;

    private bool _disposed = false;

    public CompositeLog(ILog[] logs)
    {
        _logs = logs;
    }

    public void Clear()
    {
        DisposedCheck();
        List<Exception>? exceptions = null;
        foreach (var log in _logs)
        {
            try
            {
                log.Clear();
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }
        if (exceptions is not null)
        {
            throw new AggregateException("One or more logs failed to clear", exceptions);
        }
    }

    public void OnIncoming(string msg)
    {
        DisposedCheck();
        List<Exception>? exceptions = null;
        foreach (var log in _logs)
        {
            try
            {
                log.OnIncoming(msg);
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }
        if (exceptions is not null)
        {
            throw new AggregateException("One or more logs failed to process OnIncoming", exceptions);
        }
    }

    public void OnOutgoing(string msg)
    {
        DisposedCheck();
        List<Exception>? exceptions = null;
        foreach (var log in _logs)
        {
            try
            {
                log.OnOutgoing(msg);
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }
        if (exceptions is not null)
        {
            throw new AggregateException("One or more logs failed to process OnOutgoing", exceptions);
        }
    }

    public void OnEvent(string s)
    {
        DisposedCheck();
        List<Exception>? exceptions = null;
        foreach (var log in _logs)
        {
            try
            {
                log.OnEvent(s);
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }
        if (exceptions is not null)
        {
            throw new AggregateException("One or more logs failed to process OnEvent", exceptions);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            // FP Enhancement: never throw out of Dispose. Each child is disposed regardless of the others'
            // failures; a rethrow here would abort the caller's teardown (e.g. session/acceptor disposal)
            // and strand later resources. A child's Dispose is a no-op-on-failure best effort — swallow.
            foreach (var log in _logs)
            {
                try { log.Dispose(); } catch { /* best-effort: never propagate out of Dispose */ }
            }
        }
        _disposed = true;
    }

    private void DisposedCheck()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    ~CompositeLog() => Dispose(false);

    // FP Enhancement: 2026-05-24 — fan FixPortal log events out to every child log in the composite (rejections & tracked-message tuples).
    public void LogOn()
    {
        DisposedCheck();
        List<Exception>? exceptions = null;
        foreach (var log in _logs)
        {
            try
            {
                log.LogOn();
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }
        if (exceptions is not null)
        {
            throw new AggregateException("One or more logs failed to process LogOn", exceptions);
        }
    }

    public void LogOff()
    {
        DisposedCheck();
        List<Exception>? exceptions = null;
        foreach (var log in _logs)
        {
            try
            {
                log.LogOff();
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }
        if (exceptions is not null)
        {
            throw new AggregateException("One or more logs failed to process LogOff", exceptions);
        }
    }

    public void OnIncomingAndOutgoing((int Id, string Raw, string Xml, string Json) message)
    {
        DisposedCheck();
        List<Exception>? exceptions = null;
        foreach (var log in _logs)
        {
            try
            {
                log.OnIncomingAndOutgoing((message.Id, message.Raw, message.Xml, message.Json));
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }
        if (exceptions is not null)
        {
            throw new AggregateException("One or more logs failed to process OnIncomingAndOutgoing", exceptions);
        }
    }

    public void OnRejectionEvent(string originalMessage, string rejectionText)
    {
        DisposedCheck();
        List<Exception>? exceptions = null;
        foreach (var log in _logs)
        {
            try
            {
                log.OnRejectionEvent(originalMessage, rejectionText);
            }
            catch (Exception ex)
            {
                exceptions ??= new List<Exception>();
                exceptions.Add(ex);
            }
        }
        if (exceptions is not null)
        {
            throw new AggregateException("One or more logs failed to process OnRejectionEvent", exceptions);
        }
    }
}