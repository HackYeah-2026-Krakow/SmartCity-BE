using System.Globalization;

namespace GreenPaceIQ.Api;

// =====================================================================
// City console backend: everything the city web app (React) needs.
// Corridors are the road lines drawn on the map. Signals on a corridor are found automatically
// (a signal within 40 m of the line belongs to it).
// =====================================================================

public record Corridor(string Id, string Name, List<LatLon> Path, double SimSpeedKmh);

public static class Corridors
{
    static LatLon P(double lat, double lon) => new LatLon(lat, lon);

    // Same coordinates as the hardcoded routes in the frontend TrafficMap, plus the demo route.
    public static readonly List<Corridor> All = new()
    {
        new Corridor("route-north", "North corridor",
            new List<LatLon> { P(50.084, 19.91), P(50.075, 19.93), P(50.065, 19.945), P(50.055, 19.96) }, 36),
        new Corridor("route-east", "East corridor",
            new List<LatLon> { P(50.065, 19.91), P(50.065, 19.93), P(50.0647, 19.945), P(50.065, 19.97), P(50.065, 19.99) }, 27),
        new Corridor("route-south", "South corridor",
            new List<LatLon> { P(50.04, 19.945), P(50.05, 19.945), P(50.0647, 19.945), P(50.08, 19.945) }, 38),
        new Corridor("route-diagonal", "Diagonal corridor",
            new List<LatLon> { P(50.044, 19.89), P(50.052, 19.915), P(50.0647, 19.945), P(50.075, 19.968), P(50.085, 19.985) }, 31),
        new Corridor("route-demo", "Demo route", DemoRoute.Points, 32),
    };
}

// Fake "other drivers" on every corridor, so the city traffic map has data. Real pings replace them.
public static class CorridorPingSimulator
{
    public static void Seed(LocationStore store, int perCorridor = 120, int seed = 11)
    {
        var rnd = new Random(seed);
        foreach (var c in Corridors.All)
        {
            var cum = RouteGeometry.Cumulative(c.Path);
            var total = cum[cum.Length - 1];
            for (int i = 0; i < perCorridor; i++)
            {
                var p = RouteGeometry.PointAt(c.Path, cum, rnd.NextDouble() * total);
                var speed = Math.Max(3, c.SimSpeedKmh + (rnd.NextDouble() - 0.5) * 16);
                store.Add(new StoredPing(p.Lat, p.Lon, speed, 0, true));
            }
        }
    }
}

// ---------- Aggregates ----------
public record SignalMetrics(Signal Signal, int Sample, double MeanDelaySec, double? MeanAdvised, double? MeanNonAdvised, HourlyLoss Loss);
public record CorridorState(Corridor Corridor, List<int> SignalIds, double? SpeedKmh, int SpeedSample, bool SpeedSimulated);
public record CityStatsDto(double? AvgSpeedKmh, double AvgDelaySec, int TrafficVolumePerHour, string PollutionLevel);

public static class CityData
{
    // Only signals with at least k observed stops (privacy filter).
    public static List<SignalMetrics> PerSignal(TripStore store)
    {
        var (_, stops) = store.Snapshot();
        var list = new List<SignalMetrics>();
        foreach (var sig in Signals.All)
        {
            var mine = stops.Where(s => s.IntersectionId == sig.Id).ToList();
            if (!PrivacyFilter.IsPublishable(mine.Count)) continue;
            var adv = mine.Where(s => s.Advised).Select(s => s.DurationSec).ToList();
            var non = mine.Where(s => !s.Advised).Select(s => s.DurationSec).ToList();
            var mean = mine.Average(s => s.DurationSec);
            list.Add(new SignalMetrics(sig, mine.Count, mean,
                adv.Count >= Assumptions.K ? adv.Average() : (double?)null,
                non.Count >= Assumptions.K ? non.Average() : (double?)null,
                TrafficCalculator.LossPerHour(mean, sig.FlowPerHour)));
        }
        return list;
    }

    // Average speed of other drivers on each corridor, ignoring pings near lights (they wait at red).
    public static List<CorridorState> States(LocationStore pings, double nowSec)
    {
        var all = pings.Snapshot();
        var result = new List<CorridorState>();
        foreach (var c in Corridors.All)
        {
            var cum = RouteGeometry.Cumulative(c.Path);
            var ids = Signals.All
                .Where(s => RouteGeometry.Project(c.Path, cum, s.Lat, s.Lon).DistM <= AdviceAssumptions.RouteMatchM)
                .Select(s => s.Id).ToList();

            List<double> Near(IEnumerable<StoredPing> src) => src
                .Where(p => RouteGeometry.Project(c.Path, cum, p.Lat, p.Lon).DistM <= AdviceAssumptions.RouteMatchM
                         && !Signals.All.Any(s => Geo.DistanceM(p.Lat, p.Lon, s.Lat, s.Lon) < AdviceAssumptions.SignalExclusionM))
                .Select(p => p.SpeedKmh).ToList();

            var speeds = Near(all.Where(p => !p.Simulated && nowSec - p.T <= AdviceAssumptions.PingWindowSec));
            bool simulated = false;
            if (speeds.Count < Assumptions.K)
            {
                speeds = Near(all.Where(p => p.Simulated));
                simulated = true;
            }
            double? avg = speeds.Count >= Assumptions.K ? speeds.Average() : (double?)null;
            result.Add(new CorridorState(c, ids, avg, speeds.Count, simulated && avg != null));
        }
        return result;
    }

    public static CityStatsDto Stats(List<SignalMetrics> metrics, List<CorridorState> states)
    {
        var speeds = states.Where(s => s.SpeedKmh != null).Select(s => s.SpeedKmh!.Value).ToList();
        double? speed = speeds.Count > 0 ? speeds.Average() : (double?)null;
        var n = metrics.Sum(m => m.Sample);
        var delay = n == 0 ? 0 : metrics.Sum(m => m.MeanDelaySec * m.Sample) / n;
        var volume = metrics.Sum(m => m.Signal.FlowPerHour);
        var co2 = metrics.Count == 0 ? 0 : metrics.Average(m => m.Loss.Co2Kg);
        var level = co2 < 30 ? "Low" : co2 < 55 ? "Moderate" : "High";
        return new CityStatsDto(speed is null ? null : Math.Round(speed.Value, 1), Math.Round(delay, 1), volume, level);
    }
}

// ---------- Map modes (same ids as the frontend tabs) ----------
public record ModeDef(string Id, string Label, string Unit, bool HigherIsWorse, double T0, double T1, double T2, string[] LegendLabels);

public static class Levels
{
    public static readonly string[] Names = { "good", "slowdown", "congestion", "critical" };
    public static readonly string[] Colors = { "#18a866", "#f4bc2b", "#f29a25", "#ef6262" };
    public const string UnknownColor = "#9aa39d";

    public static int Rank(double v, double t0, double t1, double t2, bool higherIsWorse)
    {
        if (higherIsWorse) return v < t0 ? 0 : v < t1 ? 1 : v < t2 ? 2 : 3;
        return v >= t0 ? 0 : v >= t1 ? 1 : v >= t2 ? 2 : 3;
    }
}

// ---------- Response DTOs ----------
public record MapPoint(double Lat, double Lng);
public record LegendItem(string Level, string Label, string Color);
public record MapRouteDto(string Id, string Name, string Level, string Color, double? Value, string Unit, List<MapPoint> Path);
public record MapIntersectionDto(int Id, string Code, string Name, double Lat, double Lng, string Level, string Color,
                                 double DelaySec, string Label, double RadiusM, bool Hotspot, int SampleSize);
public record MapDto(string Mode, string Label, List<LegendItem> Legend, List<MapRouteDto> Routes,
                     List<MapIntersectionDto> Intersections, CityStatsDto Stats, List<string> StatsText, bool Simulated, string Note);

public record StatCardDto(string Id, string Label, string Value, string? Suffix, double? Raw, double? ChangePct, string? Change);
public record SummaryDto(List<StatCardDto> Cards, bool Simulated, string Note);

public record ImpactCardDto(string Id, string Title, string Subtitle, double Baseline, double Current, double Target,
                            double Progress, string Unit, string Badge, double ChangePct);
public record CityImpactDto(bool InsufficientData, List<ImpactCardDto> Cards, double TargetReductionPct, string Basis);

public record CityRewardsDto(int ActiveParticipants, long RewardsIssued, long Redemptions, int RedemptionCount,
                             List<RewardItem> Catalog, bool Simulated);

public record SourceDto(string Name, string Status, bool Connected, bool Simulated);
public record AlertRuleDto(string Id, string Description, int TriggeredNow);
public record SettingsDto(int DataRefreshSec, List<SourceDto> ConnectedSources, List<AlertRuleDto> AlertRules, int PrivacyK);

public static class CityConsole
{
    public const double TargetReductionPct = 60;   // assumption: the city's goal is -60% vs baseline
    public const int RefreshSec = 30;

    static readonly ModeDef[] Modes =
    {
        new("traffic-speed", "Traffic speed", "km/h", false, 35, 28, 20, new[] { "Fast flow", "Slowing", "Slow", "Crawling" }),
        new("congestion", "Congestion", "vehicle-hours lost per hour", true, 2, 5, 9, new[] { "Good flow", "Slowdown", "Congestion", "Critical hotspot" }),
        new("air-pollution", "Air pollution", "kg CO2 per hour", true, 25, 45, 70, new[] { "Clean", "Moderate", "High", "Very high" }),
        new("signal-delays", "Signal delays", "sec", true, 15, 25, 40, new[] { "Short waits", "Noticeable", "Long waits", "Critical delays" }),
        new("greenpace-efficiency", "GreenPace efficiency", "% delay saved", false, 40, 35, 30, new[] { "High efficiency", "Good", "Low", "Very low" }),
    };

    static double? SignalValue(ModeDef m, SignalMetrics s, Dictionary<int, double> speedBySignal) => m.Id switch
    {
        "traffic-speed" => speedBySignal.TryGetValue(s.Signal.Id, out var sp) ? sp : (double?)null,
        "congestion" => s.Loss.VehicleHours,
        "air-pollution" => s.Loss.Co2Kg,
        "signal-delays" => s.MeanDelaySec,
        "greenpace-efficiency" => (s.MeanAdvised is double a && s.MeanNonAdvised is double n && n > 0) ? (n - a) / n * 100 : (double?)null,
        _ => (double?)null
    };

    // ---------- GET /api/city/map?mode= ----------
    public static MapDto BuildMap(string? modeId, TripStore store, LocationStore pings, double nowSec)
    {
        var mode = Modes.FirstOrDefault(m => m.Id == modeId) ?? Modes[1];
        var metrics = CityData.PerSignal(store);
        var states = CityData.States(pings, nowSec);
        var (trips, _) = store.Snapshot();

        var speedBySignal = states
            .Where(s => s.SpeedKmh != null)
            .SelectMany(s => s.SignalIds.Select(id => (id, v: s.SpeedKmh!.Value)))
            .GroupBy(x => x.id)
            .ToDictionary(g => g.Key, g => g.Average(x => x.v));

        (string Level, string Color) LevelOf(double? v)
        {
            if (v is null) return ("unknown", Levels.UnknownColor);
            var r = Levels.Rank(v.Value, mode.T0, mode.T1, mode.T2, mode.HigherIsWorse);
            return (Levels.Names[r], Levels.Colors[r]);
        }

        var routes = new List<MapRouteDto>();
        foreach (var s in states)
        {
            double? value;
            if (mode.Id == "traffic-speed") value = s.SpeedKmh;
            else
            {
                var vals = metrics.Where(m => s.SignalIds.Contains(m.Signal.Id))
                    .Select(m => SignalValue(mode, m, speedBySignal))
                    .Where(v => v != null).Select(v => v!.Value).ToList();
                value = vals.Count > 0 ? vals.Average() : (double?)null;
            }
            var lv = LevelOf(value);
            routes.Add(new MapRouteDto(s.Corridor.Id, s.Corridor.Name, lv.Level, lv.Color,
                value is null ? null : Math.Round(value.Value, 1), mode.Unit,
                s.Corridor.Path.Select(p => new MapPoint(p.Lat, p.Lon)).ToList()));
        }

        var hotId = metrics.OrderByDescending(m => m.Loss.VehicleHours).Select(m => (int?)m.Signal.Id).FirstOrDefault();
        var intersections = metrics.Select(m =>
        {
            var lv = LevelOf(SignalValue(mode, m, speedBySignal));
            return new MapIntersectionDto(m.Signal.Id, m.Signal.Code, m.Signal.Name, m.Signal.Lat, m.Signal.Lon,
                lv.Level, lv.Color, Math.Round(m.MeanDelaySec),
                FormattableString.Invariant($"{m.Signal.Code} · {m.MeanDelaySec:0} sec"),
                Math.Round(Math.Clamp(200 + m.Loss.VehicleHours * 40, 200, 600)),
                hotId == m.Signal.Id, m.Sample);
        }).ToList();

        var legend = Levels.Names.Select((n, i) => new LegendItem(n, mode.LegendLabels[i], Levels.Colors[i])).ToList();

        var stats = CityData.Stats(metrics, states);
        var text = new List<string>
        {
            stats.AvgSpeedKmh is double sp ? FormattableString.Invariant($"{sp:0} km/h") : "n/a",
            FormattableString.Invariant($"{stats.AvgDelaySec:0} sec"),
            FormattableString.Invariant($"{stats.TrafficVolumePerHour:N0} /h"),
            stats.PollutionLevel
        };

        return new MapDto(mode.Id, mode.Label, legend, routes, intersections, stats, text,
            trips.Any(t => t.Simulated) || states.Any(s => s.SpeedSimulated),
            "Colours depend on the mode. Intersections with fewer than k observed stops are hidden. Traffic volume is an estimate.");
    }

    // ---------- GET /api/city/summary ----------
    public static SummaryDto BuildSummary(TripStore store, LocationStore pings, double nowSec)
    {
        var metrics = CityData.PerSignal(store);
        var states = CityData.States(pings, nowSec);
        var stats = CityData.Stats(metrics, states);
        var (trips, stops) = store.Snapshot();
        var totals = store.Totals();

        var adv = stops.Where(s => s.Advised).Select(s => s.DurationSec).ToList();
        var non = stops.Where(s => !s.Advised).Select(s => s.DurationSec).ToList();
        double? delayChange = adv.Count >= Assumptions.K && non.Count >= Assumptions.K
            ? Math.Round((adv.Average() - non.Average()) / non.Average() * 100, 1)
            : (double?)null;
        string? delayText = delayChange is double d
            ? FormattableString.Invariant($"{Math.Abs(d):0.#}% {(d < 0 ? "lower" : "higher")} with GreenPace")
            : null;

        static string F(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);

        var cards = new List<StatCardDto>
        {
            new("avg-speed", "Avg. speed", stats.AvgSpeedKmh is double s1 ? F(s1, "0") : "n/a", "km/h", stats.AvgSpeedKmh, null, null),
            new("avg-delay", "Avg. signal delay", F(stats.AvgDelaySec, "0"), "sec", stats.AvgDelaySec, delayChange, delayText),
            new("traffic-volume", "Traffic volume", F(stats.TrafficVolumePerHour, "N0"), "/h", stats.TrafficVolumePerHour, null, null),
            new("pollution", "Pollution level", stats.PollutionLevel, null, null, null, null),
            new("time-lost", "Time lost at signals", F(metrics.Sum(m => m.Loss.VehicleHours)), "veh-h/h", metrics.Sum(m => m.Loss.VehicleHours), null, null),
            new("fuel-lost", "Fuel wasted", F(metrics.Sum(m => m.Loss.FuelLitres), "0"), "L/h", metrics.Sum(m => m.Loss.FuelLitres), null, null),
            new("co2", "CO2 emitted", F(metrics.Sum(m => m.Loss.Co2Kg), "0"), "kg/h", metrics.Sum(m => m.Loss.Co2Kg), null, null),
            new("drivers", "Active drivers", F(totals.Participants, "N0"), null, totals.Participants, null, null),
        };
        return new SummaryDto(cards, trips.Any(t => t.Simulated),
            "Loss values add up the intersections with enough data and use estimated traffic flow.");
    }

    // ---------- GET /api/city/impact (ImpactMetricCard) ----------
    public static CityImpactDto BuildImpact(TripStore store)
    {
        var impact = ImpactReports.Build(store, "demo");
        if (impact.InsufficientData) return new CityImpactDto(true, new List<ImpactCardDto>(), TargetReductionPct, impact.Basis);

        var cards = new List<ImpactCardDto>();

        void Add(string id, string title, string subtitle, Metric m)
        {
            var target = m.Before * (1 - TargetReductionPct / 100.0);
            var span = m.Before - target;
            var progress = span <= 0 ? 0 : Math.Clamp((m.Before - m.Current) / span * 100, 0, 100);
            var change = m.SavingsPct;
            var badge = change < 0 ? FormattableString.Invariant($"{Math.Abs(change):0.#}% reduction")
                      : change > 0 ? FormattableString.Invariant($"{change:0.#}% increase")
                      : "no change";
            cards.Add(new ImpactCardDto(id, title, subtitle, m.Before, m.Current, Math.Round(target, 2),
                Math.Round(progress, 0), m.Unit, badge, change));
        }

        Add("time-at-red", "Time at red lights", "Per driver per month", impact.TimeAtRedLights!);
        Add("fuel", "Fuel consumption", "Per driver per month", impact.Fuel!);
        Add("cost", "Fuel cost", "Per driver per month", impact.Cost!);
        Add("co2", "CO2 emissions", "Per driver per month", impact.Co2!);

        var (_, stops) = store.Snapshot();
        var adv = stops.Where(s => s.Advised).Select(s => s.DurationSec).ToList();
        var non = stops.Where(s => !s.Advised).Select(s => s.DurationSec).ToList();
        if (adv.Count >= Assumptions.K && non.Count >= Assumptions.K)
        {
            var b = non.Average();
            var c = adv.Average();
            Add("signal-delay", "Signal delay", "Average wait when stopped",
                new Metric(Math.Round(b, 0), Math.Round(c, 0), "sec", Math.Round((c - b) / b * 100, 0)));
        }
        return new CityImpactDto(false, cards, TargetReductionPct, impact.Basis + " Target = baseline minus the city goal (assumption).");
    }

    // ---------- GET /api/city/rewards ----------
    public static CityRewardsDto BuildRewards(TripStore store)
    {
        var t = store.Totals();
        var (trips, _) = store.Snapshot();
        return new CityRewardsDto(t.Participants, t.PointsIssued, t.PointsRedeemed, t.Redemptions,
            RewardsService.Catalog, trips.Any(x => x.Simulated));
    }

    // ---------- GET /api/city/reports/intersections.csv ----------
    public static string IntersectionsCsv(TripStore store)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("code,name,lat,lon,sampleSize,meanDelaySec,p95DelaySec,vehicleHoursLostPerHour,fuelLitresPerHour,co2KgPerHour,confidence");
        foreach (var i in CityReports.Build(store).Items)
        {
            var code = Signals.All.First(s => s.Id == i.Id).Code;
            var name = i.Name.Replace("\"", "\"\"");
            sb.AppendLine(FormattableString.Invariant(
                $"{code},\"{name}\",{i.Lat},{i.Lon},{i.SampleSize},{i.MeanDelaySec},{i.P95DelaySec},{i.VehicleHoursLostPerHour},{i.FuelLitresPerHour},{i.Co2KgPerHour},{i.Confidence}"));
        }
        return sb.ToString();
    }

    // ---------- GET /api/city/settings ----------
    public static SettingsDto BuildSettings(TripStore store, LocationStore pings, double nowSec)
    {
        var metrics = CityData.PerSignal(store);
        var states = CityData.States(pings, nowSec);
        var realPings = pings.Snapshot().Any(p => !p.Simulated && nowSec - p.T <= AdviceAssumptions.PingWindowSec);

        var sources = new List<SourceDto>
        {
            new("Driver app locations", realPings ? "live" : "simulated", true, !realPings),
            new("Driver app trips", "simulated", true, true),
            new("Signal timing", "simulated", true, true),
            new("OpenStreetMap signals", "planned", false, false),
            new("dane.gov.pl datasets", "planned", false, false),
        };
        var rules = new List<AlertRuleDto>
        {
            new("slow-signal", "Intersection average wait above 45 s", metrics.Count(m => m.MeanDelaySec > 45)),
            new("slow-corridor", "Corridor average speed below 20 km/h", states.Count(s => s.SpeedKmh is < 20)),
            new("hotspot", "More than 9 vehicle-hours lost per hour at one intersection", metrics.Count(m => m.Loss.VehicleHours > 9)),
        };
        return new SettingsDto(RefreshSec, sources, rules, Assumptions.K);
    }
}
