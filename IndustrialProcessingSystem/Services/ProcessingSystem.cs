using IndustrialProcessingSystem.Enums;
using IndustrialProcessingSystem.Models;
using System.Diagnostics;
using System.Xml.Linq;

namespace IndustrialProcessingSystem.Services {
    public class ProcessingSystem {
        private readonly string path;
        public event Func<Guid, int, Task>? JobCompleted;
        public event Func<Guid, string, Task>? JobFailed;

        private readonly PriorityQueue<Job, int> queue;
        private readonly Dictionary<Guid, Job> submittedJobs = new();
        private readonly Dictionary<Guid, TaskCompletionSource<int>> pendingTcs = new();
        private readonly int maxQueueSize;

        private readonly Dictionary<JobType, List<long>> executionTimes = new();
        private readonly Dictionary<JobType, int> failedCounts = new();
        private readonly object statsLock = new();

        private readonly Timer reportTimer;
        private int reportIndex = 0;
        private readonly object reportLock = new();

        public ProcessingSystem(int workerCount, int maxQueueSize, IEnumerable<Job> initialJobs, string basePath) {
            this.maxQueueSize = maxQueueSize;
            this.path = basePath;
            queue = new PriorityQueue<Job, int>();

            for (int i = 0; i < workerCount; i++) Task.Run(() => WorkerLoop());

            reportTimer = new Timer(_ => GenerateReport(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            foreach (var job in initialJobs) Submit(job);
        }

        private async Task WorkerLoop() {
            Console.WriteLine($"[Worker {Thread.CurrentThread.ManagedThreadId}] started");

            while (true) {
                Job job;
                TaskCompletionSource<int> tcs;

                lock (queue) {
                    while (queue.Count == 0) {
                        Console.WriteLine($"[Worker {Thread.CurrentThread.ManagedThreadId}] waiting...");
                        Monitor.Wait(queue);
                    }

                    queue.TryDequeue(out job!, out _);
                    pendingTcs.TryGetValue(job.Id, out tcs!);
                }

                if (tcs == null) {
                    Console.WriteLine($"[Worker] No TCS found for job {job.Id}, skipping");
                    continue;
                }

                Console.WriteLine($"[Worker {Thread.CurrentThread.ManagedThreadId}] processing {job.Id} ({job.Type})");
                await Process(job, tcs);
            }
        }

        public JobHandle Submit(Job job) {
            lock (queue) {
                if (queue.Count >= maxQueueSize) return null!;
                if (submittedJobs.ContainsKey(job.Id)) return null!;

                var tcs = new TaskCompletionSource<int>();
                submittedJobs[job.Id] = job;
                pendingTcs[job.Id] = tcs;
                queue.Enqueue(job, job.Priority);

                Monitor.Pulse(queue);
                return new JobHandle(job.Id, tcs.Task);
            }
        }

        private async Task Process(Job job, TaskCompletionSource<int> tcs) {
            Exception? lastException = null;

            for (int attempt = 0; attempt < 3; attempt++) {
                var sw = Stopwatch.StartNew();

                try {
                    var resultTask = job.Type switch {
                        JobType.Prime => Task.Run(() => ExecutePrime(job.Payload)),
                        JobType.IO => ExecuteIO(job.Payload),
                        _ => throw new InvalidOperationException($"Unknown job type: {job.Type}")
                    };

                    var completed = await Task.WhenAny(resultTask, Task.Delay(2000));

                    if (completed != resultTask) throw new TimeoutException("Job exceeded 2s limit");

                    var result = await resultTask;
                    sw.Stop();

                    tcs.TrySetResult(result);

                    lock (statsLock) {
                        if (!executionTimes.ContainsKey(job.Type))
                            executionTimes[job.Type] = new List<long>();
                        executionTimes[job.Type].Add(sw.ElapsedMilliseconds);
                    }

                    lock (queue) 
                        pendingTcs.Remove(job.Id);

                    await (JobCompleted?.Invoke(job.Id, result) ?? Task.CompletedTask);
                    return;
                }
                catch (Exception ex) {
                    sw.Stop();
                    lastException = ex;
                    Console.WriteLine($"[Worker] Job {job.Id} attempt {attempt + 1} failed: {ex.Message}");

                    if (attempt < 2) await Task.Delay(100);
                }
            }

            // abort when all 3 attempts are exhausted
            tcs.TrySetException(lastException!);

            lock (statsLock)
                failedCounts[job.Type] = failedCounts.GetValueOrDefault(job.Type) + 1;

            lock (queue) pendingTcs.Remove(job.Id);

            await (JobFailed?.Invoke(job.Id, "ABORT") ?? Task.CompletedTask);
        }

        private int ExecutePrime(string payload) {
            // payload format: "numbers:10_000,threads:3"
            var parts = payload.Split(',');
            var number = int.Parse(parts[0].Split(':')[1].Replace("_", ""));
            var threads = Math.Clamp(int.Parse(parts[1].Split(':')[1]), 1, 8);

            int count = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = threads };

            Parallel.For(2, number + 1, options, n => {
                if (IsPrime(n))
                    Interlocked.Increment(ref count);
            });

            return count;
        }

        private bool IsPrime(int n) {
            if (n < 2) return false;
            for (int i = 2; i <= Math.Sqrt(n); i++)
                if (n % i == 0) return false;
            return true;
        }

        private async Task<int> ExecuteIO(string payload) {
            // payload format: "delay:1_000"
            var delay = int.Parse(payload.Split(':')[1].Replace("_", ""));
            await Task.Delay(delay);
            return Random.Shared.Next(0, 101);
        }

        public IEnumerable<Job> GetTopJobs(int n) {
            lock (queue)
                return queue.UnorderedItems
                    .OrderBy(x => x.Priority)
                    .Take(n)
                    .Select(x => x.Element)
                    .ToList();
        }

        public Job GetJob(Guid id) {
            lock (queue) {
                submittedJobs.TryGetValue(id, out var job);
                return job ?? throw new KeyNotFoundException($"Job {id} not found");
            }
        }

        private void GenerateReport() {
            lock (reportLock) {
                try {
                    var reportsDir = Path.Combine(this.path, "Reports");
                    Directory.CreateDirectory(reportsDir);

                    var fileName = $"report_{reportIndex++ % 10}.xml";
                    var filePath = Path.Combine(reportsDir, fileName);

                    XElement report;

                    lock (statsLock) {
                        // for types with only failures to appear in the report
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

                        // reset so next report covers only the past minute
                        executionTimes.Clear();
                        failedCounts.Clear();
                    }

                    report.Save(filePath);
                    Console.WriteLine($"[REPORT] Saved: {filePath}");
                }
                catch (Exception ex) {
                    Console.WriteLine($"[REPORT ERROR] {ex.Message}");
                }
            }
        }
    }
}