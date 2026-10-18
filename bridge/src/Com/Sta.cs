using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SwBridge.Com;

/// The one thread that talks to SOLIDWORKS.
///
/// SOLIDWORKS is a single-threaded COM server, so calls are serialized here and nowhere else.
/// The thread registers an OLE message filter: while SOLIDWORKS shows a modal dialog it rejects
/// incoming calls (RPC_E_CALL_REJECTED), and the filter retries them until the job's deadline
/// instead of failing at once.
///
/// A call SOLIDWORKS is executing cannot be interrupted. When a job outlives its deadline (or
/// its caller cancels), the caller gets TIMEOUT or CANCELLED. If the job was still queued it is
/// dropped and never runs; the queued -> running / abandoned handoff is one atomic exchange, so
/// "it will not run" is a promise. If it was running, the thread stays occupied, the outcome is
/// unknown, and later jobs fail fast with SW_BUSY naming it instead of queueing behind it.
sealed class Sta : IDisposable
{
    const int Queued = 0, Running = 1, Abandoned = 2;

    readonly BlockingCollection<Job> queue = new BlockingCollection<Job>();
    readonly Thread thread;
    volatile Job current;

    public Sta()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "swbridge-sta" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// What the thread is executing right now, for answers that must not wait on it.
    public (string name, TimeSpan elapsed)? Current
    {
        get
        {
            var j = current;
            return j == null ? null : (j.Name, DateTime.UtcNow - j.Started);
        }
    }

    public Task<T> Run<T>(string name, TimeSpan budget, CancellationToken cancel, Func<T> work)
    {
        var busy = current;
        if (busy != null && busy.Overdue)
            throw new BridgeException("SW_BUSY",
                $"SOLIDWORKS is still executing {busy.Name} ({(int)(DateTime.UtcNow - busy.Started).TotalSeconds} s); " +
                "a dialog may be open on the Windows desktop");

        var job = new Job(name, budget, () => work());
        queue.Add(job);
        return Await<T>(job, cancel);
    }

    static async Task<T> Await<T>(Job job, CancellationToken cancel)
    {
        var gaveUp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancel.Register(() => gaveUp.TrySetResult(true)))
        {
            var done = await Task.WhenAny(job.Result.Task, Task.Delay(job.Budget + TimeSpan.FromSeconds(2)), gaveUp.Task)
                .ConfigureAwait(false);
            if (done != job.Result.Task)
            {
                string outcome;
                if (Interlocked.CompareExchange(ref job.State, Abandoned, Queued) == Queued)
                {
                    outcome = "it never started and will not run";
                }
                else
                {
                    job.Overdue = true;
                    outcome = "it is still running inside SOLIDWORKS and may yet complete; " +
                              "check the result (sw_status, sw_inspect) before retrying";
                }
                if (done == gaveUp.Task) throw new BridgeException("CANCELLED", $"{job.Name} was cancelled; {outcome}");
                throw new BridgeException("TIMEOUT",
                    $"{job.Name} did not finish within {(int)job.Budget.TotalSeconds} s; {outcome}. " +
                    "SOLIDWORKS may be busy or showing a dialog");
            }
        }
        return (T)await job.Result.Task.ConfigureAwait(false);
    }

    void Loop()
    {
        MessageFilter.Register();
        foreach (var job in queue.GetConsumingEnumerable())
        {
            if (Interlocked.CompareExchange(ref job.State, Running, Queued) != Queued) continue;
            job.Started = DateTime.UtcNow;
            current = job;
            MessageFilter.Deadline = job.Started + job.Budget;
            try
            {
                using (new ComScope()) job.Result.TrySetResult(job.Work());
            }
            catch (Exception e)
            {
                job.Result.TrySetException(e);
            }
            finally
            {
                current = null;
            }
        }
    }

    public void Dispose() => queue.CompleteAdding();

    sealed class Job
    {
        public readonly string Name;
        public readonly TimeSpan Budget;
        public readonly Func<object> Work;
        public readonly TaskCompletionSource<object> Result =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        public int State;
        public DateTime Started;
        public volatile bool Overdue;

        public Job(string name, TimeSpan budget, Func<object> work)
        {
            Name = name;
            Budget = budget;
            Work = work;
        }
    }
}

/// IOleMessageFilter for the STA thread: retry calls SOLIDWORKS rejects while it is busy,
/// until the current job's deadline. The standard pattern for automating Office-style servers.
[ComVisible(true)]
sealed class MessageFilter : IOleMessageFilter
{
    [ThreadStatic] public static DateTime Deadline;

    const int SERVERCALL_ISHANDLED = 0;
    const int SERVERCALL_RETRYLATER = 2;
    const int PENDINGMSG_WAITDEFPROCESS = 2;

    [DllImport("ole32.dll")]
    static extern int CoRegisterMessageFilter(IOleMessageFilter filter, out IOleMessageFilter old);

    public static void Register() => CoRegisterMessageFilter(new MessageFilter(), out _);

    int IOleMessageFilter.HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo) =>
        SERVERCALL_ISHANDLED;

    int IOleMessageFilter.RetryRejectedCall(IntPtr callee, int tickCount, int rejectType)
    {
        if (rejectType != SERVERCALL_RETRYLATER) return -1;
        return DateTime.UtcNow < Deadline ? 250 : -1;
    }

    int IOleMessageFilter.MessagePending(IntPtr callee, int tickCount, int pendingType) =>
        PENDINGMSG_WAITDEFPROCESS;
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IOleMessageFilter
{
    [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr callee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr callee, int tickCount, int pendingType);
}
