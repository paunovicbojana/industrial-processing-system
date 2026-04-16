using IndustrialProcessingSystem.Enums;
using IndustrialProcessingSystem.Models;
using IndustrialProcessingSystem.Services;
using System.Xml.Linq;

// walk up directories until we find the project root (where .csproj lives)
var dir = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(dir, "IndustrialProcessingSystem.csproj")))
{
    dir = Directory.GetParent(dir)!.FullName;
}

var path = Path.Combine(dir, "log.txt");

// SemaphoreSlim(1,1) used instead of lock because we need await inside the handler
// lock cannot be used with await - it would block the thread
var logLock = new SemaphoreSlim(1, 1);

// read config from XML
var xml = XElement.Load("Config/SystemConfig.xml");
var workerCount = (int)xml.Element("WorkerCount")!;
var maxQueueSize = (int)xml.Element("MaxQueueSize")!;

// LINQ query - builds Job objects from each <Job> element in XML
var initialJobs = from element in xml.Element("Jobs")!.Descendants("Job")
                  let type = Enum.Parse<JobType>((string)element.Attribute("Type")!)
                  let payload = (string)element.Attribute("Payload")!
                  let priority = (int)element.Attribute("Priority")!
                  select new Job(Guid.NewGuid(), type, payload, priority);

var system = new ProcessingSystem(workerCount, maxQueueSize, initialJobs, dir);

// subscribe to events using async lambdas
// WaitAsync acquires the semaphore, Release frees it - only one write at a time
system.JobCompleted += async (id, result) => {
    var line = $"[{DateTime.Now}] [COMPLETED] {id}, {result}";
    await logLock.WaitAsync();
    try { await File.AppendAllTextAsync(path, line + Environment.NewLine); }
    finally { logLock.Release(); }
    Console.WriteLine(line);
};

system.JobFailed += async (id, reason) => {
    var line = $"[{DateTime.Now}] [FAILED] {id}, {reason}";
    await logLock.WaitAsync();
    try { await File.AppendAllTextAsync(path, line + Environment.NewLine); }
    finally { logLock.Release(); }
    Console.WriteLine(line);
};

var jobTypes = Enum.GetValues<JobType>();

// one producer thread per worker
var producerThreads = Enumerable.Range(0, workerCount)
    .Select(_ => new Thread(() => {
        while (true)
        {
            var rng = Random.Shared;

            // pick a random job type and build the correct payload format
            var type = jobTypes[rng.Next(jobTypes.Length)];
            var payload = type == JobType.Prime
                ? $"numbers:{rng.Next(10000, 100000)},threads:{rng.Next(1, 9)}"
                : $"delay:{rng.Next(100, 3000)}";
            var priority = rng.Next(1, 6);

            var job = new Job(Guid.NewGuid(), type, payload, priority);

            // Submit returns null if queue is full or job is duplicate
            var handle = system.Submit(job);

            // random delay between submissions to simulate real producer behavior
            Thread.Sleep(rng.Next(200, 1000));
        }
    })).ToList();

// IsBackground = true means threads die automatically when main program exits
producerThreads.ForEach(t => { t.IsBackground = true; t.Start(); });

// keep the app alive - blocks main thread until user presses Enter
Console.ReadLine();