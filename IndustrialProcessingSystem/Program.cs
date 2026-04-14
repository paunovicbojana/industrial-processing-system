using IndustrialProcessingSystem.Enums;
using IndustrialProcessingSystem.Models;
using IndustrialProcessingSystem.Services;
using System.Xml.Linq;

var xml = XElement.Load("Config/SystemConfig.xml");

var workerCount = (int)xml.Element("WorkerCount")!;
var maxQueueSize = (int)xml.Element("MaxQueueSize")!;

var initialJobs = from element in xml.Element("Jobs")!.Descendants("Job")
                  let type = Enum.Parse<JobType>((string)element.Attribute("Type")!)
                  let payload = (string)element.Attribute("Payload")!
                  let priority = (int)element.Attribute("Priority")!
                  select new Job(Guid.NewGuid(), type, payload, priority);

var system = new ProcessingSystem(workerCount, maxQueueSize, initialJobs);

Console.WriteLine($"Pokretanje sistema: {workerCount} niti, max queue: {maxQueueSize}");