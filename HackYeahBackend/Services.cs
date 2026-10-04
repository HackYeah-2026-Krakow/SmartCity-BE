    using System.Globalization;

    namespace GreenPaceIQ.Api;

    public static class TripValidator
    {
        public static string? Validate(TripBatch b)
        {
            if (string.IsNullOrWhiteSpace(b.TripId)) return "tripId is required";
            if (b.Points is null || b.Points.Count < 2) return "at least 2 points are required";
            if (b.Points.Count > 20000) return "too many points in one batch";
            for (int i = 0; i < b.Points.Count; i++)
            {
                var p = b.Points[i];
                if (p.T < 1_000_000_000) return "t must be unix seconds";
                if (p.SpeedKmh < 0 || p.SpeedKmh > 250) return "speedKmh out of range";
                if (p.Lat is < -90 or > 90 || p.Lon is < -180 or > 180) return "lat/lon out of range";
                if (i > 0 && p.T <= b.Points[i - 1].T) return "points must be strictly increasing in time";
            }
            return null;
        }
    }

    public record TripSummary(string TripId, int Stops, double TimeLostSeconds, double FuelWastedLiters,
                              double Co2EmittedGrams, double DistanceKm, int PointsEarned, string SummaryMessage);

    // Ingest -> detect stops -> metrics -> fuel/CO2 -> match to intersections -> store anonymous aggregates.
    public class TripPipeline
    {
        readonly TripStore _store;
        public TripPipeline(TripStore store) => _store = store;

        public TripSummary? Ingest(TripBatch b, bool simulated = false)
        {
            if (!_store.TryRegisterTrip(b.TripId)) return null;   // duplicate trip id

            var stops = StopDetector.Detect(b.Points);
            var m = TripMath.Metrics(b.Points, stops);
            var fuel = TrafficCalculator.FuelLitres(m.StoppedSec, m.StopCount);
            var co2Kg = TrafficCalculator.Co2Kg(fuel);
            var day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds((long)b.Points[^1].T).UtcDateTime);

            // Points of stops that match a signal are kept as (intersection, hour, duration). No trip link.
            var stored = new List<StoredStop>();
            foreach (var s in stops)
            {
                var sig = Signals.Match(s.Lat, s.Lon);
                if (sig is not null)
                    stored.Add(new StoredStop(sig.Id, (long)(s.StartSec / 3600), s.DurationSec, b.AdvisoryActive, simulated));
            }

            // Rewards: 10 points per shared trip + 1 per 5 g CO2 saved vs baseline (advised trips only).
            int points = 0;
            if (!string.IsNullOrWhiteSpace(b.DeviceId))
            {
                var savedG = b.AdvisoryActive ? Math.Max(0, _store.BaselineCo2G() - co2Kg * 1000) : 0;
                points = 10 + (int)(savedG / 5);
                _store.AddEarning(b.DeviceId!, new Earning(day, points, savedG));
            }

            _store.Add(new StoredTrip(day, b.AdvisoryActive, simulated, m.StopCount, m.StoppedSec, fuel, co2Kg, m.DistanceM, m.DurationSec), stored);

            var co2g = co2Kg * 1000;
            var msg = FormattableString.Invariant($"This trip: {m.StopCount} stops, {m.StoppedSec:F0} s lost, about {fuel:F2} L and {co2g:F0} g CO2");
            return new TripSummary(b.TripId, m.StopCount, Math.Round(m.StoppedSec, 1), Math.Round(fuel, 4),
                                   Math.Round(co2g, 1), Math.Round(m.DistanceM / 1000, 2), points, msg);
        }
    }

    // ---------- City dashboard ----------
    public record IntersectionDto(int Id, string Name, double Lat, double Lon, int SampleSize, double MeanDelaySec, double P95DelaySec,
                                  double VehicleHoursLostPerHour, double FuelLitresPerHour, double Co2KgPerHour,
                                  string Confidence, string Explanation);
    public record CityReport(int K, int HiddenCount, List<IntersectionDto> Items, string Note);

    public static class CityReports
    {
        public static CityReport Build(TripStore store)
        {
            var (_, stops) = store.Snapshot();
            var items = new List<IntersectionDto>();
            int hidden = 0;

            foreach (var sig in Signals.All)
            {
                var delays = stops.Where(s => s.IntersectionId == sig.Id).Select(s => s.DurationSec).ToList();
                var stats = TrafficCalculator.Stats(delays);
                if (!PrivacyFilter.IsPublishable(stats.SampleSize)) { hidden++; continue; }

                var loss = TrafficCalculator.LossPerHour(stats.MeanDelaySec, sig.FlowPerHour);
                var confidence = stats.SampleSize < 20 ? "low" : stats.SampleSize < 100 ? "medium" : "high";
                items.Add(new IntersectionDto(sig.Id, sig.Name, sig.Lat, sig.Lon, stats.SampleSize,
                    Math.Round(stats.MeanDelaySec, 1), Math.Round(stats.P95DelaySec, 1),
                    Math.Round(loss.VehicleHours, 2), Math.Round(loss.FuelLitres, 1), Math.Round(loss.Co2Kg, 1),
                    confidence, Explain(sig.Name, stats, loss)));
            }

            return new CityReport(Assumptions.K, hidden,
                items.OrderByDescending(i => i.VehicleHoursLostPerHour).ToList(),
                "Traffic flow per intersection is an estimate. Intersections with fewer than k observed stops are hidden.");
        }

        // Rule-based text built ONLY from computed numbers. This is where an LLM can be plugged in later,
        // with the same rule: it receives these numbers and may not invent new ones.
        public static string Explain(string name, IntersectionStats s, HourlyLoss l)
        {
            var why = s.P95DelaySec > 2 * s.MeanDelaySec
                ? "Waiting times vary a lot, which can point to a long red phase or queues that do not clear in one cycle."
                : s.MeanDelaySec > 40
                    ? "Typical waits are long, so the signal timing is worth reviewing."
                    : "Waits are moderate and fairly even.";
            return FormattableString.Invariant(
                $"{name}: drivers who stopped waited {s.MeanDelaySec:F0} s on average (95% waited up to {s.P95DelaySec:F0} s), based on {s.SampleSize} observed stops. At the assumed flow this is about {l.VehicleHours:F1} vehicle-hours lost per hour and {l.FuelLitres:F1} L of fuel. {why}");
        }
    }

    // ---------- Impact dashboard ("Before" vs "With GreenPace IQ") ----------
    public record Metric(double Before, double Current, string Unit, double SavingsPct);
    public record ImpactDto(bool InsufficientData, int K, Metric? TimeAtRedLights, Metric? Fuel, Metric? Cost, Metric? Co2,
                            int GreenPointsEarned, double SimulatedSharePct, string Basis);

    public static class ImpactReports
    {
        public static ImpactDto Build(TripStore store, string deviceId)
        {
            var (trips, _) = store.Snapshot();
            var before = trips.Where(t => !t.Advised).ToList();
            var current = trips.Where(t => t.Advised).ToList();
            var wallet = store.Wallet(deviceId);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var monthPoints = wallet.Earnings.Where(e => e.Day.Year == today.Year && e.Day.Month == today.Month).Sum(e => e.Points);
            var simShare = trips.Count == 0 ? 0 : Math.Round(trips.Count(t => t.Simulated) * 100.0 / trips.Count, 0);
            var basis = FormattableString.Invariant($"Average per trip x {Assumptions.TripsPerMonth} trips/month. Compares drivers without advice to drivers with advice (not a controlled experiment).");

            if (before.Count < Assumptions.K || current.Count < Assumptions.K)
                return new ImpactDto(true, Assumptions.K, null, null, null, null, monthPoints, simShare, basis);

            Metric M(Func<StoredTrip, double> f, double scale, string unit, int digits)
            {
                var b = before.Average(f) * scale;
                var c = current.Average(f) * scale;
                var pct = b <= 0 ? 0 : (c - b) / b * 100;
                return new Metric(Math.Round(b, digits), Math.Round(c, digits), unit, Math.Round(pct, 0));
            }

            var n = Assumptions.TripsPerMonth;
            return new ImpactDto(false, Assumptions.K,
                M(t => t.StoppedSec, n / 60.0, "minutes", 0),
                M(t => t.FuelL, n, "litres", 1),
                M(t => t.FuelL * Assumptions.PetrolPricePln, n, "PLN", 2),
                M(t => t.Co2Kg, n, "kg", 1),
                monthPoints, simShare, basis);
        }
    }

    // ---------- Rewards ----------
    public record RewardItem(string Id, string Title, int Cost);
    public record GoalDto(int Current, int Max);
    public record RewardsDto(int Balance, GoalDto MonthlyGoal, int StreakDays, double Co2SavedKg, List<RewardItem> Available);

    public static class RewardsService
    {
        // Demo catalogue. Real rewards need partnerships with the city.
        public static readonly List<RewardItem> Catalog = new()
        {
            new("parking-1h", "1 hour free parking", 500),
            new("transit-day", "Day ticket for public transport", 800),
        };

        public static RewardsDto Build(TripStore store, string deviceId)
        {
            var w = store.Wallet(deviceId);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var month = w.Earnings.Where(e => e.Day.Year == today.Year && e.Day.Month == today.Month).ToList();

            var days = w.Earnings.Select(e => e.Day).ToHashSet();
            var d = days.Contains(today) ? today : today.AddDays(-1);
            int streak = 0;
            while (days.Contains(d)) { streak++; d = d.AddDays(-1); }

            return new RewardsDto(w.Balance, new GoalDto(month.Sum(e => e.Points), Assumptions.MonthlyGoalPoints),
                streak, Math.Round(month.Sum(e => e.Co2SavedG) / 1000, 1), Catalog);
        }
    }
