using System;
using System.Diagnostics;
using System.Threading;

namespace AowEmailWrapper.Helpers
{
    /// <summary>
    /// Starting the Wrapper while it already runs shows the running one instead of doing nothing: the new
    /// process sets a named event and exits, and the running one, which waits on that event, shows its
    /// window. The name carries the executable's name, as the single-instance check does, so a copy under
    /// another name (the smoke tests) neither answers nor sends.
    /// </summary>
    public sealed class SecondStartSignal : IDisposable
    {
        private readonly EventWaitHandle _event;
        private RegisteredWaitHandle _registration;

        private static string EventName
        {
            get
            {
                using (Process current = Process.GetCurrentProcess())
                {
                    return @"Local\AowEmailWrapper.Show." + current.ProcessName;
                }
            }
        }

        private SecondStartSignal(EventWaitHandle handle)
        {
            _event = handle;
        }

        /// <summary>Called by the running Wrapper: <paramref name="show"/> runs on a pool thread each time another start asks.</summary>
        public static SecondStartSignal Listen(Action show)
        {
            SecondStartSignal signal = new SecondStartSignal(new EventWaitHandle(false, EventResetMode.AutoReset, EventName));
            signal._registration = ThreadPool.RegisterWaitForSingleObject(signal._event, (state, timedOut) => show(), null, Timeout.Infinite, false);
            return signal;
        }

        /// <summary>Called by a second start: asks the running Wrapper to show itself. False when none listens.</summary>
        public static bool AskRunningToShow()
        {
            EventWaitHandle handle;
            if (!EventWaitHandle.TryOpenExisting(EventName, out handle))
            {
                return false;
            }
            using (handle)
            {
                //The start the player just made may take the foreground; this lets the running one have it
                WindowHelper.AllowOthersToComeToFront();
                return handle.Set();
            }
        }

        public void Dispose()
        {
            _registration?.Unregister(null);
            _event.Dispose();
        }
    }
}
