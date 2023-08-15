using System.Diagnostics;
using Backend.Api;
using Backend.Hubs;
using Backend.Infrastructure.Metrics;
using Backend.Infrastructure.Tracing;
using Backend.Models;
using Backend.MQTT;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Proto.OpenTelemetry;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Log = Serilog.Log;

try
{
    var builder = WebApplication.CreateBuilder(args);

    ConfigureLogging(builder);
    ConfigureTracing(builder);
    ConfigureMetrics(builder);

    builder.Services.AddControllers();
    builder.Services.AddSignalR();
    builder.Services.AddRealtimeMapProtoActor();
    builder.Services.AddProtoActorDashboard();
    builder.Services.AddHostedService<MqttIngress>();

    // add map grid for Helsinki region
    builder.Services.AddSingleton(_ => new MapGrid(0.2, 60.0, 24.4, 60.6, 25.6));

    var app2 = builder.Build();

    app2.UseCors(b => b
        .WithOrigins("http://localhost:8080")
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials()
    );

    // for hosting the proto actor dashboard behind a reverse proxy on a subpath
    if (builder.Configuration["PathBase"] != null)
        app2.UsePathBase(builder.Configuration["PathBase"]);

    app2.UseHealthChecks("/healthz");
    app2.UseRouting();
    app2.MapHub<EventsHub>("/events");
    app2.MapOrganizationApi();
    app2.MapTrailApi();
    app2.MapProtoActorDashboard();


    app2.Run();
}
catch (Exception e)
{
    Log.Fatal(e, "Application start-up failed");
    Console.WriteLine("Application start-up failed");
    Console.WriteLine(e);
}
finally
{
    Log.CloseAndFlush();
}

static void ConfigureLogging(WebApplicationBuilder builder)
    => builder.Host.UseSerilog((context, cfg)
        => cfg
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.WithProperty("service", builder.Configuration["Service:Name"])
            .Enrich.WithProperty("env", builder.Environment.EnvironmentName)
            .Enrich.With<TraceIdEnricher>()
    );

static void ConfigureTracing(WebApplicationBuilder builder) =>
    builder.Services.AddOpenTelemetryTracing(b =>
        b.SetResourceBuilder(ResourceBuilder
                .CreateDefault()
                .AddService(builder.Configuration["Service:Name"])
                // add additional "service" tag to facilitate Grafana traces to logs correlation
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("service", builder.Configuration["Service:Name"]),
                    new("env", builder.Environment.EnvironmentName)
                })
            )
            .AddAspNetCoreInstrumentation(opt => opt.RecordException = true)
            .AddMqttInstrumentation()
            .AddSignalRInstrumentation()
            .AddProtoActorInstrumentation()
            .AddOtlpExporter(opt => { opt.Endpoint = new Uri(builder.Configuration["Otlp:Endpoint"]); }));

static void ConfigureMetrics(WebApplicationBuilder builder) =>
    builder.Services.AddOpenTelemetryMetrics(b =>
        b.SetResourceBuilder(ResourceBuilder
                .CreateDefault()
                .AddService(builder.Configuration["Service:Name"])
            )
            .AddAspNetCoreInstrumentation()
            .AddRealtimeMapInstrumentation()
            .AddProtoActorInstrumentation()
            .AddOtlpExporter(opt =>
            {
                opt.Endpoint = new Uri(builder.Configuration["Otlp:Endpoint"]);
                opt.BatchExportProcessorOptions.ScheduledDelayMilliseconds =
                    builder.Configuration.GetValue<int>("Otlp:MetricsIntervalMilliseconds");
            })
    );

public class TraceIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (Activity.Current != null)
        {
            // facilitate Grafana logs to traces correlation
            logEvent.AddOrUpdateProperty(
                propertyFactory.CreateProperty("traceID", Activity.Current.TraceId.ToHexString()));
        }
    }
}