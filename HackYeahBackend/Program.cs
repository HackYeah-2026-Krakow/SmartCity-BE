using GreenPaceIQ.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
builder.Services.AddSingleton<TripStore>();
builder.Services.AddSingleton<TripPipeline>();
builder.Services.AddSingleton<LocationStore>();
builder.Services.AddHostedService<LiveTrafficSimulator>();

var app = builder.Build();
app.UseCors();

// Fill the stores with simulated trips, rewards and driver locations so every screen has data on first run.
if (app.Configuration.GetValue("Seed:Enabled", true))
{
    Simulator.Seed(app.Services.GetRequiredService<TripPipeline>(), app.Configuration.GetValue("Seed:Trips", 400));
    Simulator.SeedRewards(app.Services.GetRequiredService<TripStore>());
    CorridorPingSimulator.Seed(app.Services.GetRequiredService<LocationStore>());
    KrakowPingSimulator.Seed(app.Services.GetRequiredService<LocationStore>());
}

static double NowSec() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

// =====================================================================
// Driver app
// =====================================================================

// Every driver app sends its position every few seconds. Anonymous. Used to measure traffic.
app.MapPost("/api/locations", (LocationPing ping, LocationStore pings) =>
{
    if (ping.Lat is < -90 or > 90 || ping.Lon is < -180 or > 180 || ping.SpeedKmh is < 0 or > 250)
        return Results.BadRequest(new { error = "invalid location" });
    pings.Add(new StoredPing(ping.Lat, ping.Lon, ping.SpeedKmh, NowSec(), false));
    return Results.NoContent();
});

// Main driver endpoint: fuel + distance + speed + traffic + traffic lights for a route.
app.MapPost("/api/route-advice", (RouteRequest req, TripStore trips, LocationStore pings) =>
{
    var error = RouteAdvice.Validate(req);
    return error is not null
        ? Results.BadRequest(new { error })
        : Results.Ok(RouteAdvice.Build(req, trips, pings, NowSec()));
});

// Same, on a built-in demo route (about 6 km, 3 lights). progressM = metres already driven.
app.MapGet("/api/route-advice/demo", (double? progressM, double? currentSpeedKmh, double? nowSec, TripStore trips, LocationStore pings) =>
    Results.Ok(RouteAdvice.Build(new RouteRequest(DemoRoute.Points, progressM, currentSpeedKmh), trips, pings, nowSec ?? NowSec())));

// Screen 01 Live Map (animated demo).
app.MapGet("/api/live-map", (double? positionM, double? nowSec) =>
    Results.Ok(LiveMap.Build(positionM ?? LiveMap.DefaultPositionM, nowSec ?? NowSec())));

// Driver app uploads a finished trip (batch of GPS points). Raw points are discarded after processing.
app.MapPost("/api/trips", (TripBatch batch, TripPipeline pipeline) =>
{
    var error = TripValidator.Validate(batch);
    if (error is not null) return Results.BadRequest(new { error });
    var summary = pipeline.Ingest(batch);
    return summary is null ? Results.Conflict(new { error = "trip already ingested" }) : Results.Ok(summary);
});

// Screen 02 Impact Dashboard.
app.MapGet("/api/impact", (string? deviceId, TripStore store) => Results.Ok(ImpactReports.Build(store, deviceId ?? "demo")));

// Screen 03 Rewards.
app.MapGet("/api/rewards", (string? deviceId, TripStore store) => Results.Ok(RewardsService.Build(store, deviceId ?? "demo")));

app.MapPost("/api/rewards/redeem", (RedeemRequest req, TripStore store) =>
{
    var item = RewardsService.Catalog.FirstOrDefault(r => r.Id == req.RewardId);
    if (item is null) return Results.NotFound(new { error = "unknown reward" });
    var device = string.IsNullOrWhiteSpace(req.DeviceId) ? "demo" : req.DeviceId!;
    return store.TrySpend(device, item.Cost)
        ? Results.Ok(RewardsService.Build(store, device))
        : Results.BadRequest(new { error = "not enough GreenPoints" });
});

// =====================================================================
// City console (web app)
// =====================================================================

// Table / ranking of intersections.
app.MapGet("/api/city/intersections", (TripStore store) => Results.Ok(CityReports.Build(store)));

// Live Traffic page (Leaflet heatmap): same shape as krakowTrafficMock in LiveTrafficPage.jsx.
app.MapGet("/api/city/traffic", (TripStore store, LocationStore pings) =>
    Results.Ok(KrakowTraffic.Build(store, pings, NowSec())));

// City Impact page: stat cards.
app.MapGet("/api/city/summary", (TripStore store, LocationStore pings) =>
    Results.Ok(CityConsole.BuildSummary(store, pings, NowSec())));

// City Impact page: baseline / current / target cards.
app.MapGet("/api/city/impact", (TripStore store) => Results.Ok(CityConsole.BuildImpact(store)));

// Live Traffic page: map. mode = traffic-speed | congestion | air-pollution | signal-delays | greenpace-efficiency
app.MapGet("/api/city/map", (string? mode, TripStore store, LocationStore pings) =>
    Results.Ok(CityConsole.BuildMap(mode, store, pings, NowSec())));

// Rewards programme page.
app.MapGet("/api/city/rewards", (TripStore store) => Results.Ok(CityConsole.BuildRewards(store)));

// Reports page.
app.MapGet("/api/city/reports", () => Results.Ok(new
{
    generatedAt = DateTime.UtcNow,
    available = new[]
    {
        new { id = "intersections", title = "Intersection ranking", format = "csv", url = "/api/city/reports/intersections.csv" },
        new { id = "impact", title = "Impact summary", format = "json", url = "/api/city/impact" },
    }
}));

app.MapGet("/api/city/reports/intersections.csv", (TripStore store) =>
    Results.File(System.Text.Encoding.UTF8.GetBytes(CityConsole.IntersectionsCsv(store)), "text/csv", "intersections.csv"));

// Settings page.
app.MapGet("/api/city/settings", (TripStore store, LocationStore pings) =>
    Results.Ok(CityConsole.BuildSettings(store, pings, NowSec())));

// "How was this calculated?" panel.
app.MapGet("/api/methodology", () => Results.Ok(new
{
    stopRule = $"speed below {Assumptions.StopBelowKmh} km/h for at least {Assumptions.MinStopSec} s, ends above {Assumptions.MoveAboveKmh} km/h",
    fuelFormula = $"stopped_s / 3600 x {Assumptions.IdleLitresPerHour} L/h + stops x {Assumptions.RestartLitres} L",
    tripFuelFormula = $"distance_km x {AdviceAssumptions.CruiseLitresPerKm} L/km + fuelFormula for the waits at red lights",
    co2Formula = $"fuel x {Assumptions.Co2KgPerLitrePetrol} kg/L (petrol)",
    lossFormula = "mean delay x estimated flow / 3600 = vehicle-hours lost per hour",
    trafficRule = $"average speed of other drivers near the route (free >= {AdviceAssumptions.FreeFlowKmh}, moderate >= {AdviceAssumptions.ModerateKmh}, else heavy)",
    privacy = $"intersection statistics shown only if sample size >= {Assumptions.K}",
    isAssumption = true,
    note = "Constants are assumptions, to be replaced with a cited source."
}));

// =====================================================================
// Dev
// =====================================================================

if (app.Environment.IsDevelopment())
    app.MapPost("/api/dev/simulate", (int? trips, TripPipeline p) => Results.Ok(new { created = Simulator.Seed(p, trips ?? 200, null) }));

app.Run();

public record RedeemRequest(string? DeviceId, string RewardId);
