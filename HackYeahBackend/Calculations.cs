namespace GreenPaceIQ.Api;

// All constants are ASSUMPTIONS. Replace with a cited source before submission (HLD A4).
public static class Assumptions
{
    public const double IdleLitresPerHour = 0.8;
    public const double RestartLitres = 0.02;
    public const double Co2KgPerLitrePetrol = 2.31;
    public const double PetrolPricePln = 6.20;     // PLN per litre, assumption
    public const int TripsPerMonth = 20;           // used to scale per-trip averages to a month
    public const double BaselineCo2G = 250;        // fallback baseline per trip until enough data
    public const int K = 5;                        // privacy threshold
    public const double StopBelowKmh = 3, MoveAboveKmh = 5;
    public const double MinStopSec = 3, MaxStopSec = 300;   // longer = parking, not a signal
    public const double MatchRadiusM = 40;
    public const int MonthlyGoalPoints = 1000;
}

public static class Geo
{
    public static double DistanceM(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

// 1. detectStops: speed < 3 km/h for >= 3 s, ends when speed > 5 km/h.
// A stop still open at the end of the trip is dropped (the driver parked).
public static class StopDetector
{
    public static List<Stop> Detect(IReadOnlyList<GpsPoint> pts)
    {
        var stops = new List<Stop>();
        int start = -1;
        for (int i = 0; i < pts.Count; i++)
        {
            var speed = pts[i].SpeedKmh;
            if (start < 0)
            {
                if (speed < Assumptions.StopBelowKmh) start = i;
            }
            else if (speed > Assumptions.MoveAboveKmh)
            {
                var duration = pts[i].T - pts[start].T;
                if (duration >= Assumptions.MinStopSec && duration <= Assumptions.MaxStopSec)
                    stops.Add(new Stop(pts[start].T, duration, pts[start].Lat, pts[start].Lon));
                start = -1;
            }
        }
        return stops;
    }
}

public record TripMetrics(double DistanceM, double DurationSec, double StoppedSec, int StopCount);

public static class TripMath
{
    // 2. tripMetrics
    public static TripMetrics Metrics(IReadOnlyList<GpsPoint> pts, IReadOnlyList<Stop> stops)
    {
        double dist = 0;
        for (int i = 1; i < pts.Count; i++)
            dist += Geo.DistanceM(pts[i - 1].Lat, pts[i - 1].Lon, pts[i].Lat, pts[i].Lon);
        return new TripMetrics(dist, pts[pts.Count - 1].T - pts[0].T, stops.Sum(s => s.DurationSec), stops.Count);
    }
}

public record IntersectionStats(int SampleSize, double MeanDelaySec, double P95DelaySec);
public record HourlyLoss(double VehicleHours, double FuelLitres, double Co2Kg);

public static class TrafficCalculator
{
    // 3. fuelAndCo2
    public static double FuelLitres(double stoppedSeconds, int stopCount) =>
        stoppedSeconds / 3600.0 * Assumptions.IdleLitresPerHour + stopCount * Assumptions.RestartLitres;

    public static double Co2Kg(double fuelLitres) => fuelLitres * Assumptions.Co2KgPerLitrePetrol;

    // 5. intersectionStats (P95 = nearest-rank)
    public static IntersectionStats Stats(IReadOnlyCollection<double> delays)
    {
        if (delays.Count == 0) return new IntersectionStats(0, 0, 0);
        var sorted = delays.OrderBy(x => x).ToList();
        int rank = (int)Math.Ceiling(0.95 * sorted.Count - 1e-9);
        return new IntersectionStats(sorted.Count, sorted.Average(), sorted[Math.Max(0, rank - 1)]);
    }

    // 6. lossScore: mean delay x flow. Assumes `flow` vehicles per hour experience that mean delay
    // (same simplification as HLD A5, so it is an upper-bound estimate).
    public static HourlyLoss LossPerHour(double meanDelaySec, double flowPerHour)
    {
        var lostSec = meanDelaySec * flowPerHour;
        var fuel = lostSec / 3600.0 * Assumptions.IdleLitresPerHour + flowPerHour * Assumptions.RestartLitres;
        return new HourlyLoss(lostSec / 3600.0, fuel, Co2Kg(fuel));
    }
}

// 8. privacyFilter
public static class PrivacyFilter
{
    public static bool IsPublishable(int sampleSize, int k = Assumptions.K) => sampleSize >= k;
}
