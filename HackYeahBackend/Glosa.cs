namespace GreenPaceIQ.Api;

public record SignalAhead(Signal Signal, double DistanceM);
public record ArrivalPrediction(int SignalId, double ArrivalInSec, bool Green);
public record Recommendation(int SpeedKmh, int GreenCount, int Total, List<ArrivalPrediction> Arrivals);

// GLOSA-style advice: pick the highest constant speed that makes us arrive on green at the most signals.
public static class Glosa
{
    static double Phase(Signal s, double unixSec)
    {
        var x = unixSec + s.OffsetSec;
        return ((x % s.CycleSec) + s.CycleSec) % s.CycleSec;
    }

    public static bool IsGreen(Signal s, double unixSec) => Phase(s, unixSec) < s.GreenSec;
    public static double SecondsToGreen(Signal s, double unixSec) { var p = Phase(s, unixSec); return p < s.GreenSec ? 0 : s.CycleSec - p; }
    public static double SecondsToRed(Signal s, double unixSec) { var p = Phase(s, unixSec); return p < s.GreenSec ? s.GreenSec - p : 0; }

    public static Recommendation Recommend(IReadOnlyList<SignalAhead> ahead, double nowSec, int minKmh = 20, int maxKmh = 50)
    {
        Recommendation? best = null;
        for (int v = maxKmh; v >= minKmh; v--)
        {
            var mps = v / 3.6;
            var arrivals = ahead.Select(a =>
            {
                var inSec = a.DistanceM / mps;
                return new ArrivalPrediction(a.Signal.Id, inSec, IsGreen(a.Signal, nowSec + inSec));
            }).ToList();
            var greens = arrivals.Count(x => x.Green);
            if (best is null || greens > best.GreenCount)   // strict >, so the higher speed wins ties
                best = new Recommendation(v, greens, ahead.Count, arrivals);
        }
        return best!;
    }
}

public record SignalStateDto(int Id, string Name, double DistanceM, string State, double SecondsToGreen, double SecondsToRed);
public record LiveMapDto(string From, string To, double RemainingKm, double EtaMin, int RecommendedSpeedKmh,
                         int GreenWaveProgressPct, int GreenLightsAhead, List<SignalStateDto> Signals, string Note);

public static class LiveMap
{
    public const double RouteLengthM = 12000;
    public const double DefaultPositionM = 1080;   // puts the 3 demo signals 120 / 450 / 780 m ahead

    public static LiveMapDto Build(double positionM, double nowSec)
    {
        positionM = Math.Clamp(positionM, 0, RouteLengthM);
        var ahead = Signals.All.Where(s => s.RouteOffsetM > positionM)
            .OrderBy(s => s.RouteOffsetM)
            .Select(s => new SignalAhead(s, s.RouteOffsetM - positionM))
            .ToList();

        var rec = Glosa.Recommend(ahead, nowSec);
        var remainingM = RouteLengthM - positionM;
        var etaMin = remainingM / (rec.SpeedKmh / 3.6) / 60.0;

        var signals = ahead.Select(a => new SignalStateDto(
            a.Signal.Id, a.Signal.Name, Math.Round(a.DistanceM),
            Glosa.IsGreen(a.Signal, nowSec) ? "green" : "red",
            Math.Round(Glosa.SecondsToGreen(a.Signal, nowSec)),
            Math.Round(Glosa.SecondsToRed(a.Signal, nowSec)))).ToList();

        var progress = rec.Total == 0 ? 100 : rec.GreenCount * 100 / rec.Total;
        return new LiveMapDto("Your location", "Work", Math.Round(remainingM / 1000, 1), Math.Round(etaMin),
            rec.SpeedKmh, progress, rec.GreenCount, signals,
            "Signal timing is simulated (fixed cycles). Speed advice assumes constant speed.");
    }
}
