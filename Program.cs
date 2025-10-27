using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CachingDemo
{
    // ===== Helpers & Types =====
    internal static class CacheHelpers
    {
        // ظرف الكاش: القيمة + وقت التخزين + TTL
        public sealed record CacheEnvelope<T>(T Value, DateTimeOffset CachedAt, TimeSpan Ttl)
        {
            public TimeSpan Age => DateTimeOffset.UtcNow - CachedAt;
            public bool IsStale => Age >= Ttl;
        }

        // TTL مع Jitter لتفادي الـ stampede
        public static TimeSpan WithJitter(TimeSpan ttl, double jitterPercent = 0.20)
        {
            var f = 1 + ((Random.Shared.NextDouble() * 2) - 1) * jitterPercent;
            return TimeSpan.FromMilliseconds(Math.Max(1, ttl.TotalMilliseconds * f));
        }

        // جلب القيمة من المصدر + لفها في Envelope + تسجيل زمن الجلب
        public static async Task<CacheEnvelope<string>> FetchEnvelopeAsync(
            Func<CancellationToken, Task<string>> fetch,
            TimeSpan ttl,
            CancellationToken ct,
            Histogram<double> latencyHist)
        {
            var sw = Stopwatch.StartNew();
            string value = await fetch(ct);
            sw.Stop();
            latencyHist.Record(sw.Elapsed.TotalMilliseconds);

            var ttlWithJitter = WithJitter(ttl);
            return new CacheEnvelope<string>(value, DateTimeOffset.UtcNow, ttlWithJitter);
        }
    }

    internal class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // اقرأ appsettings.json مع reload (اختياري)
            builder.Configuration
                   .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                   .AddEnvironmentVariables();

            // MemoryCache config
            builder.Services.AddMemoryCache(op =>
            {
                op.SizeLimit = 1024;
                op.ExpirationScanFrequency = TimeSpan.FromSeconds(10);
                op.CompactionPercentage = 0.1;
            });

            // Metrics
            var meter = new Meter("CachingDemo.Metrics", "1.0.0");
            var hitsCounter = meter.CreateCounter<long>("cache_hits");
            var missesCounter = meter.CreateCounter<long>("cache_misses");
            var bgRefreshStarted = meter.CreateCounter<long>("cache_bg_refresh_started");
            var bgRefreshCompleted = meter.CreateCounter<long>("cache_bg_refresh_completed");
            var bgRefreshFailed = meter.CreateCounter<long>("cache_bg_refresh_failed");
            var sourceFetchLatency = meter.CreateHistogram<double>("source_fetch_latency_ms");

            // Configurable refreshThresholdRatio (default 0.8)
            double refreshThresholdRatio =
                builder.Configuration.GetValue<double?>("Caching:SWR:RefreshThresholdRatio") ?? 0.8;

            // TTL baseline
            TimeSpan baseTtl = TimeSpan.FromSeconds(30);

            var app = builder.Build();

            app.MapGet("/", () => "Caching Demo OK");

            // ===== مثال بسيط للكاش (/weather) =====
            app.MapGet("/weather", async (IMemoryCache cache) =>
            {
                const string cacheKey = "weather_data";

                if (!cache.TryGetValue(cacheKey, out string? data))
                {
                    Console.WriteLine("❌ Cache MISS — fetching from source...");
                    // مصدر بسيط بدون CancellationToken لهذا المثال فقط
                    data = await Task.Run(async () =>
                    {
                        await Task.Delay(1500);
                        return $"Weather fetched at {DateTime.Now:T}";
                    });

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

            // ===== مصدر بيدعم الإلغاء لاستخدامه مع SWR الذكي =====
            static async Task<string> GetWeatherFromSourceAsync(CancellationToken ct)
            {
                await Task.Delay(1500, ct); // simulate slow DB/API
                return $"Weather fetched at {DateTime.Now:T}";
            }

            // ===== حالة Single-Flight (Thread-safe) =====
            var inFlight = new ConcurrentDictionary<string, Lazy<Task<CacheHelpers.CacheEnvelope<string>>>>(
                StringComparer.Ordinal);

            // ===== SWR الذكي + Single-Flight + Metrics =====
            app.MapGet("/weather/swr-smart", async (IMemoryCache cache, ILoggerFactory lf, HttpContext http) =>
            {
                var logger = lf.CreateLogger("SWR");
                var ct = http.RequestAborted;
                const string key = "weather_swr_v1";

                if (cache.TryGetValue(key, out CacheHelpers.CacheEnvelope<string>? envFromCache))
                {
                    hitsCounter.Add(1);

                    var age = envFromCache!.Age;
                    var ttl = envFromCache.Ttl;
                    bool shouldRefresh = age >= TimeSpan.FromMilliseconds(ttl.TotalMilliseconds * refreshThresholdRatio);

                    var response = new
                    {
                        Data = envFromCache.Value,
                        Stale = envFromCache.IsStale,
                        AgeMs = (int)age.TotalMilliseconds,
                        TtlMs = (int)ttl.TotalMilliseconds,
                        RefreshedInBackground = shouldRefresh,
                        RefreshThresholdRatio = refreshThresholdRatio
                    };

                    if (shouldRefresh)
                    {
                        bgRefreshStarted.Add(1);
                        _ = Task.Run(async () =>
                        {
                            var lazyTask = inFlight.GetOrAdd(
                                key,
                                _ => new Lazy<Task<CacheHelpers.CacheEnvelope<string>>>(
                                    () => CacheHelpers.FetchEnvelopeAsync(GetWeatherFromSourceAsync, baseTtl, CancellationToken.None, sourceFetchLatency),
                                    LazyThreadSafetyMode.ExecutionAndPublication));

                            try
                            {
                                var newEnv = await lazyTask.Value;
                                cache.Set(key, newEnv, new MemoryCacheEntryOptions
                                {
                                    AbsoluteExpirationRelativeToNow = newEnv.Ttl,
                                    Size = 1
                                });
                                bgRefreshCompleted.Add(1);
                                logger.LogInformation("♻️ SWR refresh completed. Age={Age}ms NewTTL={TTL}ms",
                                    (int)newEnv.Age.TotalMilliseconds, (int)newEnv.Ttl.TotalMilliseconds);
                            }
                            catch (Exception ex)
                            {
                                bgRefreshFailed.Add(1);
                                logger.LogWarning(ex, "Background SWR refresh failed for key {Key}", key);
                            }
                            finally
                            {
                                inFlight.TryRemove(key, out _);
                            }
                        });
                    }

                    return Results.Ok(response);
                }

                // MISS: Single-Flight حقيقي — أول طلب يجلب والباقي ينتظر نفس الـ Task
                missesCounter.Add(1);
                var firstLazy = inFlight.GetOrAdd(
                    key,
                    _ => new Lazy<Task<CacheHelpers.CacheEnvelope<string>>>(
                        () => CacheHelpers.FetchEnvelopeAsync(GetWeatherFromSourceAsync, baseTtl, ct, sourceFetchLatency),
                        LazyThreadSafetyMode.ExecutionAndPublication));

                CacheHelpers.CacheEnvelope<string> env;
                try
                {
                    env = await firstLazy.Value;
                }
                catch
                {
                    inFlight.TryRemove(key, out _);
                    throw;
                }

                cache.Set(key, env, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = env.Ttl,
                    Size = 1
                });
                inFlight.TryRemove(key, out _);

                return Results.Ok(new
                {
                    Data = env.Value,
                    Stale = false,
                    AgeMs = (int)env.Age.TotalMilliseconds,
                    TtlMs = (int)env.Ttl.TotalMilliseconds,
                    RefreshedInBackground = false,
                    RefreshThresholdRatio = refreshThresholdRatio
                });
            });

            await app.RunAsync();
        }
    }
}