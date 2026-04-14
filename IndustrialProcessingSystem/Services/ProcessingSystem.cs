using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IndustrialProcessingSystem.Models;

namespace IndustrialProcessingSystem.Services
{
    public class ProcessingSystem
    {
        public event Action<Guid, int>? JobCompleted;
        public event Action<Guid, string>? JobFailed;
        private PriorityQueue<Job, int>? queue;

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
