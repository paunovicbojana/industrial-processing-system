using IndustrialProcessingSystem.Enums;
using IndustrialProcessingSystem.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace IndustrialProcessingSystem.Services
{
    public class ProcessingSystem
    {
        public event Action<Guid, int>? JobCompleted;
        public event Action<Guid, string>? JobFailed;

        private readonly PriorityQueue<Job, int> queue;
        private readonly HashSet<Guid> executedJobs = new();
        private readonly int maxQueueSize;
        private readonly int workerCount;

        public ProcessingSystem(int workerCount, int maxQueueSize, IEnumerable<Job> initialJobs)
        {

            this.workerCount = workerCount;
            this.maxQueueSize = maxQueueSize;

            queue = new PriorityQueue<Job, int>();

            foreach (var job in initialJobs)
            {
                queue.Enqueue(job, job.Priority);
            }
        }

        public JobHandle Submit(Job job)
        {
            lock (queue)
            {
                if (queue.Count >= maxQueueSize)
                    return null!;

                if (executedJobs.Contains(job.Id))
                    return null!;

                executedJobs.Add(job.Id);
                queue.Enqueue(job, job.Priority);
            }

            var tcs = new TaskCompletionSource<int>();

            Task.Run(async () =>
            {
                await Process(job, tcs);
            });

            return new JobHandle(job.Id, tcs.Task);
        }

        private async Task Process(Job job, TaskCompletionSource<int> tcs)
        {
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
                        tcs.SetResult(result);
                        JobCompleted?.Invoke(job.Id, result);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (attempt == 2)
                    {
                        tcs.SetException(ex);
                        JobFailed?.Invoke(job.Id, "ABORT");
                        return;
                    }
                }
            }

            tcs.SetCanceled();
            JobFailed?.Invoke(job.Id, "ABORT");
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
            return null;
        }

        public Job GetJob(Guid id)
        {
            return null;
        }

    }
}
