using IndustrialProcessingSystem.Enums;
using IndustrialProcessingSystem.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;

namespace IndustrialProcessingSystem.Services
{
    public class ProcessingSystem
    {
        private readonly string path;
        public event Func<Guid, int, Task>? JobCompleted;
        public event Func<Guid, string, Task>? JobFailed;

        private readonly PriorityQueue<Job, int> queue;
        private readonly HashSet<Guid> submittedJobs = new();
        private readonly Dictionary<Guid, Job> allJobs = new();
        private readonly Dictionary<Guid, TaskCompletionSource<int>> pendingTcs = new();

        private readonly int maxQueueSize;

        private readonly ConcurrentDictionary<JobType, List<long>> executionTimes = new();
        private readonly ConcurrentDictionary<JobType, int> failedCounts = new();
        private readonly object statsLock = new();

        private readonly Timer reportTimer;
        private int reportIndex = 0;
        private readonly object reportLock = new();

        public ProcessingSystem(int workerCount, int maxQueueSize, IEnumerable<Job> initialJobs, string basePath)
        {
            this.maxQueueSize = maxQueueSize;
            this.path = basePath;
            queue = new PriorityQueue<Job, int>();

            for (int i = 0; i < workerCount; i++)
            {
                var worker = new Thread(() => WorkerLoop().GetAwaiter().GetResult());
                worker.IsBackground = true;
                worker.Start();
            }

            reportTimer = new Timer(_ => GenerateReport(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            foreach (var job in initialJobs)
                Submit(job);
        }

        private async Task WorkerLoop()
        {
            Console.WriteLine($"[Worker {Thread.CurrentThread.ManagedThreadId}] started");

            while (true)
            {
                Job job;
                TaskCompletionSource<int> tcs;

                lock (queue)
                {
                    while (queue.Count == 0)
                    {
                        Console.WriteLine($"[Worker {Thread.CurrentThread.ManagedThreadId}] waiting...");
                        Monitor.Wait(queue);
                    }

                    queue.TryDequeue(out job!, out _);
                    pendingTcs.TryGetValue(job.Id, out tcs!);
                }

                Console.WriteLine($"[Worker {Thread.CurrentThread.ManagedThreadId}] processing {job.Id} ({job.Type})");

                if (tcs != null)
                    await Process(job, tcs);
            }
        }

        public JobHandle Submit(Job job)
        {
            lock (queue)
            {
                if (queue.Count >= maxQueueSize)
                    return null!;
                if (submittedJobs.Contains(job.Id))
                    return null!;

                submittedJobs.Add(job.Id);

                var tcs = new TaskCompletionSource<int>();
                
                allJobs[job.Id] = job;
                pendingTcs[job.Id] = tcs;
               
                queue.Enqueue(job, job.Priority);

                Monitor.Pulse(queue); 

                return new JobHandle(job.Id, tcs.Task);
            }
        }

        private async Task Process(Job job, TaskCompletionSource<int> tcs)
        {
            var sw = Stopwatch.StartNew();

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var resultTask = job.Type switch
                    {
                        JobType.Prime => Task.Run(() => ExecutePrime(job.Payload)),
                        JobType.IO => ExecuteIO(job.Payload),
                        _ => throw new InvalidOperationException()
                    };

                    var completed = await Task.WhenAny(resultTask, Task.Delay(2000));

                    if (completed == resultTask)
                    {
                        var result = await resultTask;
                        tcs.TrySetResult(result);
                        
                        sw.Stop();

                        lock (statsLock)
                        {
                            if (!executionTimes.ContainsKey(job.Type))
                                executionTimes[job.Type] = new List<long>();

                            executionTimes[job.Type].Add(sw.ElapsedMilliseconds);
                        }

                        if (JobCompleted != null)
                            await JobCompleted.Invoke(job.Id, result);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (attempt == 2)
                    {
                        tcs.TrySetException(ex);
                        lock (statsLock)
                        {
                            failedCounts.AddOrUpdate(job.Type, 1, (_, v) => v + 1);
                        }
                        if (JobFailed != null)
                            await JobFailed.Invoke(job.Id, "ABORT");
                        return;
                    }
                }
            }

            tcs.TrySetCanceled();
            lock (statsLock)
            {
                failedCounts.AddOrUpdate(job.Type, 1, (_, v) => v + 1);
            }
            if (JobFailed != null)
                await JobFailed.Invoke(job.Id, "ABORT");
        }

        private int ExecutePrime(string payload)
        {
            // payload format: "numbers:10_000,threads:3"
            var parts = payload.Split(',');
            var number = int.Parse(parts[0].Split(":")[1].Replace("_", ""));
            var threads = int.Parse(parts[1].Split(":")[1]);
            threads = Math.Clamp(threads, 1, 8);

            int count = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = threads };

            Parallel.For(2, number + 1, options, (n) =>
            {
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

        private Task<int> ExecuteIO(string payload)
        {
            // payload format: "delay:1_000"
            var delay = int.Parse(payload.Split(':')[1].Replace("_", ""));

            return Task.Run(() =>
            {
                Thread.Sleep(delay);
                return new Random().Next(0, 101);
            });
        }

        public IEnumerable<Job> GetTopJobs(int n)
        {
            lock (queue)
            {
                return queue.UnorderedItems
                    .OrderBy(x => x.Priority)
                    .Take(n)
                    .Select(x => x.Element)
                    .ToList();
            }
        }

        public Job GetJob(Guid id)
        {
            lock (queue)
            {
                allJobs.TryGetValue(id, out var job);
                return job ?? throw new KeyNotFoundException();
            }
        }

        private void GenerateReport()
        {
            lock (reportLock)
            {
                try
                {
                    var reportsDir = Path.Combine(this.path, "Reports");
                    Directory.CreateDirectory(reportsDir);

                    var index = reportIndex++;
                    var fileName = $"report_{index % 10}.xml";
                    var filePath = Path.Combine(reportsDir, fileName);

                    XElement report;

                    lock (statsLock)
                    {
                        report = new XElement("Report",
                            new XAttribute("GeneratedAt", DateTime.Now),

                            executionTimes.Select(kvp =>
                                new XElement("JobType",
                                    new XAttribute("Type", kvp.Key),
                                    new XAttribute("Count", kvp.Value.Count),
                                    new XAttribute("AverageTime",
                                        kvp.Value.Count > 0 ? kvp.Value.Average() : 0),
                                    new XAttribute("Failed",
                                        failedCounts.GetValueOrDefault(kvp.Key))
                                )
                            )
                        );
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
