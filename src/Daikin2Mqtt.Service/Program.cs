using Daikin2Mqtt.Core;
using Daikin2Mqtt.Core.Mqtt;
using Daikin2Mqtt.Daikin;
using Daikin2Mqtt.Service;

var builder = WebApplication.CreateBuilder(args);

var configDirectory = ExternalConfig.AddTo(builder.Configuration, args);

// The HTTP server serves the web interface (Web.Port). When it is disabled it listens on a random loopback port.
var webOptions = builder.Configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();
builder.WebHost.UseUrls(webOptions.Enabled ? new[] { $"http://0.0.0.0:{webOptions.Port}" } : new[] { "http://127.0.0.1:0" });

builder.Services.AddSingleton(new ConfigEditor(configDirectory ?? ExternalConfig.DefaultWritableDirectory()));

builder.Services.AddDaikin2MqttCore(builder.Configuration);
builder.Services.AddDaikin(builder.Configuration);

// Live monitor (web interface): every MQTT message goes through the decorator, Daikin events through the BridgeWorker.
builder.Services.AddSignalR();
builder.Services.AddSingleton<MonitorStream>();
builder.Services.AddSingleton<IMqttPublisher>(sp =>
    new MonitoredMqttPublisher(sp.GetRequiredService<MqttConnection>(), sp.GetRequiredService<MonitorStream>()));

// Start order: MQTT, bridge, units. Stop order is the reverse, so "offline" is published before MQTT disconnects.
builder.Services.AddHostedService<MqttWorker>();
builder.Services.AddHostedService<BridgeWorker>();
builder.Services.AddHostedService<DaikinWorker>();

var app = builder.Build();

if (webOptions.Enabled)
{
    app.UseWebInterface(webOptions);
}

app.Run();

