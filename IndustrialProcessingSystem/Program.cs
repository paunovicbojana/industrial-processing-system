using IndustrialProcessingSystem.Enums;
using IndustrialProcessingSystem.Models;
using IndustrialProcessingSystem.Services;
using System.Xml.Linq;

var dir = Directory.GetCurrentDirectory();

while (!File.Exists(Path.Combine(dir, "IndustrialProcessingSystem.csproj"))) {
    dir = Directory.GetParent(dir)!.FullName;
}

var path = Path.Combine(dir, "log.txt");

var logLock = new SemaphoreSlim(1, 1);

var xml = XElement.Load("Config/SystemConfig.xml");

var workerCount = (int)xml.Element("WorkerCount")!;
var maxQueueSize = (int)xml.Element("MaxQueueSize")!;

var initialJobs = from element in xml.Element("Jobs")!.Descendants("Job")
                  let type = Enum.Parse<JobType>((string)element.Attribute("Type")!)
                  let payload = (string)element.Attribute("Payload")!
                  let priority = (int)element.Attribute("Priority")!
                  select new Job(Guid.NewGuid(), type, payload, priority);

var system = new ProcessingSystem(workerCount, maxQueueSize, initialJobs, dir);

system.JobCompleted += async (id, result) => {
    var line = $"[{DateTime.Now}] [COMPLETED] {id}, {result}";
    await logLock.WaitAsync();
    try { await File.AppendAllTextAsync(path, line + Environment.NewLine); }
    finally { logLock.Release(); }
    //Console.WriteLine(line);
};

system.JobFailed += async (id, reason) => {
    var line = $"[{DateTime.Now}] [FAILED] {id}, {reason}";
    await logLock.WaitAsync();
    try { await File.AppendAllTextAsync(path, line + Environment.NewLine); }
    finally { logLock.Release(); }
    //Console.WriteLine(line);
};

var jobTypes = Enum.GetValues<JobType>();

var producerThreads = Enumerable.Range(0, workerCount)
    .Select(_ => new Thread(() => {
        while (true) {
            var rng = Random.Shared;
            var type = jobTypes[rng.Next(jobTypes.Length)];
            var payload = type == JobType.Prime
                ? $"numbers:{rng.Next(10000, 100000)},threads:{rng.Next(1, 9)}"
                : $"delay:{rng.Next(100, 3000)}";
            var priority = rng.Next(1, 6);

            var job = new Job(Guid.NewGuid(), type, payload, priority);

            var handle = system.Submit(job);
            if (handle != null)
                //Console.WriteLine($"Submitted {job.Id} ({type}, priority {priority})");

            Thread.Sleep(rng.Next(200, 1000));
        }
    })).ToList();

producerThreads.ForEach(t => { t.IsBackground = true; t.Start(); });

Console.ReadLine();