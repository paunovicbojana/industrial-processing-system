using IndustrialProcessingSystem.Enums;
using IndustrialProcessingSystem.Models;
using System.Diagnostics;
using System.Xml.Linq;

namespace IndustrialProcessingSystem.Services
{
    public class ProcessingSystem
    {
        private readonly string path;
        // events - subscribed to in Program.cs using lambda expressions
        public event Func<Guid, int, Task>? JobCompleted;
        public event Func<Guid, string, Task>? JobFailed;

        // priority queue - lower number = higher priority
        private readonly PriorityQueue<Job, int> queue;
        // idempotency - same Id cannot be submitted twice
        private readonly Dictionary<Guid, Job> submittedJobs = new();
        // promises - tcs.Task is what JobHandle holds, completed when worker finishes
        private readonly Dictionary<Guid, TaskCompletionSource<int>> pendingTcs = new();
        private readonly int maxQueueSize;

        // statistics for the report - separate lock to avoid blocking the queue lock
        private readonly Dictionary<JobType, List<long>> executionTimes = new();
        private readonly Dictionary<JobType, int> failedCounts = new();
        private readonly object statsLock = new();

        // timer fires GenerateReport() every 60s
        private readonly Timer reportTimer;
        // % 10 ensures rolling behavior - overwrites oldest file after ten reports
        private int reportIndex = 0;
        private readonly object reportLock = new();

        public ProcessingSystem(int workerCount, int maxQueueSize, IEnumerable<Job> initialJobs, string basePath)
        {
            this.maxQueueSize = maxQueueSize;
            this.path = basePath;
            queue = new PriorityQueue<Job, int>();

            // spin up worker tasks - async, not blocking threads
            for (int i = 0; i < workerCount; i++) Task.Run(() => WorkerLoop());

            // start the report timer
            reportTimer = new Timer(_ => GenerateReport(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            // load initial jobs from XML config
            foreach (var job in initialJobs) Submit(job);
        }

        private async Task WorkerLoop()
        {
            while (true)
            {
                Job job;
                TaskCompletionSource<int> tcs;

                lock (queue)
                {
                    // Monitor.Wait releases the lock and sleeps until Monitor.Pulse wakes it
                    while (queue.Count == 0)
                        Monitor.Wait(queue);

                    queue.TryDequeue(out job!, out _);
                    pendingTcs.TryGetValue(job.Id, out tcs!);
                }

                if (tcs == null) continue;

                await Process(job, tcs);
            }
        }

        public JobHandle Submit(Job job)
        {
            lock (queue)
            {
                // reject if queue is full or job already submitted (idempotency)
                if (queue.Count >= maxQueueSize) return null!;
                if (submittedJobs.ContainsKey(job.Id)) return null!;

                var tcs = new TaskCompletionSource<int>();
                submittedJobs[job.Id] = job;
                pendingTcs[job.Id] = tcs;
                queue.Enqueue(job, job.Priority);

                // wake up one waiting worker
                Monitor.Pulse(queue);
                return new JobHandle(job.Id, tcs.Task);
            }
        }

        private async Task Process(Job job, TaskCompletionSource<int> tcs)
        {
            Exception? lastException = null;

            // 3 attempts total - retry twice on failure
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var sw = Stopwatch.StartNew();

                try
                {
                    var resultTask = job.Type switch
                    {
                        // Prime is CPU-bound so Task.Run offloads it to thread pool
                        JobType.Prime => Task.Run(() => ExecutePrime(job.Payload)),
                        // IO is async - no thread blocked while waiting
                        JobType.IO => ExecuteIO(job.Payload),
                        _ => throw new InvalidOperationException($"Unknown job type: {job.Type}")
                    };

                    // race the job against a 2s timeout - whoever finishes first wins
                    var completed = await Task.WhenAny(resultTask, Task.Delay(2000));

                    if (completed != resultTask) throw new TimeoutException("Job exceeded 2s limit");

                    var result = await resultTask;
                    sw.Stop();

                    // complete the promise so JobHandle.Result unblocks
                    tcs.TrySetResult(result);

                    lock (statsLock)
                    {
                        if (!executionTimes.ContainsKey(job.Type))
                            executionTimes[job.Type] = new List<long>();
                        executionTimes[job.Type].Add(sw.ElapsedMilliseconds);
                    }

                    lock (queue)
                    {
                        pendingTcs.Remove(job.Id);
                        submittedJobs.Remove(job.Id); // clean up - job lifecycle complete
                    }

                    await (JobCompleted?.Invoke(job.Id, result) ?? Task.CompletedTask);
                    return;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    lastException = ex;
                    // wait before retrying
                    if (attempt < 2) await Task.Delay(100);
                }
            }

            // all 3 attempts exhausted - fail the promise
            tcs.TrySetException(lastException!);

            lock (statsLock)
                failedCounts[job.Type] = failedCounts.GetValueOrDefault(job.Type) + 1;

            lock (queue)
            {
                pendingTcs.Remove(job.Id);
                submittedJobs.Remove(job.Id); // clean up even on failure
            }

            // ABORT logged by the event handler in Program.cs
            await (JobFailed?.Invoke(job.Id, "ABORT") ?? Task.CompletedTask);
        }

        private int ExecutePrime(string payload)
        {
            // payload format: "numbers:10_000,threads:3"
            var parts = payload.Split(',');
            var number = int.Parse(parts[0].Split(':')[1].Replace("_", ""));
            // clamp threads to [1,8] as required
            var threads = Math.Clamp(int.Parse(parts[1].Split(':')[1]), 1, 8);

            int count = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = threads };

            // Interlocked.Increment is thread-safe counter increment
            Parallel.For(2, number + 1, options, n => {
                if (IsPrime(n))
                    Interlocked.Increment(ref count);
            });

            return count;
        }

        private bool IsPrime(int n)
        {
            if (n < 2) return false;
            for (int i = 2; i <= Math.Sqrt(n); i++)
                if (n % i == 0) return false;
            return true;
        }

        private async Task<int> ExecuteIO(string payload)
        {
            // payload format: "delay:1_000"
            var delay = int.Parse(payload.Split(':')[1].Replace("_", ""));
            // Task.Delay instead of Thread.Sleep - does not block the thread
            await Task.Delay(delay);
            return Random.Shared.Next(0, 101);
        }

        public IEnumerable<Job> GetTopJobs(int n)
        {
            lock (queue)
                // queue is unordered internally - must sort manually
                return queue.UnorderedItems
                    .OrderBy(x => x.Priority)
                    .Take(n)
                    .Select(x => x.Element)
                    .ToList();
        }

        public Job GetJob(Guid id)
        {
            lock (queue)
            {
                submittedJobs.TryGetValue(id, out var job);
                return job ?? throw new KeyNotFoundException($"Job {id} not found");
            }
        }

        private void GenerateReport()
        {
            Console.WriteLine($"[TOP JOBS @ {DateTime.Now}]");

            int rank = 1;
            foreach (var job in GetTopJobs(5))
                Console.WriteLine($"#{rank++} [{job.Type}] Priority: {job.Priority} Id: {job.Id}");

            lock (reportLock)
            {
                try
                {
                    var reportsDir = Path.Combine(this.path, "Reports");
                    Directory.CreateDirectory(reportsDir);

                    // rolling 10 files - % 10 wraps index back to 0 after 10 reports
                    var fileName = $"report_{reportIndex++ % 10}.xml";
                    var filePath = Path.Combine(reportsDir, fileName);

                    XElement report;

                    lock (statsLock)
                    {
                        // union ensures types with only failures also appear in the report
                        var allTypes = executionTimes.Keys
                            .Union(failedCounts.Keys)
                            .OrderBy(t => t);

                        report = new XElement("Report",
                            new XAttribute("GeneratedAt", DateTime.Now),
                            allTypes.Select(type => {
                                executionTimes.TryGetValue(type, out var times);
                                return new XElement("JobType",
                                    new XAttribute("Type", type),
                                    new XAttribute("Count", times?.Count ?? 0),
                                    new XAttribute("AverageTime", times?.Count > 0 ? times.Average() : 0),
                                    new XAttribute("Failed", failedCounts.GetValueOrDefault(type))
                                );
                            })
                        );

                        // reset stats so next report covers only the past minute
                        executionTimes.Clear();
                        failedCounts.Clear();
                    }

                    report.Save(filePath);
                    Console.WriteLine($"[REPORT] Saved: {filePath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[REPORT ERROR] {ex.Message}");
                }
            }
        }
    }
}