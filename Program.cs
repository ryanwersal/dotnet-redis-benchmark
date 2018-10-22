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
        const string LowestTestedVersion = "1.2.6";
        const string HighestTestedVersion = "2.0.513";

        static void Main(string[] args)
        {
            Console.CancelKeyPress += (sender, cancelArgs) =>
            {
                Log.Verbose("Cancelling execution...");
                cts.Cancel();
            };

            var app = new CommandLineApplication
            {
                Name = "redis-benchmark",
                FullName = "Minimal redis-benchmark clone using StackExchange.Redis",
                ExtendedHelpText = $@"
The version of StackExchange.Redis can be changed by specifying
the environment variable StackExchangeRedisVersion and providing a
value that is a valid version of the NuGet package.

If no value is specified it will default to {HighestTestedVersion}.

Note that the version must be compatible with the APIs used in the 
tool. The lowest version tested was {LowestTestedVersion} and the highest was {HighestTestedVersion}."
            };
            app.HelpOption("-?|--help");

            var hostOption = app.Option("-h|--host", "Server hostname (default 127.0.0.1)", CommandOptionType.SingleValue, false);
            var portOption = app.Option("-p|--port", "Server port (default 6379)", CommandOptionType.SingleValue, false);
            var socketOption = app.Option("-s|--socket", "Server socket (overrides host and port)", CommandOptionType.SingleValue, false);
            var passwordOption = app.Option("-a|--auth-password", "Password for Redis Auth", CommandOptionType.SingleValue, false);
            var clientsOption = app.Option("-c|--clients", "Number of parallel connections (default 50)", CommandOptionType.SingleValue, false);
            var requestsOption = app.Option("-n|--num-requests", "Total number of requests (default 100000)", CommandOptionType.SingleValue, false);
            var sizeOption = app.Option("-d|--data-size", "Data size of SET/GET value, specified  (default 2B)", CommandOptionType.SingleValue, false);
            var dbNumOption = app.Option("--dbnum", "SELECT the specified db number (default 0)", CommandOptionType.SingleValue, false);
            var keyspaceLenOption = app.Option("-r|--keyspace-length", @"Use random keys for SET/GET/INCR, random values for SADD
  Using this option the benchmark will expand the string __rand_int__
  inside an argument with a 12 digits number in the specified range
  from 0 to keyspacelen - 1.The substitution changes every time a command
  is executed.Default tests use this to hit random keys in the
  specified range.", CommandOptionType.SingleValue, false);
            var quietOption = app.Option("-q|--quiet", "Quiet. Just show query/sec values", CommandOptionType.NoValue, false);
            var loopOption = app.Option("-l|--loop", "Loop. Run the tests forever", CommandOptionType.NoValue, false);
            var testsOption = app.Option("-t|--tests", "Only run the comma separated list of tests. The test names are the same as the ones produced as output", CommandOptionType.SingleValue, false);
            var longLivedConnectionsOption = app.Option("-L|--long-lived-connections", "Use long lived connections for all test runs", CommandOptionType.NoValue, false);

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

                if (testsOption.HasValue())
                {
                    var tests = testsOption.Value().ToLower().Split(",");
                    config.BenchmarkTests = new HashSet<string>(tests);
                }

                if (passwordOption.HasValue()) config.Password = passwordOption.Value();
                if (clientsOption.HasValue()) config.ClientCount = int.Parse(clientsOption.Value());
                if (requestsOption.HasValue()) config.RequestCount = int.Parse(requestsOption.Value());
                if (sizeOption.HasValue()) config.ValueSize = ByteSize.Parse(sizeOption.Value());
                if (dbNumOption.HasValue()) config.DatabaseNumber = int.Parse(dbNumOption.Value());
                if (keyspaceLenOption.HasValue()) config.KeyspaceLength = int.Parse(keyspaceLenOption.Value());
                if (quietOption.HasValue()) config.QuietOutput = true;
                if (loopOption.HasValue()) config.LoopIndefinitely = true;
                if (longLivedConnectionsOption.HasValue()) config.LongLivedConnections = true;

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Is(config.QuietOutput ? LogEventLevel.Information : LogEventLevel.Verbose)
                    .WriteTo.Console()
                    .CreateLogger();

                if (!config.LoopIndefinitely)
                {
                    await ExecuteBenchmarks(config);
                }
                else
                {
                    while (true) await ExecuteBenchmarks(config);
                }

                // Clean up all connections if long lived connections are configured
                if (config.LongLivedConnections && StaticClients != null)
                {
                    foreach (var client in StaticClients)
                    {
                        client.Dispose();
                    }
                    StaticClients = null;
                }

                return 0;
            });

            app.Execute(args);
        }

        private readonly static CancellationTokenSource cts = new CancellationTokenSource();
        private static async Task ExecuteBenchmarks(BenchmarkConfiguration config)
        {
            if (config.IsBenchmarkEnabled("ping"))
            {
                await ExecuteTest("PING", async client =>
                {
                    await client.Db.PingAsync();
                }, config, cts.Token);
            }

            if (config.IsBenchmarkEnabled("set"))
            {
                await ExecuteTest("SET", async client =>
                {
                    client.Random.NextBytes(client.Bytes);
                    await client.Db.StringSetAsync(client.GetKey("foo"), client.Bytes);
                }, config, cts.Token);
            }

            if (config.IsBenchmarkEnabled("get"))
            {
                await ExecuteTest("GET", async client =>
                {
                    await client.Db.StringGetAsync(client.GetKey("foo"));
                }, config, cts.Token);
            }

            if (config.IsBenchmarkEnabled("del"))
            {
                await ExecuteTest("DEL", async client =>
                {
                    await client.Db.KeyDeleteAsync(client.GetKey("foo"));
                }, config, cts.Token);
            }
        }

        private static async Task ExecuteTest(
            string operation, Func<RedisClient, Task> testFunc, BenchmarkConfiguration config, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;

            var clients = await CreateClients(config, ct);

            var testTasks = clients.Select(client =>
            {
                return Task.Factory.StartNew(
                    async () =>
                    {
                        for (var i = 0; i < config.RequestCount; ++i)
                        {
                            if (i % 100 == 0)
                            {
                                if (Log.IsEnabled(LogEventLevel.Debug))
                                {
                                    Log.Verbose("Client #{ClientNumber} has sent {Number} of {Total} {Operation}.",
                                                client.ClientNumber, i, config.RequestCount, operation);
                                }

                                if (ct.IsCancellationRequested) break;
                            }

                            try
                            {
                                await testFunc(client);
                            }
                            catch (Exception e)
                            {
                                Log.Error(e, "Error occurred when running test for Client #{ClientNumber} for {Operation}.",
                                    client.ClientNumber, operation);
                            }
                        }
                    },
                    ct,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                ).Unwrap();
            });

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            await Task.WhenAll(testTasks);

            stopwatch.Stop();

            var totalRequests = config.ClientCount * config.RequestCount;
            var requestsPerSecond = totalRequests / stopwatch.Elapsed.TotalSeconds;

            Log.Information("{Operation}: {RequestPerSecond:F3} requests per second ({TotalRequests} requests over {TotalRuntime:F3} seconds)", 
                operation, requestsPerSecond, totalRequests, stopwatch.Elapsed.TotalSeconds);

            // Dispose all connections if it is desired to recreate them per test, otherwise keep them live
            if (!config.LongLivedConnections)
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }
                clients = null;
            }
        }

        private static IEnumerable<RedisClient> StaticClients = null;
        private static async Task<IEnumerable<RedisClient>> CreateClients(BenchmarkConfiguration config, CancellationToken ct)
        {
            if (config.LongLivedConnections && StaticClients != null)
            {
                return StaticClients;
            }

            var clientTasks = Enumerable.Range(0, config.ClientCount).Select(num => CreateClient(num, config, ct));
            var clients = await Task.WhenAll(clientTasks);

            if (config.LongLivedConnections)
            {
                StaticClients = clients;
            }

            return clients;
        }

        private static async Task<RedisClient> CreateClient(int clientNumber, BenchmarkConfiguration config, CancellationToken ct)
        {
            var options = new ConfigurationOptions
            {
                Password = config.Password,
                ClientName = "redis-benchmark-client",
                DefaultDatabase = config.DatabaseNumber,
            };
            options.EndPoints.Add(config.Endpoint);

            var connection = await ConnectionMultiplexer.ConnectAsync(options);

            var faker = new Faker();
            var seedDateTimeOffset = faker.Date.BetweenOffset(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
            var rnd = new Random((int)seedDateTimeOffset.ToUnixTimeSeconds());
            var bytes = new byte[(int)config.ValueSize.Bytes];

            Log.Verbose("Client #{ClientNumber} created.", clientNumber);

            return new RedisClient
            {
                ClientNumber = clientNumber,
                Config = config,
                Connection = connection,
                Db = connection.GetDatabase(config.DatabaseNumber),
                Faker = faker,
                Random = rnd,
                Bytes = bytes,
            };
        }
    }

    public class RedisClient : IDisposable
    {
        public int ClientNumber { get; set; }
        public BenchmarkConfiguration Config { get; set; }

        public ConnectionMultiplexer Connection { get; set; }
        public IDatabase Db { get; set; }
        public Faker Faker { get; set; }
        public Random Random { get; set; }
        public byte[] Bytes { get; set; }

        public string GetKey(string key)
        {
            return $"{Random.Next(0, Config.KeyspaceLength):000000000000}{key}";
        }

        public void Dispose()
        {
            Log.Verbose("Disposing Client #{ClientNumber}...", ClientNumber);

            Connection.Dispose();
            Connection = null;

            Config = null;
            Db = null;
            Faker = null;
            Random = null;
            Bytes = null;
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
        public int KeyspaceLength { get; set; } = 1;
        public bool QuietOutput { get; set; } = false;
        public bool LoopIndefinitely { get; set; } = false;
        public ISet<string> BenchmarkTests { get; set; }
        public bool LongLivedConnections { get; set; } = false;

        public bool IsBenchmarkEnabled(string test)
        {
            if (BenchmarkTests == null) return true;

            return BenchmarkTests.Contains(test.ToLower());
        }
    }
}
