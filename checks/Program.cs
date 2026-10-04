using GreenPaceIQ.Api;
using Microsoft.Extensions.Configuration;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine($"PASS: {message}");
}
double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
var configuration = new ConfigurationBuilder().Build();
var locations = new LocationStore();
var trips = new TripStore();
Simulator.Seed(new TripPipeline(trips), 400);
var real = new StoredPing(50.061, 19.925, 40, Now(), false);
locations.Add(real);
using var simulator = new LiveTrafficSimulator(locations, configuration);
await simulator.StartAsync(CancellationToken.None);
// StartAsync may schedule ExecuteAsync on a background thread in .NET 10.
for (var i = 0; locations.Snapshot().Count < 100 && i < 100; i++) await Task.Delay(20);
var first = KrakowTraffic.Build(trips, locations, Now());
var count = locations.Snapshot().Count;
await Task.Delay(5500);
var second = KrakowTraffic.Build(trips, locations, Now());
Check(second.Corridors.Zip(first.Corridors).Any(x => x.First.Metrics.SpeedKmh != x.Second.Metrics.SpeedKmh), "corridor speeds change after five seconds");
Check(second.Corridors.Zip(first.Corridors).Any(x => x.First.Heat.Congestion != x.Second.Heat.Congestion), "map congestion heat changes");
Check(second.Corridors.Zip(first.Corridors).Count(x => Math.Abs(x.First.Metrics.SpeedKmh - x.Second.Metrics.SpeedKmh) >= 10) >= 3, "several roads have clearly visible speed changes");
Check(second.Intersections.Zip(first.Intersections).Any(x => x.First.Metrics.SpeedKmh != x.Second.Metrics.SpeedKmh), "intersection speeds change");
Check(locations.Snapshot().Count == count, "simulation does not grow the ping store");
Check(locations.Snapshot().Contains(real), "real observations survive simulation updates");
Check(locations.Snapshot().All(x => x.SpeedKmh is >= 0 and <= 50), "speeds remain within bounds");
await simulator.StopAsync(CancellationToken.None);
foreach (var key in new[] { "Seed:Enabled", "Simulation:Enabled" })
{
    var disabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { [key] = "false" }).Build();
    var empty = new LocationStore();
    using var stopped = new LiveTrafficSimulator(empty, disabled);
    await stopped.StartAsync(CancellationToken.None);
    await Task.Delay(100);
    Check(empty.Snapshot().Count == 0, $"{key}=false disables generation");
    await stopped.StopAsync(CancellationToken.None);
}
