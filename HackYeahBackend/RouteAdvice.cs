namespace GreenPaceIQ.Api;

// ---------- Input ----------
public record LatLon(double Lat, double Lon);

// Sent by every driver app every few seconds. Anonymous: no id, no trip link.
public record LocationPing(double Lat, double Lon, double SpeedKmh);

// Route = polyline from any routing provider (OSRM, Google, Mapbox...).
// ProgressM = metres already driven along the route (0 at the start).
public record RouteRequest(List<LatLon> Route, double? ProgressM, double? CurrentSpeedKmh);

public record StoredPing(double Lat, double Lon, double SpeedKmh, double T, bool Simulated);

// ---------- Assumptions (all to be replaced with a cited source) ----------
public static class AdviceAssumptions
{
    public const double CruiseLitresPerKm = 0.07;   // 7 L/100 km while moving
    public const int UrbanLimitKmh = 50;
    public const int MinAdviceKmh = 20;
    public const double FreeFlowKmh = 35, ModerateKmh = 20;   // traffic levels by average speed
    public const double PingWindowSec = 120;
    public const double RouteMatchM = 40;           // ping or signal counts as "on the route"
    public const double SignalExclusionM = 80;      // ignore pings near lights (they wait at red)
}

// ---------- Live locations of other drivers ----------
public class LocationStore
{
    readonly object _gate = new();
    readonly List<StoredPing> _pings = new();

    public void Add(StoredPing p)
    {
        lock (_gate)
        {
            _pings.Add(p);
            if (p.Simulated) return;
            var cutoff = p.T - 600;   // keep real pings 10 minutes only
            _pings.RemoveAll(x => !x.Simulated && x.T < cutoff);
        }
    }

    public void ReplaceSimulated(IEnumerable<StoredPing> pings, double nowSec)
    {
        var replacement = pings.ToList();
        if (replacement.Any(p => !p.Simulated))
            throw new ArgumentException("Only simulated pings can be replaced.", nameof(pings));
        lock (_gate)
        {
            _pings.RemoveAll(p => p.Simulated || p.T < nowSec - 600);
            _pings.AddRange(replacement);
        }
    }

    public List<StoredPing> Snapshot() { lock (_gate) return _pings.ToList(); }
}

// ---------- Route geometry ----------
public static class RouteGeometry
{
    public static double[] Cumulative(IReadOnlyList<LatLon> r)
    {
        var c = new double[r.Count];
        for (int i = 1; i < r.Count; i++)
            c[i] = c[i - 1] + Geo.DistanceM(r[i - 1].Lat, r[i - 1].Lon, r[i].Lat, r[i].Lon);
        return c;
    }

    // Distance from a point to the polyline, and where along the line the closest point is.
    public static (double DistM, double OffsetM) Project(IReadOnlyList<LatLon> r, double[] cum, double lat, double lon)
    {
        double kx = 111320 * Math.Cos(r[0].Lat * Math.PI / 180), ky = 110540;
        double px = (lon - r[0].Lon) * kx, py = (lat - r[0].Lat) * ky;
        double best = double.MaxValue, bestOffset = 0;

        for (int i = 0; i < r.Count - 1; i++)
        {
            double ax = (r[i].Lon - r[0].Lon) * kx, ay = (r[i].Lat - r[0].Lat) * ky;
            double bx = (r[i + 1].Lon - r[0].Lon) * kx, by = (r[i + 1].Lat - r[0].Lat) * ky;
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            double t = len2 < 1e-9 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
            double cx = ax + t * dx, cy = ay + t * dy;
            double d = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            if (d < best) { best = d; bestOffset = cum[i] + t * (cum[i + 1] - cum[i]); }
        }
        return (best, bestOffset);
    }

    public static LatLon PointAt(IReadOnlyList<LatLon> r, double[] cum, double offsetM)
    {
        for (int i = 0; i < r.Count - 1; i++)
        {
            if (offsetM <= cum[i + 1])
            {
                var seg = cum[i + 1] - cum[i];
                var t = seg < 1e-9 ? 0 : (offsetM - cum[i]) / seg;
                return new LatLon(r[i].Lat + t * (r[i + 1].Lat - r[i].Lat), r[i].Lon + t * (r[i + 1].Lon - r[i].Lon));
            }
        }
        return r[r.Count - 1];
    }
}

// Demo route through the 3 example signals (about 6 km). Straight lines between points, for demo only.
public static class DemoRoute
{
    public static readonly List<LatLon> Points = new()
    {
        new LatLon(50.0630, 19.9150),
        new LatLon(Signals.All[2].Lat, Signals.All[2].Lon),
        new LatLon(Signals.All[0].Lat, Signals.All[0].Lon),
        new LatLon(Signals.All[1].Lat, Signals.All[1].Lon),
        new LatLon(50.0380, 19.9820),
    };
}

// Fake "other drivers" on the demo route so traffic is not empty. Real pings replace them when available.
public static class PingSimulator
{
    public static void Seed(LocationStore store, int count = 150, int seed = 7)
    {
        var rnd = new Random(seed);
        var route = DemoRoute.Points;
        var cum = RouteGeometry.Cumulative(route);
        var total = cum[cum.Length - 1];
        for (int i = 0; i < count; i++)
        {
            var p = RouteGeometry.PointAt(route, cum, rnd.NextDouble() * total);
            store.Add(new StoredPing(p.Lat, p.Lon, 22 + rnd.NextDouble() * 20, 0, true));
        }
    }
}

// ---------- Driving model ----------
public record LightOnRoute(Signal Signal, double DistanceM);
public record LightArrival(int SignalId, double ArrivalInSec, double WaitSec);
public record DriveResult(double TravelSec, double WaitSec, int Stops, List<LightArrival> Arrivals);

public static class TripModel
{
    // Drive the remaining route at a constant speed. At each light: if red on arrival, wait until green.
    public static DriveResult Drive(IReadOnlyList<LightOnRoute> lights, double totalM, double speedKmh, double nowSec)
    {
        var mps = Math.Max(1.0, speedKmh / 3.6);
        double t = 0, pos = 0, wait = 0;
        int stops = 0;
        var arrivals = new List<LightArrival>();

        foreach (var l in lights.OrderBy(x => x.DistanceM))
        {
            t += (l.DistanceM - pos) / mps;
            pos = l.DistanceM;
            var w = Glosa.SecondsToGreen(l.Signal, nowSec + t);
            arrivals.Add(new LightArrival(l.Signal.Id, t, w));
            if (w > 0) { t += w; wait += w; stops++; }
        }
        t += Math.Max(0, totalM - pos) / mps;
        return new DriveResult(t, wait, stops, arrivals);
    }

    // fuel = moving fuel for the distance + idling while waiting + restarts
    public static double Fuel(double distanceM, DriveResult d) =>
        distanceM / 1000.0 * AdviceAssumptions.CruiseLitresPerKm + TrafficCalculator.FuelLitres(d.WaitSec, d.Stops);

    // Try every speed from cap down to 20 km/h. Score = fuel + travel time valued like idling,
    // so we do not recommend crawling just to save a tiny wait. Higher speed wins ties.
    public static (int Speed, DriveResult Result) BestSpeed(IReadOnlyList<LightOnRoute> lights, double totalM, double nowSec, int capKmh)
    {
        int min = Math.Min(AdviceAssumptions.MinAdviceKmh, capKmh);
        int bestSpeed = capKmh;
        DriveResult? best = null;
        double bestScore = double.MaxValue;

        for (int v = capKmh; v >= min; v--)
        {
            var r = Drive(lights, totalM, v, nowSec);
            var score = Fuel(totalM, r) + r.TravelSec * Assumptions.IdleLitresPerHour / 3600.0;
            if (score < bestScore - 1e-9) { bestScore = score; bestSpeed = v; best = r; }
        }
        return (bestSpeed, best!);
    }
}

// ---------- Response ----------
public record FuelDto(double EstimatedLitres, double WithoutAdviceLitres, double SavedLitres, double CostPln, double Co2Kg);
public record SpeedDto(int RecommendedKmh, int BaselineKmh, int? TrafficCapKmh);
public record TrafficDto(string Level, double? AvgSpeedKmh, int SampleSize, bool Simulated);
public record StopsDto(int WithAdvice, int WithoutAdvice);
public record LightAdviceDto(int Id, string Name, double DistanceM, string StateNow, double SecondsToGreen, double SecondsToRed,
                             string StateOnArrival, double ArrivalInSec, double WaitSec, double? TypicalWaitSec, int HistorySampleSize);
public record RouteAdviceDto(double DistanceKm, double EtaMin, double EtaMinWithoutAdvice, FuelDto Fuel, SpeedDto Speed,
                             TrafficDto Traffic, StopsDto ExpectedStops, List<LightAdviceDto> TrafficLights, string Note);

public static class RouteAdvice
{
    public static string? Validate(RouteRequest r)
    {
        if (r.Route is null || r.Route.Count < 2) return "route needs at least 2 points";
        if (r.Route.Count > 5000) return "route is too long";
        if (r.Route.Any(p => p.Lat is < -90 or > 90 || p.Lon is < -180 or > 180)) return "lat/lon out of range";
        return null;
    }

    public static RouteAdviceDto Build(RouteRequest req, TripStore trips, LocationStore pings, double nowSec)
    {
        var route = req.Route;
        var cum = RouteGeometry.Cumulative(route);
        var totalRouteM = cum[cum.Length - 1];
        var progress = Math.Clamp(req.ProgressM ?? 0, 0, totalRouteM);
        var remainingM = totalRouteM - progress;

        // 1. Which known traffic lights are on the rest of the route
        var lights = new List<LightOnRoute>();
        foreach (var s in Signals.All)
        {
            var (dist, offset) = RouteGeometry.Project(route, cum, s.Lat, s.Lon);
            if (dist <= AdviceAssumptions.RouteMatchM && offset > progress)
                lights.Add(new LightOnRoute(s, offset - progress));
        }
        lights = lights.OrderBy(l => l.DistanceM).ToList();

        // 2. Traffic from other drivers limits how fast we can go
        var traffic = EstimateTraffic(pings, route, cum, nowSec);
        int cap = AdviceAssumptions.UrbanLimitKmh;
        int? trafficCap = null;
        if (traffic.AvgSpeedKmh is double avg)
        {
            trafficCap = (int)Math.Clamp(Math.Round(avg), 5, cap);
            cap = trafficCap.Value;
        }
        var current = req.CurrentSpeedKmh ?? 0;
        int baseline = current > 5 ? (int)Math.Clamp(Math.Round(current), 5, cap) : cap;

        // 3. Compare "drive as now" with "drive at the recommended speed"
        var (recKmh, rec) = TripModel.BestSpeed(lights, remainingM, nowSec, cap);
        var baseSim = TripModel.Drive(lights, remainingM, baseline, nowSec);
        var fuelRec = TripModel.Fuel(remainingM, rec);
        var fuelBase = TripModel.Fuel(remainingM, baseSim);

        // 4. Per-light details
        var (_, stops) = trips.Snapshot();
        var lightDtos = new List<LightAdviceDto>();
        foreach (var l in lights)
        {
            var arrival = rec.Arrivals.First(a => a.SignalId == l.Signal.Id);
            var hist = stops.Where(x => x.IntersectionId == l.Signal.Id).Select(x => x.DurationSec).ToList();
            double? typical = hist.Count >= Assumptions.K ? Math.Round(hist.Average(), 0) : (double?)null;
            lightDtos.Add(new LightAdviceDto(
                l.Signal.Id, l.Signal.Name, Math.Round(l.DistanceM),
                Glosa.IsGreen(l.Signal, nowSec) ? "green" : "red",
                Math.Round(Glosa.SecondsToGreen(l.Signal, nowSec)), Math.Round(Glosa.SecondsToRed(l.Signal, nowSec)),
                arrival.WaitSec > 0 ? "red" : "green", Math.Round(arrival.ArrivalInSec), Math.Round(arrival.WaitSec),
                typical, hist.Count));
        }

        return new RouteAdviceDto(
            Math.Round(remainingM / 1000, 2),
            Math.Round(rec.TravelSec / 60, 1),
            Math.Round(baseSim.TravelSec / 60, 1),
            new FuelDto(Math.Round(fuelRec, 2), Math.Round(fuelBase, 2), Math.Round(Math.Max(0, fuelBase - fuelRec), 2),
                        Math.Round(fuelRec * Assumptions.PetrolPricePln, 2), Math.Round(TrafficCalculator.Co2Kg(fuelRec), 2)),
            new SpeedDto(recKmh, baseline, trafficCap),
            traffic,
            new StopsDto(rec.Stops, baseSim.Stops),
            lightDtos,
            "Fuel is a model estimate (7 L/100 km moving + idling + restarts, all assumptions). Light timing is simulated. Traffic is the average speed of other app users on the route, simulated until real users send locations.");
    }

    // Average speed of other drivers near the route, ignoring pings close to lights (those wait at red).
    static TrafficDto EstimateTraffic(LocationStore store, IReadOnlyList<LatLon> route, double[] cum, double nowSec)
    {
        var all = store.Snapshot();

        List<double> Near(IEnumerable<StoredPing> src) => src
            .Where(p => RouteGeometry.Project(route, cum, p.Lat, p.Lon).DistM <= AdviceAssumptions.RouteMatchM
                     && !Signals.All.Any(s => Geo.DistanceM(p.Lat, p.Lon, s.Lat, s.Lon) < AdviceAssumptions.SignalExclusionM))
            .Select(p => p.SpeedKmh)
            .ToList();

        var speeds = Near(all.Where(p => !p.Simulated && nowSec - p.T <= AdviceAssumptions.PingWindowSec));
        bool simulated = false;
        if (speeds.Count < Assumptions.K)
        {
            speeds = Near(all.Where(p => p.Simulated));
            simulated = true;
        }
        if (speeds.Count < Assumptions.K) return new TrafficDto("unknown", null, speeds.Count, false);

        var avg = speeds.Average();
        var level = avg >= AdviceAssumptions.FreeFlowKmh ? "free" : avg >= AdviceAssumptions.ModerateKmh ? "moderate" : "heavy";
        return new TrafficDto(level, Math.Round(avg, 1), speeds.Count, simulated);
    }
}
