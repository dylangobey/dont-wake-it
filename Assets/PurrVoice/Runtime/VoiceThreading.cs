using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PurrNet.Voice
{
    /// <summary>
    /// Platform-aware threading utilities for voice filter processing.
    /// Uses multithreading where supported (Windows, Mac, mobile, consoles);
    /// falls back to main-thread processing on WebGL where System.Threading is unavailable.
    /// </summary>
    public static class VoiceThreading
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        private const bool MultithreadingSupported = false;
#else
        private const bool MultithreadingSupported = true;
#endif

        /// <summary>
        /// True if the current platform supports managed multithreading (Parallel.For, Task.Run, etc.).
        /// False on WebGL; true on standalone, mobile, and consoles.
        /// </summary>
        public static bool IsMultithreadingSupported => MultithreadingSupported;

        /// <summary>
        /// Minimum sample count to consider using parallel processing.
        /// Below this, the overhead of Parallel.For outweighs benefits.
        /// </summary>
        public const int ParallelThreshold = 256;

#if !(UNITY_WEBGL && !UNITY_EDITOR)
        private static readonly ConcurrentQueue<Action> _workQueue = new ConcurrentQueue<Action>();
        private static readonly ManualResetEventSlim _workReady = new ManualResetEventSlim(false);
        private static volatile bool _shutdown;
        private static Thread _worker;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init()
        {
            Shutdown();
            _shutdown = false;

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "PurrVoice.FilterWorker"
            };
            _worker.Start();

            Application.quitting -= OnQuitting;
            Application.quitting += OnQuitting;
        }

        private static void OnQuitting()
        {
            Shutdown();
        }

        private static void Shutdown()
        {
            _shutdown = true;
            _workReady.Set();

            if (_worker != null && _worker.IsAlive)
            {
                _worker.Join(500);
                _worker = null;
            }

            while (_workQueue.TryDequeue(out _)) { }
        }

        private static void WorkerLoop()
        {
            while (!_shutdown)
            {
                _workReady.Wait();
                _workReady.Reset();

                while (_workQueue.TryDequeue(out var work))
                {
                    if (_shutdown)
                        break;

                    try
                    {
                        work();
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
            }
        }
#endif

        /// <summary>
        /// Executes a for loop where each iteration is independent. Uses parallel processing
        /// on supported platforms when count >= ParallelThreshold; otherwise runs sequentially.
        /// </summary>
        public static void For(int fromInclusive, int toExclusive, Action<int> body)
        {
            if (toExclusive <= fromInclusive) return;

            int count = toExclusive - fromInclusive;
            if (!MultithreadingSupported || count < ParallelThreshold)
            {
                for (int i = fromInclusive; i < toExclusive; i++)
                    body(i);
                return;
            }

            Parallel.For(fromInclusive, toExclusive, body);
        }

        /// <summary>
        /// Queues work to the dedicated worker thread. Returns immediately without blocking the caller.
        /// On WebGL, runs synchronously on the calling thread.
        /// </summary>
#pragma warning disable CS0162 // Unreachable code detected
        public static void QueueWork(Action action)
        {
            if (!MultithreadingSupported)
            {
                action();
                return;
            }

            _workQueue.Enqueue(action);
            _workReady.Set();
        }
#pragma warning restore CS0162 // Unreachable code detected
    }
}
