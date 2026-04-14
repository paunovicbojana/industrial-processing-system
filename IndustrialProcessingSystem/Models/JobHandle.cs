using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IndustrialProcessingSystem.Models
{
    public class JobHandle
    {
        private Guid Id { get; init; }
        private Task<int> Result { get; init; }

        public JobHandle(Guid id, Task<int> result)
        {
            Id = id;
            Result = result;
        }
    }
}
