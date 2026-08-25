using System.Diagnostics;
using AAEmu.Commons.Utils;

using NLog;

namespace AAEmu.Game.Core.Managers;

public class TickManager : Singleton<TickManager>, ITickManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    public delegate void OnTickEvent(TimeSpan delta);
    public TickEventHandler OnTick { get; } = new();
    private bool DoTickLoop = true;
    private Thread TickThread;
    private readonly Stopwatch _metricsLogSw = new();

    /// <summary>
    /// Gets a snapshot of the tick duration metrics (invoke p50/p95/max, per-subscriber durations, subscriber count).
    /// </summary>
    public TickMetricsSnapshot GetTickMetrics()
    {
        return OnTick.SnapshotMetrics();
    }

    private void TickLoop()
    {
        var sw = new Stopwatch();
        sw.Start();
        _metricsLogSw.Start();
        while (DoTickLoop)
        {
            var before = sw.Elapsed;
            OnTick.Invoke();
            var time = sw.Elapsed - before;
            if (time > TimeSpan.FromMilliseconds(100))
                Logger.Warn("Tick took {0}ms to finish", time.TotalMilliseconds);

            // Periodic metrics summary — at most once per 60s (the loop itself runs at ~20ms sleep)
            if (_metricsLogSw.Elapsed >= TimeSpan.FromSeconds(60))
            {
                _metricsLogSw.Restart();
                var metrics = OnTick.SnapshotMetrics();
                Logger.Info("Tick metrics: invoke p50={0:F1}ms p95={1:F1}ms max={2:F1}ms subscribers={3}",
                    metrics.InvokeP50Ms, metrics.InvokeP95Ms, metrics.InvokeMaxMs, metrics.SubscriberCount);
            }

            Thread.Sleep(20);
        }
        sw.Stop();
    }

    public void Initialize()
    {
        TickThread = new Thread(() => TickLoop());
        TickThread.Start();
    }

    public void Stop()
    {
        DoTickLoop = false;
    }

    public class TickEventEntity(TickEventHandler.OnTickEvent ev, TimeSpan tickRate, bool useAsync, string name)
    {
        public TickEventHandler.OnTickEvent Event { get; } = ev;
        public TimeSpan LastExecution { get; set; }
        public TimeSpan TickRate { get; } = tickRate;
        public Task ActiveTask { get; set; }
        public bool UseAsync { get; } = useAsync;
        public string Name { get; } = name;
    }

    public class TickEventHandler
    {
        private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

        public delegate void OnTickEvent(TimeSpan delta);
        private readonly List<TickEventEntity> _eventList;
        private readonly Queue<TickEventEntity> _eventsToAdd;
        private readonly Queue<OnTickEvent> _eventsToRemove;
        private readonly Stopwatch _sw;
        private readonly object _lock = new();

        /// <summary>
        /// In-process duration metrics for this handler (invoke + per-subscriber ring buffers).
        /// </summary>
        internal TickMetrics Metrics { get; } = new();

        public TickEventHandler()
        {
            _eventList = [];
            _eventsToAdd = new Queue<TickEventEntity>();
            _eventsToRemove = new Queue<OnTickEvent>();
            _sw = new Stopwatch();
            _sw.Start();
        }

        public void Invoke()
        {
            var invokeSw = Stopwatch.StartNew();

            lock (_lock)
            {
                while (_eventsToAdd.Count > 0)
                {
                    var ev = _eventsToAdd.Dequeue();
                    _eventList.Add(ev);
                }
                while (_eventsToRemove.Count > 0)
                {
                    var ev = _eventsToRemove.Dequeue();
                    var evToRemove = _eventList.FirstOrDefault(o => o.Event == ev);
                    if (evToRemove?.Event != null)
                        _eventList.Remove(evToRemove);
                }
            }

            foreach (var ev in _eventList)
            {
                var delta = ev.LastExecution != default ? _sw.Elapsed - ev.LastExecution : ev.TickRate.Add(TimeSpan.FromMilliseconds(1));
                if (delta > ev.TickRate)
                {
                    if (ev.UseAsync)
                    {
                        if (ev.ActiveTask == null || ev.ActiveTask.IsCompleted)
                        {
                            ev.LastExecution = _sw.Elapsed;
                            ev.ActiveTask = Task.Run(() =>
                            {
                                var subSw = Stopwatch.StartNew();
                                try
                                {
                                    ev.Event(delta);
                                }
                                catch (Exception e)
                                {
                                    Logger.Error("{0}\n{1}", e.Message, e.StackTrace);
                                }
                                finally
                                {
                                    subSw.Stop();
                                    Metrics.RecordSubscriber(ev.Name, subSw.Elapsed);
                                }
                            });
                        }
                    }
                    else
                    {
                        ev.LastExecution = _sw.Elapsed;
                        var subSw = Stopwatch.StartNew();
                        try
                        {
                            ev.Event(delta);
                        }
                        catch (Exception e)
                        {
                            Logger.Error("{0}\n{1}", e.Message, e.StackTrace);
                        }
                        finally
                        {
                            subSw.Stop();
                            Metrics.RecordSubscriber(ev.Name, subSw.Elapsed);
                        }
                    }
                }
            }

            invokeSw.Stop();
            Metrics.RecordInvoke(invokeSw.Elapsed);
        }

        /// <summary>
        /// Subscribes a tick event. Async subscribers dispatch via the thread pool so a slow
        /// handler can never block the tick loop; sync subscribers run inline (use sparingly).
        /// </summary>
        /// <param name="name">Optional subscriber name for per-subscriber metrics (defaults to the handler method name).</param>
        public void Subscribe(OnTickEvent tickEvent, TimeSpan tickRate = default, bool useAsync = false, string name = null)
        {
            lock (_lock)
            {
                _eventsToAdd.Enqueue(new TickEventEntity(tickEvent, tickRate, useAsync, name ?? tickEvent.Method?.Name ?? "anonymous"));
            }
        }

        public void UnSubscribe(OnTickEvent tickEvent)
        {
            lock (_lock)
            {
                _eventsToRemove.Enqueue(tickEvent);
            }
        }

        /// <summary>
        /// Gets a snapshot of the tick duration metrics (invoke p50/p95/max, per-subscriber durations, subscriber count).
        /// </summary>
        public TickMetricsSnapshot SnapshotMetrics()
        {
            lock (_lock)
            {
                return Metrics.Snapshot(_eventList.Count);
            }
        }
    }
}

/// <summary>
/// Per-subscriber tick duration metrics (p50/p95/max over the sampled window).
/// </summary>
public sealed class SubscriberTickMetrics
{
    public string Name { get; init; }
    public long SampleCount { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double MaxMs { get; init; }
}

/// <summary>
/// Immutable snapshot of tick metrics: overall invoke duration percentiles, per-subscriber
/// durations and the live subscriber count.
/// </summary>
public sealed class TickMetricsSnapshot
{
    public int SubscriberCount { get; init; }
    public long InvokeSampleCount { get; init; }
    public double InvokeP50Ms { get; init; }
    public double InvokeP95Ms { get; init; }
    public double InvokeMaxMs { get; init; }
    public IReadOnlyDictionary<string, SubscriberTickMetrics> Subscribers { get; init; } = new Dictionary<string, SubscriberTickMetrics>();
}

/// <summary>
/// Fixed-capacity ring buffer of duration samples with percentile queries. Thread-safe via the owner's lock.
/// </summary>
internal sealed class SampleRing
{
    private const int Capacity = 1024;
    private readonly double[] _samples = new double[Capacity];
    private int _count;
    private int _head;

    public void Add(double ms)
    {
        _samples[_head] = ms;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity)
            _count++;
    }

    public (long Count, double P50Ms, double P95Ms, double P99Ms, double MaxMs) Summarize()
    {
        if (_count == 0)
            return (0, 0, 0, 0, 0);

        var ordered = new double[_count];
        for (var i = 0; i < _count; i++)
            ordered[i] = _samples[(_head - _count + i + Capacity) % Capacity];
        Array.Sort(ordered);

        return (_count, Percentile(ordered, 0.50), Percentile(ordered, 0.95), Percentile(ordered, 0.99), ordered[^1]);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 1)
            return sorted[0];
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

/// <summary>
/// Thread-safe tick duration metrics: invoke ring buffer + per-subscriber ring buffers.
/// </summary>
internal sealed class TickMetrics
{
    private readonly Lock _lock = new();
    private readonly SampleRing _invokeSamples = new();
    private readonly Dictionary<string, SampleRing> _subscriberSamples = new();

    public void RecordInvoke(TimeSpan duration)
    {
        lock (_lock)
        {
            _invokeSamples.Add(duration.TotalMilliseconds);
        }
    }

    public void RecordSubscriber(string name, TimeSpan duration)
    {
        lock (_lock)
        {
            if (!_subscriberSamples.TryGetValue(name, out var ring))
            {
                ring = new SampleRing();
                _subscriberSamples.Add(name, ring);
            }
            ring.Add(duration.TotalMilliseconds);
        }
    }

    public TickMetricsSnapshot Snapshot(int subscriberCount)
    {
        lock (_lock)
        {
            var invoke = _invokeSamples.Summarize();
            var subscribers = new Dictionary<string, SubscriberTickMetrics>(_subscriberSamples.Count);
            foreach (var (name, ring) in _subscriberSamples)
            {
                var s = ring.Summarize();
                subscribers[name] = new SubscriberTickMetrics
                {
                    Name = name,
                    SampleCount = s.Count,
                    P50Ms = s.P50Ms,
                    P95Ms = s.P95Ms,
                    MaxMs = s.MaxMs
                };
            }

            return new TickMetricsSnapshot
            {
                SubscriberCount = subscriberCount,
                InvokeSampleCount = invoke.Count,
                InvokeP50Ms = invoke.P50Ms,
                InvokeP95Ms = invoke.P95Ms,
                InvokeMaxMs = invoke.MaxMs,
                Subscribers = subscribers
            };
        }
    }
}
