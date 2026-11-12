using ErrorLensLLM.Demo;
using ErrorLensLLM.Serilog;
using Npgsql;
using Serilog;

// A small student information system split into modules, each run as its own process:
//   dotnet run --project samples/ErrorLensLLM.Demo -- --Demo:Module=kayit --urls http://localhost:5101
// A release with a fault: --Demo:Version=1.1.0 --Demo:Faults:0=kayit-missing-column (see Faults)
// A fault that starts later: add --Demo:FaultDelaySeconds=90; a fault without a release: POST /admin/faults/{name}

var builder = WebApplication.CreateBuilder(args);
var demo = builder.Configuration.GetSection(DemoOptions.Section).Get<DemoOptions>() ?? new DemoOptions();
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.Section));

builder.Host.UseSerilog((context, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.WithProperty("Module", demo.Module)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Module}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.ErrorLens(new ErrorLensSinkOptions
    {
        CollectorUrl = demo.CollectorUrl,
        Application = demo.Module,
        Environment = demo.Environment,
        Version = demo.Version
    }));

foreach (var (name, url) in demo.Modules)
    builder.Services.AddHttpClient(name, c => { c.BaseAddress = url; c.Timeout = TimeSpan.FromSeconds(10); });

var connectionString = builder.Configuration.GetConnectionString("Demo")!;
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

if (demo.Module == "traffic")
    builder.Services.AddHostedService<TrafficGenerator>();

var app = builder.Build();

if (demo.Module == "kayit")
    await DemoDatabase.EnsureAsync(connectionString);

Log.Information("{Module} {Version} basladi, aktif hatalar: {Faults}", demo.Module, demo.Version, demo.Faults);

switch (demo.Module)
{
    case "kayit": Modules.MapKayit(app); break;
    case "not": Modules.MapNot(app); break;
    case "odeme": Modules.MapOdeme(app); break;
    case "traffic": break;
    default: throw new InvalidOperationException($"Unknown module '{demo.Module}'. Use kayit, not, odeme or traffic.");
}

app.MapGet("/health", () => Results.Ok(new { module = demo.Module, version = demo.Version, faults = demo.Faults, runtimeFaults = DemoOptions.RuntimeFaults.Keys }));

// Switch a fault on or off without a release, e.g. an external service going down
app.MapPost("/admin/faults/{name}", (string name) => { DemoOptions.RuntimeFaults[name] = true; return Results.NoContent(); });
app.MapDelete("/admin/faults/{name}", (string name) => { DemoOptions.RuntimeFaults.TryRemove(name, out _); return Results.NoContent(); });
app.Run();
