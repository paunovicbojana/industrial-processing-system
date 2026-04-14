using IndustrialProcessingSystem.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IndustrialProcessingSystem.Models
{
    public class Job
    {
        public Guid Id {  get; init; }
        public JobType Type { get; init; }
        public string Payload { get; init; } = string.Empty;
        public int Priority { get; init; }

        public Job(Guid id, JobType type, string payload, int priority)
        {
            Id = id;
            Type = type;
            Payload = payload;
            Priority = priority;
        }
    }
}
