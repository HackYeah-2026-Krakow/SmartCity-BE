namespace GreenPaceIQ.Api;

public record WalletView(List<Earning> Earnings, int Spent)
{
    public int Balance => Earnings.Sum(e => e.Points) - Spent;
}

public record WalletTotals(int Participants, long PointsIssued, long PointsRedeemed, int Redemptions);

// In-memory store for the MVP. Swap for PostgreSQL later (same method surface).
public class TripStore
{
    class WalletData { public List<Earning> Earnings = new(); public int Spent; public int Redemptions; }

    readonly object _gate = new();
    readonly List<StoredTrip> _trips = new();
    readonly List<StoredStop> _stops = new();
    readonly Dictionary<string, WalletData> _wallets = new();
    readonly HashSet<string> _tripIds = new();

    public bool TryRegisterTrip(string tripId) { lock (_gate) return _tripIds.Add(tripId); }

    public void Add(StoredTrip trip, IEnumerable<StoredStop> stops)
    {
        lock (_gate) { _trips.Add(trip); _stops.AddRange(stops); }
    }

    public (List<StoredTrip> Trips, List<StoredStop> Stops) Snapshot()
    {
        lock (_gate) return (_trips.ToList(), _stops.ToList());
    }

    // Average CO2 (g) of trips WITHOUT advice, used to estimate savings of advised trips.
    public double BaselineCo2G()
    {
        lock (_gate)
        {
            var b = _trips.Where(t => !t.Advised).ToList();
            return b.Count >= Assumptions.K ? b.Average(t => t.Co2Kg) * 1000 : Assumptions.BaselineCo2G;
        }
    }

    WalletData GetOrCreate(string id)
    {
        if (!_wallets.TryGetValue(id, out var w)) _wallets[id] = w = new WalletData();
        return w;
    }

    public void AddEarning(string deviceId, Earning e) { lock (_gate) GetOrCreate(deviceId).Earnings.Add(e); }

    public WalletView Wallet(string deviceId)
    {
        lock (_gate)
        {
            if (!_wallets.TryGetValue(deviceId, out var w)) return new WalletView(new List<Earning>(), 0);
            return new WalletView(w.Earnings.ToList(), w.Spent);
        }
    }

    public bool TrySpend(string deviceId, int cost)
    {
        lock (_gate)
        {
            var w = GetOrCreate(deviceId);
            if (w.Earnings.Sum(e => e.Points) - w.Spent < cost) return false;
            w.Spent += cost;
            w.Redemptions++;
            return true;
        }
    }

    // City-level rewards numbers (totals only, no individual wallets).
    public WalletTotals Totals()
    {
        lock (_gate)
        {
            var active = _wallets.Values.Where(w => w.Earnings.Count > 0).ToList();
            return new WalletTotals(
                active.Count,
                active.Sum(w => (long)w.Earnings.Sum(e => e.Points)),
                active.Sum(w => (long)w.Spent),
                active.Sum(w => w.Redemptions));
        }
    }
}
