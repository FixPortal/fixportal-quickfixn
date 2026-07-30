using System.Threading;
using NUnit.Framework;
using QuickFix;
using QuickFix.Logger;
using QuickFix.Store;

namespace UnitTests;

// Pins the AbstractInitiator restart-race fix: a Start() racing an in-flight Stop() must not
// resurrect/duplicate the worker thread. The bug was that Stop() sets IsStopped=true and blocks in
// Join() *outside* _sync before nulling _thread, so a concurrent Start() (guarded only by
// "_thread is not null && !IsStopped") would pass, flip IsStopped back to false, and spawn a second
// OnStart worker. The fix guards Start() on "_thread is not null" alone.
[TestFixture]
public class AbstractInitiatorRestartRaceTests
{
    private sealed class GatedInitiator : AbstractInitiator
    {
        public readonly ManualResetEventSlim Entered = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public readonly ManualResetEventSlim StopEntered = new(false);
        public int OnStartCount;

        public GatedInitiator(IApplication app, IMessageStoreFactory store, SessionSettings settings)
            : base(app, store, settings, (ILogFactory?)new NullLogFactory()) { }

        protected override void OnStart()
        {
            Interlocked.Increment(ref OnStartCount);
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
}
