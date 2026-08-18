using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgentOffice.Bridge
{
/// <summary>Marshals work items onto Excel's main (UI) thread.</summary>
    internal sealed class ExcelSynchronizer : IDisposable
    {
        private Control _invoker;
        private volatile bool _disposed;

/// <summary>Must be called on Excel's main thread (ThisAddIn_Startup).</summary>
        public void Initialize()
        {
            _invoker = new Control();
            // Force handle creation now, on the main thread.
            var forceHandle = _invoker.Handle;
            GC.KeepAlive(forceHandle);
        }

/// <summary>Queue on Excel's UI thread and await its result.</summary>
        public Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ExcelSynchronizer));

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _invoker.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        tcs.TrySetResult(work());
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            return AwaitWithTimeout(tcs.Task, timeout);
        }

        private static async Task<T> AwaitWithTimeout<T>(Task<T> task, TimeSpan timeout)
        {
            using (var cts = new CancellationTokenSource())
            {
                var delay = Task.Delay(timeout, cts.Token);
                var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
                if (completed == task)
                {
                    cts.Cancel();
                    return await task.ConfigureAwait(false); // propagates work exceptions
                }
                throw new BridgeException(
                    "TIMEOUT",
                    "Excel did not process the request within " + (int)timeout.TotalSeconds +
                    "s — a modal dialog may be open. Try again.");
            }
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                _invoker?.Dispose();
            }
            catch
            {
                // Ignore — the control may already be gone during Excel shutdown.
            }
        }
    }
}
