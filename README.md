# SmartCity-BE

Run the API:

```powershell
dotnet run --project .\HackYeahBackend\HackYeahBackend.csproj --launch-profile http
```

By default, seeded demo traffic is refreshed every **5 seconds**. Each road moves through a twenty-second cycle of queueing, moving traffic, free flow and congestion, with speeds around 7, 23, 45 and 16 km/h. Roads use different phases, producing visible changes in speed, congestion and their map colours. Simulated GPS positions also move. `/api/city/traffic`, `/api/city/map`, `/api/city/summary` and route advice read the same location store. Stop-delay and impact aggregates still come from ingested trips.

Each update replaces the simulated fleet atomically rather than accumulating observations. Recent real driver pings are preserved and keep priority when enough measurements are available. Disable updates with `--Simulation:Enabled=false`; `--Seed:Enabled=false` disables both initial demo seeding and live simulation. In Docker use `Simulation__Enabled=false` or `Seed__Enabled=false`.

The frontend must fetch `/api/city/traffic` repeatedly to display changes automatically; an already downloaded response does not update itself.

Build and start the container from this repository root:

```powershell
docker build -t smartcity-be .
docker run --rm -p 8080:8080 smartcity-be
```

Run the generator checks (including an actual five-second update):

```powershell
dotnet run --project .\checks\Checks.csproj
```
