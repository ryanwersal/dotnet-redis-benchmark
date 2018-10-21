using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Bogus;
using ByteSizeLib;
using Microsoft.Extensions.CommandLineUtils;
using Serilog;
using Serilog.Events;
using StackExchange.Redis;

namespace DotnetRedisBenchmark
{
    class Program
    {
        static void Main(string[] args)
        {
            var app = new CommandLineApplication
            {
                Name = "redis-benchmark clone using StackExchange.Redis"
            };
            app.HelpOption("-?|--help");

            var hostOption = app.Option("-h", "Server hostname (default 127.0.0.1)", CommandOptionType.SingleValue, false);
            var portOption = app.Option("-p", "Server port (default 6379)", CommandOptionType.SingleValue, false);
            var socketOption = app.Option("-s", "Server socket (overrides host and port)", CommandOptionType.SingleValue, false);
            var passwordOption = app.Option("-a", "Password for Redis Auth", CommandOptionType.SingleValue, false);
            var clientsOption = app.Option("-c", "Number of parallel connections (default 50)", CommandOptionType.SingleValue, false);
            var requestsOption = app.Option("-n", "Total number of requests (default 100000)", CommandOptionType.SingleValue, false);
            var sizeOption = app.Option("-d", "Data size of SET/GET value, specified  (default 2B)", CommandOptionType.SingleValue, false);
            var dbNumOption = app.Option("--dbnum", "SELECT the specified db number (default 0)", CommandOptionType.SingleValue, false);
            var keepAliveOption = app.Option("-k", "1=keep alive 0=reconnect (default 1)", CommandOptionType.SingleValue, false);
            var keyspaceLenOption = app.Option("-r", @"Use random keys for SET/GET/INCR, random values for SADD
  Using this option the benchmark will expand the string __rand_int__
  inside an argument with a 12 digits number in the specified range
  from 0 to keyspacelen - 1.The substitution changes every time a command
  is executed.Default tests use this to hit random keys in the
  specified range.", CommandOptionType.SingleValue, false);
            var pipelineRequestsOption = app.Option("-P", "Pipeline <numreq> requests. Default 1 (no pipeline)", CommandOptionType.SingleValue, false);
            var quietOption = app.Option("-q", "Quiet. Just show query/sec values", CommandOptionType.NoValue, false);
            var csvOption = app.Option("--csv", "Output in CSV format", CommandOptionType.NoValue, false);
            var loopOption = app.Option("-l", "Loop. Run the tests forever", CommandOptionType.NoValue, false);
            var testsOption = app.Option("-t", "Only run the comma separated list of tests. The test names are the same as the ones produced as output", CommandOptionType.SingleValue, false);
            var idleOption = app.Option("-I", "Idle mode. Just open N idle connections and wait.", CommandOptionType.NoValue, false);

            app.OnExecute(async () =>
            {
                var config = new BenchmarkConfiguration();

                if (socketOption.HasValue())
                {
                    var values = socketOption.Value().Split(":");
                    config.Endpoint = new IPEndPoint(IPAddress.Parse(values[0]), int.Parse(values[1]));
                }
                else if (hostOption.HasValue() || portOption.HasValue())
                {
                    var host = IPAddress.Parse(hostOption.HasValue() ? hostOption.Value() : "127.0.0.1");
                    var port = int.Parse(portOption.HasValue() ? portOption.Value() : "6379");
                    config.Endpoint = new IPEndPoint(host, port);
                }

                //if (testsOption.HasValue()) 
                //{
                //    var tests = testsOption.Value().Split(",");
                //    config.Benchmarks = new HashSet<BenchmarkTests>();
                //    foreach (var test in testsOption.Value().Split(","))
                //    {
                //        config.Benchmarks.Add(Enum.Parse<BenchmarkTests>(test));
                //    }
                //}

                if (passwordOption.HasValue()) config.Password = passwordOption.Value();
                if (clientsOption.HasValue()) config.ClientCount = int.Parse(clientsOption.Value());
                if (requestsOption.HasValue()) config.RequestCount = int.Parse(requestsOption.Value());
                if (sizeOption.HasValue()) config.ValueSize = ByteSize.Parse(sizeOption.Value());
                if (dbNumOption.HasValue()) config.DatabaseNumber = int.Parse(dbNumOption.Value());
                if (keepAliveOption.HasValue()) config.KeepAlive = Enum.Parse<KeepAliveOptions>(keepAliveOption.Value());
                if (keyspaceLenOption.HasValue()) config.KeyspaceLength = int.Parse(keyspaceLenOption.Value());
                if (pipelineRequestsOption.HasValue()) config.PipelineRequestCount = int.Parse(pipelineRequestsOption.Value());
                if (quietOption.HasValue()) config.QuietOutput = true;
                if (csvOption.HasValue()) config.UseCsvOutput = true;
                if (loopOption.HasValue()) config.LoopIndefinitely = true;
                if (idleOption.HasValue()) config.IdleConnections = true;

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Is(config.QuietOutput ? LogEventLevel.Information : LogEventLevel.Verbose)
                    .WriteTo.Console()
                    .CreateLogger();

                await ExecuteBenchmark(config);

                return 0;
            });

            app.Execute(args);
        }

        private static async Task ExecuteBenchmark(BenchmarkConfiguration config)
        {
            // redis-benchmark -h 10.0.75.1 -p 31002 -q -n 100000
            var cts = new CancellationTokenSource();

            var clientTasks = Enumerable.Range(0, config.ClientCount).Select(clientNumber =>
            {
                return Task.Factory.StartNew(
                    () => CreateClient(clientNumber, config, cts.Token),
                    cts.Token, 
                    TaskCreationOptions.LongRunning, 
                    TaskScheduler.Default
                ).Unwrap();
            });

            await Task.WhenAll(clientTasks);
        }

        private static async Task ExecuteTest(
            string operation, Func<Task> testFunc, int clientNumber, BenchmarkConfiguration config, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            for (var i = 0; i < config.RequestCount; ++i)
            {
                if (i % 100 == 0)
                {
                    if (Log.IsEnabled(LogEventLevel.Debug))
                    {
                        Log.Verbose("Client #{ClientNumber} has sent {Number} of {Total} {Operation}.",
                                    clientNumber, i, config.RequestCount, operation);
                    }

                    if (ct.IsCancellationRequested) break;
                }

                await testFunc();
            }

            stopwatch.Stop();

            var requestsPerSecond = config.RequestCount / stopwatch.Elapsed.TotalSeconds;

            Log.Information("{Operation}: {RequestPerSecond} requests per second", operation, requestsPerSecond);
        }

        private static async Task CreateClient(int clientNumber, BenchmarkConfiguration config, CancellationToken ct)
        {
            Log.Verbose("Creating client #{ClientNumber}...", clientNumber);

            var options = new ConfigurationOptions
            {
                Password = config.Password,
                ClientName = "redis-benchmark-client",
                DefaultDatabase = config.DatabaseNumber,
            };
            options.EndPoints.Add(config.Endpoint);

            using (var connection = await ConnectionMultiplexer.ConnectAsync(options))
            {
                var db = connection.GetDatabase();

                var faker = new Faker();
                var seedDateTimeOffset = faker.Date.BetweenOffset(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
                var rnd = new Random((int)seedDateTimeOffset.ToUnixTimeSeconds());
                var bytes = new byte[(int)config.ValueSize.Bytes];

                await ExecuteTest("PING", async () =>
                {
                    await db.PingAsync();
                }, clientNumber, config, ct);

                await ExecuteTest("SET", async () => 
                {
                    rnd.NextBytes(bytes);
                    await db.StringSetAsync(GetKey(rnd, config.KeyspaceLength, "foo"), bytes);
                }, clientNumber, config, ct);

                await ExecuteTest("GET", async () =>
                {
                    await db.StringGetAsync(GetKey(rnd, config.KeyspaceLength, "foo"));
                }, clientNumber, config, ct);
            }

            Log.Verbose("Client #{ClientNumber} finished.", clientNumber);
        }

        private static string GetKey(Random rnd, int maxValue, string key)
        {
            return $"{rnd.Next(0, maxValue):000000000000}{key}";
        }
    }

    public class BenchmarkConfiguration
    {
        public IPEndPoint Endpoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 6379);
        public string Password { get; set; } = null;
        public int ClientCount { get; set; } = 50;
        public int RequestCount { get; set; } = 100000;
        public ByteSize ValueSize { get; set; } = ByteSize.FromBytes(2);
        public int DatabaseNumber { get; set; } = 0;
        public KeepAliveOptions KeepAlive { get; set; } = KeepAliveOptions.Reconnect;
        public int KeyspaceLength { get; set; } = 1;
        public int PipelineRequestCount { get; set; } = 1;
        public bool QuietOutput { get; set; } = false;
        public bool UseCsvOutput { get; set; } = false;
        public bool LoopIndefinitely { get; set; } = false;
        public bool IdleConnections { get; set; } = false;
    }

    public enum KeepAliveOptions
    {
        Reconnect = 0,
        KeepAlive = 1,
    }
}
