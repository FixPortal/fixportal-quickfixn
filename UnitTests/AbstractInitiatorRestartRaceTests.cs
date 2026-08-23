using System.Threading;
using NUnit.Framework;
using QuickFix;
using QuickFix.Logger;
using QuickFix.Store;

namespace UnitTests;

// Pins the two guards that keep AbstractInitiator.Start() from spawning a duplicate worker:
//
// 1. The lifecycle lock: Stop(bool) holds _lifecycleSync across its whole teardown (including
//    Join(5000)), so a Start() racing an in-flight Stop() bails at Monitor.TryEnter and returns
//    immediately. Start_RacingAnInFlightStop_DoesNotSpawnASecondWorker pins THAT guard — it
//    cannot reach the "_thread is not null" check, because Stop never releases the lock inside
//    the race window. (An earlier header claimed this test pinned the _thread guard; that was
//    adversarial finding R11 — the claim was wrong, the lock makes that path unreachable here.)
//
// 2. The "_thread is not null" guard: a plain second Start() with no Stop in flight passes
//    Monitor.TryEnter and is refused by the _thread check.
//    Start_CalledTwiceWithNoStop_DoesNotSpawnASecondWorker pins THAT guard.
[TestFixture]
public class AbstractInitiatorRestartRaceTests
{
    private sealed class GatedInitiator : AbstractInitiator
    {
        public readonly ManualResetEventSlim Entered = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public readonly ManualResetEventSlim StopEntered = new(false);
        public readonly ManualResetEventSlim SecondWorkerSpawned = new(false);
        public int OnStartCount;

        public GatedInitiator(IApplication app, IMessageStoreFactory store, SessionSettings settings)
            : base(app, store, settings, (ILogFactory?)new NullLogFactory()) { }

        protected override void OnStart()
        {
            if (Interlocked.Increment(ref OnStartCount) > 1)
                SecondWorkerSpawned.Set();
            Entered.Set();
            Release.Wait(); // block regardless of IsStopped so the test can pin the race window
        }

        protected override bool OnPoll(double timeout) => false;
        protected override void OnStop() => StopEntered.Set();
        protected override void DoConnect(Session session, SettingsDictionary settings) { }
    }

    private static SessionSettings InitiatorSettings()
    {
        var d = new SettingsDictionary();
        d.SetString(SessionSettings.CONNECTION_TYPE, "initiator");
        d.SetString(SessionSettings.USE_DATA_DICTIONARY, "N");
        d.SetString(SessionSettings.START_TIME, "12:00:00");
        d.SetString(SessionSettings.END_TIME, "12:00:00");
        d.SetString(SessionSettings.HEARTBTINT, "30");
        d.SetString(SessionSettings.SOCKET_CONNECT_HOST, "127.0.0.1");
        d.SetString(SessionSettings.SOCKET_CONNECT_PORT, "65530");
        var s = new SessionSettings();
        s.Set(new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET"), d);
        return s;
    }

    [Test]
    public void Start_RacingAnInFlightStop_DoesNotSpawnASecondWorker()
    {
        var init = new GatedInitiator(new SessionTestSupport.MockApplication(),
            new MemoryStoreFactory(), InitiatorSettings());

        init.Start();
        Assert.That(init.Entered.Wait(5000), Is.True, "OnStart should run");
        Assert.That(init.OnStartCount, Is.EqualTo(1));

        // Stop on a background thread; its Join(5000) blocks on the gated OnStart, leaving the
        // initiator in the race window: IsStopped=true, _thread still the old (alive) worker.
        var stopper = new Thread(() => init.Stop(force: true));
        stopper.Start();

        // OnStop is called after IsStopped is set and immediately before Join, so this signal
        // deterministically places the test inside the restart race window.
        Assert.That(init.StopEntered.Wait(5000), Is.True, "Stop should enter the race window");
        Assert.That(init.IsStopped, Is.True, "Stop should have reached IsStopped=true (the race window)");

        // Race a Start now. With the fix it must refuse (a worker still exists); without it, Start
        // would flip IsStopped=false and spawn a second OnStart -> OnStartCount == 2.
        init.Start();

        // Release both the original worker and any wrongly spawned replacement. Stop joins the
        // current worker reference, so completion is the deterministic observation boundary.
        init.Release.Set();
        Assert.That(stopper.Join(5000), Is.True, "Stop should complete once the worker exits");
        Assert.That(init.OnStartCount, Is.EqualTo(1),
            "a Start racing an in-flight Stop must not resurrect/duplicate the worker thread");

        init.Dispose();
    }

    [Test]
    public void Start_CalledTwiceWithNoStop_DoesNotSpawnASecondWorker()
    {
        var init = new GatedInitiator(new SessionTestSupport.MockApplication(),
            new MemoryStoreFactory(), InitiatorSettings());

        init.Start();
        Assert.That(init.Entered.Wait(5000), Is.True, "OnStart should run");
        Assert.That(init.OnStartCount, Is.EqualTo(1));

        // No Stop() in flight, so this Start() passes Monitor.TryEnter(_lifecycleSync) and is
        // refused by the "_thread is not null" guard alone — the guard the racing-Stop test
        // above structurally cannot reach.
        init.Start();

        // Deterministic mutation detection: if the guard is regressed, the second worker's
        // OnStart sets SecondWorkerSpawned as soon as it runs. Waiting on the event (rather
        // than a fixed sleep) fails fast on regression and never under-waits on a slow runner.
        Assert.That(init.SecondWorkerSpawned.Wait(2000), Is.False,
            "a second Start() with an existing worker must be refused by the _thread guard");
        Assert.That(init.OnStartCount, Is.EqualTo(1));

        init.Release.Set();
        init.Dispose();
    }
}
