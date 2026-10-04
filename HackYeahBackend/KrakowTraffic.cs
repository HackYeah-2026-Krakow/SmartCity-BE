namespace GreenPaceIQ.Api;

// =====================================================================
// GET /api/city/traffic : returns EXACTLY the shape of `krakowTrafficMock` in LiveTrafficPage.jsx,
// so the frontend only has to replace the constant with a fetch.
// Geometry (corridors, intersections) is the team's. Numbers come from our simulated trips and driver locations.
// =====================================================================

public record KrakowCorridorDef(string Id, string Name, List<LatLon> Path, double SimSpeedKmh);
public record KrakowIntersectionDef(string Id, string Name, int SignalId);

public static class KrakowNetwork
{
    static LatLon P(double lat, double lon) => new LatLon(lat, lon);

    public static readonly List<KrakowCorridorDef> CorridorDefs = new()
    {
        new("aleje", "Aleje Trzech Wieszczów",
            new List<LatLon> { P(50.0489, 19.9320), P(50.0530, 19.9280), P(50.0577, 19.9262), P(50.0615, 19.9250), P(50.0645, 19.9242), P(50.0700, 19.9238) }, 14),
        new("mogilska-lubicz", "Lubicz / Mogilska",
            new List<LatLon> { P(50.0648, 19.9440), P(50.0652, 19.9490), P(50.0657, 19.9540), P(50.0660, 19.9594), P(50.0657, 19.9650), P(50.0653, 19.9710) }, 16),
        new("dietla", "Dietla",
            new List<LatLon> { P(50.0490, 19.9320), P(50.0520, 19.9370), P(50.0559, 19.9451), P(50.0570, 19.9520), P(50.0576, 19.9591) }, 20),
        new("powstania", "Powstania Warszawskiego",
            new List<LatLon> { P(50.0660, 19.9594), P(50.0635, 19.9592), P(50.0607, 19.9591), P(50.0576, 19.9591) }, 17),
        new("basztowa", "Basztowa / Westerplatte",
            new List<LatLon> { P(50.0653, 19.9345), P(50.0655, 19.9405), P(50.0649, 19.9455), P(50.0626, 19.9485), P(50.0602, 19.9478) }, 23),
        new("konopnickiej", "Konopnickiej",
            new List<LatLon> { P(50.0415, 19.9325), P(50.0450, 19.9322), P(50.0489, 19.9320), P(50.0520, 19.9290) }, 22),
        // extra short corridor so Old Town / Planty has traffic data
        new("planty", "Planty",
            new List<LatLon> { P(50.0600, 19.9345), P(50.0617, 19.9373), P(50.0635, 19.9400) }, 31),
    };

    // Each intersection of the team's map is one of our signals (ids 11-17 in Domain.cs).
    public static readonly List<KrakowIntersectionDef> Intersections = new()
    {
        new("mogilskie", "Rondo Mogilskie", 11),
        new("grzegorzeckie", "Rondo Grzegórzeckie", 12),
        new("grunwaldzkie", "Rondo Grunwaldzkie", 13),
        new("mickiewicza-czarnowiejska", "Mickiewicza × Czarnowiejska", 14),
        new("krasinskiego", "Aleja Krasińskiego", 15),
        new("dietla-starowislna", "Dietla × Starowiślna", 16),
        new("old-town", "Old Town / Planty", 17),
    };

    public const int FirstSignalId = 11;
}

// Fake "other drivers" on the team's corridors (replaced by real pings when they arrive).
public static class KrakowPingSimulator
{
    public static void Seed(LocationStore store, int perCorridor = 120, int seed = 21)
    {
        var rnd = new Random(seed);
        foreach (var c in KrakowNetwork.CorridorDefs)
        {
            var cum = RouteGeometry.Cumulative(c.Path);
            var total = cum[cum.Length - 1];
            for (int i = 0; i < perCorridor; i++)
            {
                var p = RouteGeometry.PointAt(c.Path, cum, rnd.NextDouble() * total);
                var speed = Math.Max(3, c.SimSpeedKmh + (rnd.NextDouble() - 0.5) * 8);
                store.Add(new StoredPing(p.Lat, p.Lon, speed, 0, true));
            }
        }
    }
}

// ---------- Response (same field names as the frontend mock) ----------
// No2 is null: we have no air-quality sensors. co2KgPerHour is our own estimate and drives the airPollution heat.
public record KMetrics(double SpeedKmh, int CongestionPercent, double? No2, int SignalDelaySec, int EfficiencyPercent, double Co2KgPerHour);
public record KHeat(double TrafficSpeed, double Congestion, double AirPollution, double SignalDelays, double GreenEfficiency);
public record KCorridorDto(string Id, string Name, List<double[]> Path, KHeat Heat, KMetrics Metrics);
public record KIntersectionDto(string Id, string Name, double[] Position, KHeat Heat, KMetrics Metrics);
public record KTrafficDto(string Scenario, List<KCorridorDto> Corridors, List<KIntersectionDto> Intersections, bool Simulated, string Note);

public static class KrakowTraffic
{
    static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    static (double? Speed, bool Simulated) PingSpeed(List<StoredPing> all, List<LatLon> path, double[] cum, double nowSec)
    {
        List<double> Near(IEnumerable<StoredPing> src) => src
            .Where(p => RouteGeometry.Project(path, cum, p.Lat, p.Lon).DistM <= AdviceAssumptions.RouteMatchM
                     && !Signals.All.Any(s => Geo.DistanceM(p.Lat, p.Lon, s.Lat, s.Lon) < AdviceAssumptions.SignalExclusionM))
            .Select(p => p.SpeedKmh).ToList();

        var speeds = Near(all.Where(p => !p.Simulated && nowSec - p.T <= AdviceAssumptions.PingWindowSec));
        bool simulated = false;
        if (speeds.Count < Assumptions.K)
        {
            speeds = Near(all.Where(p => p.Simulated));
            simulated = true;
        }
        if (speeds.Count >= Assumptions.K) return (speeds.Average(), simulated);
        return (null, false);
    }

    static KMetrics MakeMetrics(double speed, double delay, double eff, double co2) => new KMetrics(
        Math.Round(speed, 1),
        (int)Math.Round(Math.Clamp(100 * (1 - speed / 50.0), 0, 100)),   // how far below free flow (50 km/h)
        null,
        (int)Math.Round(delay),
        (int)Math.Round(eff),
        Math.Round(co2, 1));

    // Heat is 0..1 for the map. Speed and efficiency: high = good. The others: high = bad.
    static KHeat MakeHeat(KMetrics k) => new KHeat(
        Clamp01((k.SpeedKmh - 5) / 30.0),
        Clamp01(k.CongestionPercent / 100.0),
        Clamp01(k.Co2KgPerHour / 80.0),
        Clamp01(k.SignalDelaySec / 100.0),
        Clamp01(k.EfficiencyPercent / 100.0));

    static double? Saving(SignalMetrics m) =>
        m.MeanAdvised is double a && m.MeanNonAdvised is double n && n > 0 ? (n - a) / n * 100 : (double?)null;

    public static KTrafficDto Build(TripStore store, LocationStore pings, double nowSec)
    {
        var metrics = CityData.PerSignal(store).ToDictionary(x => x.Signal.Id);   // only signals with 5+ stops
        var allPings = pings.Snapshot();
        var (trips, _) = store.Snapshot();

        // City-wide fallbacks for corridors that have no signal with enough data
        var kMetrics = metrics.Values.Where(x => x.Signal.Id >= KrakowNetwork.FirstSignalId).ToList();
        double cityDelay = kMetrics.Count > 0 ? kMetrics.Average(x => x.MeanDelaySec) : 25;
        double cityCo2 = kMetrics.Count > 0 ? kMetrics.Average(x => x.Loss.Co2Kg) : 30;
        double cityEff = kMetrics.Select(x => Saving(x)).Where(v => v != null).Select(v => v!.Value).DefaultIfEmpty(30).Average();

        // Corridors: speed from other drivers' locations, signals found by position
        var states = new List<(KrakowCorridorDef Def, List<int> Ids, double? Speed)>();
        foreach (var def in KrakowNetwork.CorridorDefs)
        {
            var cum = RouteGeometry.Cumulative(def.Path);
            var ids = Signals.All
                .Where(s => RouteGeometry.Project(def.Path, cum, s.Lat, s.Lon).DistM <= AdviceAssumptions.RouteMatchM)
                .Select(s => s.Id).ToList();
            var (speed, _) = PingSpeed(allPings, def.Path, cum, nowSec);
            states.Add((def, ids, speed));
        }
        double citySpeed = states.Where(c => c.Speed != null).Select(c => c.Speed!.Value).DefaultIfEmpty(25).Average();

        var corridors = new List<KCorridorDto>();
        foreach (var c in states)
        {
            var sigs = c.Ids.Where(metrics.ContainsKey).Select(id => metrics[id]).ToList();
            var delay = sigs.Count > 0 ? sigs.Average(x => x.MeanDelaySec) : cityDelay;
            var effs = sigs.Select(x => Saving(x)).Where(v => v != null).Select(v => v!.Value).ToList();
            var eff = effs.Count > 0 ? effs.Average() : cityEff;
            var co2 = sigs.Count > 0 ? sigs.Average(x => x.Loss.Co2Kg) : cityCo2;
            var km = MakeMetrics(c.Speed ?? citySpeed, delay, eff, co2);
            corridors.Add(new KCorridorDto(c.Def.Id, c.Def.Name,
                c.Def.Path.Select(p => new[] { p.Lat, p.Lon }).ToList(), MakeHeat(km), km));
        }

        // Intersections: hidden if fewer than k stops were observed (privacy)
        var intersections = new List<KIntersectionDto>();
        foreach (var def in KrakowNetwork.Intersections)
        {
            if (!metrics.TryGetValue(def.SignalId, out var sm)) continue;
            var speeds = states.Where(c => c.Ids.Contains(def.SignalId) && c.Speed != null).Select(c => c.Speed!.Value).ToList();
            var speed = speeds.Count > 0 ? speeds.Average() : citySpeed;
            var km = MakeMetrics(speed, sm.MeanDelaySec, Saving(sm) ?? cityEff, sm.Loss.Co2Kg);
            intersections.Add(new KIntersectionDto(def.Id, def.Name, new[] { sm.Signal.Lat, sm.Signal.Lon }, MakeHeat(km), km));
        }

        return new KTrafficDto(
            FormattableString.Invariant($"Simulated data · {DateTime.UtcNow:HH:mm} UTC"),
            corridors, intersections, trips.Any(t => t.Simulated),
            "Speed = average of other drivers near the corridor. Congestion % = how far below 50 km/h. no2 is null (no air-quality sensors); airPollution heat uses our CO2 estimate (co2KgPerHour).");
    }
}
