using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics.Metrics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache(op =>
{
    op.SizeLimit = 1024; // Set cache size limit
    op.ExpirationScanFrequency = TimeSpan.FromSeconds(10); // Set expiration scan frequency for obsolete items
    op.CompactionPercentage = 0.1; // Set compaction percentage to delete expired items
});
var app = builder.Build();
var meter = new Meter("CachingDemo.Metrics");
var hitsCounter = meter.CreateCounter<int>("cache_hits");
var missesCounter = meter.CreateCounter<int>("cache_misses");
app.MapGet("/", () => "Hello World!");
app.MapGet("/weather", async (IMemoryCache cache) =>
{
    string cacheKey = "weather_data";

    // حاول تجيب من الكاش
    if (!cache.TryGetValue(cacheKey, out string? data))
    {
        // Cache Miss → هنجلب البيانات من المصدر
        Console.WriteLine("❌ Cache MISS — fetching from source...");
        data = await GetWeatherFromSourceAsync();

        // خزّن النتيجة في الكاش
        cache.Set(
            cacheKey,
            data,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30),
                SlidingExpiration = TimeSpan.FromSeconds(10),
                Size = 1
            });
    }
    else
    {
        Console.WriteLine("✅ Cache HIT");
    }

    return Results.Ok(new { Source = data });
});

async Task<string> GetWeatherFromSourceAsync()
{
    await Task.Delay(1500); // simulate slow DB/API
    return $"Weather fetched at {DateTime.Now:T}";
}
//Adding Single Flight Request Caching and SWR (Stale-While-Revalidate)
var inFlightTasks = new Dictionary<string, Task<string>>();

app.MapGet("/weather/swr", async (IMemoryCache cache) =>
{
    string key = "weather_swr";
    if (cache.TryGetValue(key, out string? data))
    {
        hitsCounter.Add(1);

        Console.WriteLine("✅ HIT (stale or fresh)");
        // trigger refresh in background
        _ = Task.Run(async () =>
        {
            if (!inFlightTasks.ContainsKey(key))
            {
                inFlightTasks[key] = GetWeatherFromSourceAsync();
                var newData = await inFlightTasks[key];
                cache.Set(key, newData, TimeSpan.FromSeconds(30));
                inFlightTasks.Remove(key);
                Console.WriteLine("♻️ Background refreshed!");
            }
        });
        return Results.Ok(new { Data = data, Stale = true });
    }

    // cache miss
    missesCounter.Add(1);
    Console.WriteLine("❌ MISS — fetching fresh data...");
    var result = await GetWeatherFromSourceAsync();
    cache.Set(key, result, TimeSpan.FromSeconds(30));
    return Results.Ok(new { Data = result, Stale = false });
});
app.Run();