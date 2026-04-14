using IndustrialProcessingSystem.Enums;
using IndustrialProcessingSystem.Models;
using System;
using System.Collections.Generic;
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
            return null; 
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
