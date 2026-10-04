namespace GreenPaceIQ.Api;

// Generates fake GPS trips and pushes them through the SAME pipeline as real data.
// Advised drivers stop less often and for shorter times (a made-up effect for the demo).
public static class Simulator
{
    public static int Seed(TripPipeline pipeline, int trips, int? seed = 42)
    {
        var rnd = new Random(seed ?? Environment.TickCount);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int created = 0;

        for (int i = 0; i < trips; i++)
        {
            bool advised = i % 2 == 0;
            double t0 = now - rnd.Next(0, 14 * 86400) - 900;
            // 60% of advised trips come from a registered rewards device
            string? device = advised && rnd.NextDouble() < 0.6 ? $"sim_dev_{rnd.Next(1, 121)}" : null;
            var batch = new TripBatch($"sim_{Guid.NewGuid():N}", device, advised, BuildTrip(rnd, advised, t0));
            if (pipeline.Ingest(batch, simulated: true) is not null) created++;
        }

        // Demo driver with a 7-day streak.
        for (int d = 0; d < 7; d++)
        {
            var batch = new TripBatch($"sim_demo_{d}_{Guid.NewGuid():N}", "demo", true, BuildTrip(rnd, true, now - d * 86400.0 - 900));
            if (pipeline.Ingest(batch, simulated: true) is not null) created++;
        }
        return created;
    }

    // Fake rewards history so the city "Rewards programme" page has numbers.
    public static void SeedRewards(TripStore store, int seed = 3)
    {
        var rnd = new Random(seed);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);

        store.AddEarning("demo", new Earning(day, 1100, 0));   // starter balance for the demo driver

        for (int i = 1; i <= 120; i++)
        {
            var device = $"sim_dev_{i}";
            store.AddEarning(device, new Earning(day, rnd.Next(300, 1500), 0));
            if (store.Wallet(device).Balance >= 500 && rnd.NextDouble() < 0.5)
                store.TrySpend(device, 500);
        }
    }

    static double Jit(Random r) => (r.NextDouble() - 0.5) * 0.0002;   // about 10 m

    // One trip passes 3 random signals. Busy signals (Congestion > 1) stop drivers more often and longer.
    static List<GpsPoint> BuildTrip(Random rnd, bool advised, double t0)
    {
        var pts = new List<GpsPoint>();
        double t = t0;
        var route = Signals.All.OrderBy(_ => rnd.Next()).Take(3).ToList();

        foreach (var sig in route)
        {
            for (int i = 0; i < 20; i++)   // cruising towards the signal
                pts.Add(new GpsPoint(t++, sig.Lat - 0.002 + Jit(rnd), sig.Lon + Jit(rnd), 35 + rnd.NextDouble() * 10));

            double pStop = Math.Clamp((advised ? 0.35 : 0.7) * sig.Congestion, 0.1, 0.95);
            if (rnd.NextDouble() < pStop)
            {
                int maxDur = (int)(advised ? 12 + 15 * sig.Congestion : 30 + 20 * sig.Congestion);
                int dur = rnd.Next(8, maxDur + 1);
                double lat = sig.Lat + Jit(rnd), lon = sig.Lon + Jit(rnd);
                for (int i = 0; i < dur; i++)
                    pts.Add(new GpsPoint(t++, lat, lon, rnd.NextDouble() * 1.5));
            }
            pts.Add(new GpsPoint(t++, sig.Lat, sig.Lon, 12));   // pulling away
        }
        return pts;
    }
}
