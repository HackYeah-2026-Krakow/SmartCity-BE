namespace GreenPaceIQ.Api;

// Refresh the demo fleet as a single snapshot; real driver observations stay intact.
public sealed class LiveTrafficSimulator(LocationStore locations, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Seed:Enabled", true)
            || !configuration.GetValue("Simulation:Enabled", true)) return;

        var frame = 0;
        Update(frame++);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) Update(frame++);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    void Update(int frame)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var pings = new List<StoredPing>();
        var corridors = Corridors.All.Select(c => (c.Path, c.SimSpeedKmh))
            .Concat(KrakowNetwork.CorridorDefs.Select(c => (c.Path, c.SimSpeedKmh)));
        var index = 0;
        foreach (var (path, baselineSpeed) in corridors)
        {
            var cum = RouteGeometry.Cumulative(path);
            var length = cum[^1];
            // A one-minute congestion wave with a different phase on each road.
            var speed = Math.Clamp(baselineSpeed + 14 * Math.Sin(frame * Math.PI / 6 + index++ * 0.9), 5, 48);
            for (var driver = 0; driver < 120; driver++)
            {
                var offset = ((driver + 0.5) / 120 + frame * 0.013) % 1;
                var point = RouteGeometry.PointAt(path, cum, offset * length);
                var driverSpeed = Math.Clamp(speed + 2 * Math.Sin(driver * 1.7), 3, 50);
                pings.Add(new StoredPing(point.Lat, point.Lon, driverSpeed, now, true));
            }
        }
        locations.ReplaceSimulated(pings, now);
    }
}
