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
        private Guid Id {  get; init; }
        private JobType Type { get; init; }
        private string Payload { get; init; } = string.Empty;
        private int Priority { get; init; }

        public Job(Guid id, JobType type, string payload, int priority)
        {
            Id = id;
            Type = type;
            Payload = payload;
            Priority = priority;
        }
    }
}
