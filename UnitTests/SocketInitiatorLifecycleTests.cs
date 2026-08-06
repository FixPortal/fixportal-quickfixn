using System.Threading;
using NUnit.Framework;
using QuickFix;
using QuickFix.Logger;
using QuickFix.Store;
using QuickFix.Transport;

namespace UnitTests;

[TestFixture]
public class SocketInitiatorLifecycleTests
{
    private sealed class GatedSocketInitiator : SocketInitiator
    {
        private readonly ManualResetEventSlim _workerEntered;
        private readonly ManualResetEventSlim _releaseWorker;
        private readonly ManualResetEventSlim _workerExited;
        private readonly ManualResetEventSlim _stopEntered;

        public GatedSocketInitiator(
            SessionSettings settings,
            ManualResetEventSlim workerEntered,
            ManualResetEventSlim releaseWorker,
            ManualResetEventSlim workerExited,
            ManualResetEventSlim stopEntered)
            : base(new SessionTestSupport.MockApplication(), new MemoryStoreFactory(), settings,
                (ILogFactory?)new NullLogFactory())
        {
            _workerEntered = workerEntered;
            _releaseWorker = releaseWorker;
            _workerExited = workerExited;
            _stopEntered = stopEntered;
        }

        protected override void OnStart()
        {
            _workerEntered.Set();
            _releaseWorker.Wait();
            try
            {
                base.OnStart();
            }
            finally
            {
                _workerExited.Set();
            }
        }

        protected override void OnStop()
        {
            _stopEntered.Set();
            base.OnStop();
        }

        public void RequestWorkerStop() => base.OnStop();
    }

    private static SessionSettings InitiatorSettings()
    {
        var session = new SettingsDictionary();
        session.SetString(SessionSettings.CONNECTION_TYPE, "initiator");
        session.SetString(SessionSettings.USE_DATA_DICTIONARY, "N");
        session.SetString(SessionSettings.START_TIME, "12:00:00");
        session.SetString(SessionSettings.END_TIME, "12:00:00");
        session.SetString(SessionSettings.HEARTBTINT, "30");
        session.SetString(SessionSettings.SOCKET_CONNECT_HOST, "127.0.0.1");
        session.SetString(SessionSettings.SOCKET_CONNECT_PORT, "65530");

        var settings = new SessionSettings();
        settings.Set(new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET"), session);
        return settings;
    }

    [Test]
    public void StopBeforeWorkerLoopStartsDoesNotGetResetByWorker()
    {
        using var workerEntered = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var workerExited = new ManualResetEventSlim();
        using var stopEntered = new ManualResetEventSlim();
        using var initiator = new GatedSocketInitiator(
            InitiatorSettings(), workerEntered, releaseWorker, workerExited, stopEntered);
        Thread? stopper = null;

        try
        {
            initiator.Start();
            Assert.That(workerEntered.Wait(5000), Is.True, "worker should reach the pre-loop gate");

            stopper = new Thread(() => initiator.Stop(force: true));
            stopper.Start();
            Assert.That(stopEntered.Wait(5000), Is.True, "stop should set shutdown before the worker loop starts");

            releaseWorker.Set();

            Assert.That(workerExited.Wait(5000), Is.True,
                "worker must not reset a shutdown requested before its loop starts");
            Assert.That(stopper.Join(5000), Is.True, "stop should complete after the worker exits");
        }
        finally
        {
            releaseWorker.Set();
            initiator.RequestWorkerStop();
            workerExited.Wait(5000);
            stopper?.Join(5000);
        }
    }
}
