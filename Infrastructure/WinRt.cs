using System.Diagnostics;
using Windows.Foundation;

namespace WwanProbe.Infrastructure
{
    // WinRT async bridging.
    public static class TaskWait
    {
        public static T Await<T>(IAsyncOperation<T> operation, int timeoutMs = 10000) =>
            FirstResult(() => operation.AsTask(), timeoutMs);

        // Returns the result of the first completed task started by start; a faulted task rethrows.
        // start runs once, and once more if nothing has completed after resendAfterMs; both tasks are then
        // awaited until timeoutMs in total. Nothing is cancelled.
        public static T FirstResult<T>(Func<Task<T>> start, int timeoutMs, int resendAfterMs = int.MaxValue)
        {
            List<Task<T>> tasks = new List<Task<T>> { start() };
            Stopwatch timer = Stopwatch.StartNew();
            while (true)
            {
                foreach (Task<T> t in tasks)
                {
                    // GetResult rethrows the original fault, not an AggregateException.
                    if (t.IsCompleted) return t.GetAwaiter().GetResult();
                }
                long elapsed = timer.ElapsedMilliseconds;
                if (elapsed >= timeoutMs) throw new TimeoutException($"WinRT async operation timed out ({timeoutMs}ms)");
                if (tasks.Count == 1 && elapsed >= resendAfterMs)
                {
                    tasks.Add(start());
                    continue;
                }
                long wait = timeoutMs - elapsed;
                if (tasks.Count == 1) wait = Math.Min(wait, resendAfterMs - elapsed);
                Task.WaitAny(tasks.ToArray(), (int)Math.Max(0, wait));
            }
        }
    }
}
