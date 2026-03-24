using TestFlightBot;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Bot>();

var host = builder.Build();
await host.RunAsync();

