using Demo;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRequestPool<SampleRequestDispatcher>(options =>
{
    options.MaxConcurrency = 4;
    options.BoundedCapacity = 200;
});

builder.Services.AddHostedService<Worker>();

builder.Build().Run();
