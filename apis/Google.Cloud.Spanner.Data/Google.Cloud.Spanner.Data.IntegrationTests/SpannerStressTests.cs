﻿// Copyright 2017 Google Inc. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Cloud.ClientTesting;
using Google.Cloud.Spanner.Data.CommonTesting;
using Google.Cloud.Spanner.V1;
using Google.Cloud.Spanner.V1.Internal.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Google.Cloud.Spanner.Data.IntegrationTests
{
    [Collection(nameof(SpannerStressTestTableFixture))]
    [PerformanceLog]
    [CommonTestDiagnostics]
    public class SpannerStressTests : StressTestBase
    {
        private static int s_rowCounter = 1;
        private static readonly string s_guid = IdGenerator.FromGuid();
        private static ThreadLocal<Random> s_rnd = new ThreadLocal<Random>(() => new Random(Environment.TickCount));
        private SpannerStressTestTableFixture _fixture;

        public SpannerStressTests(SpannerStressTestTableFixture fixture) =>
            _fixture = fixture;

        private async Task<TimeSpan> TestWriteOneRow(Stopwatch sw)
        {
            using (var connection = _fixture.GetConnection())
            {
                var localCounter = Interlocked.Increment(ref s_rowCounter);
                var insertCommand = connection.CreateInsertCommand(_fixture.TableName);
                insertCommand.Parameters.Add("ID", SpannerDbType.String, $"{s_guid}{localCounter}");
                insertCommand.Parameters.Add("Title", SpannerDbType.String, "Title");

                // This uses an ephemeral transaction, so it's legal to retry it.
                await ExecuteWithRetry(insertCommand.ExecuteNonQueryAsync);
            }
            return sw.Elapsed;
        }

        private async Task<TimeSpan> TestWriteTx(Stopwatch sw)
        {
            await ExecuteWithRetry(async () =>
            {
                using (var connection = _fixture.GetConnection())
                {
                    await connection.OpenAsync();
                    using (var tx = await connection.BeginTransactionAsync())
                    {
                        var rowsToWrite = Enumerable.Range(0, s_rnd.Value.Next(5) + 1)
                            .Select(x => Interlocked.Increment(ref s_rowCounter)).ToList();

                        var insertCommand = connection.CreateInsertCommand(_fixture.TableName);
                        var idParameter = insertCommand.Parameters.Add("ID", SpannerDbType.String);
                        var titleParameter = insertCommand.Parameters.Add("Title", SpannerDbType.String);
                        titleParameter.Value = "Title";
                        insertCommand.Transaction = tx;

                        var tasks = rowsToWrite.Select(
                            x =>
                            {
                                // This will blow up with dupe primary keys if not threadsafe.
                                idParameter.Value = $"{s_guid}{x}";
                                return insertCommand.ExecuteNonQueryAsync(CancellationToken.None);
                            });

                        // This doesn't really *help* with perf, but is something
                        // somebody will try.
                        await Task.WhenAll(tasks);
                        await tx.CommitAsync();
                    }
                }
            });
            return sw.Elapsed;
        }

        private static async Task ExecuteWithRetry(Func<Task> actionToRetry)
        {
            var retry = true;
            long delay = 0;
            while (retry)
            {
                retry = false;
                try
                {
                    await actionToRetry();
                }
                catch (Exception e) when (e.IsTransientSpannerFault())
                {
                    //TODO(benwu): replace with topaz.
                    retry = true;
                    Console.WriteLine("retrying due to " + e.Message);
                    delay = delay * 2;
                    delay += s_rnd.Value.Next(100);
                    delay = Math.Min(delay, 5000);
                    await Task.Delay(TimeSpan.FromMilliseconds(delay));
                }
            }
        }

        [Fact]
        public Task RunWriteStress() => RunStress(TestWriteOneRow);

        [Fact]
        public Task RunParallelTransactionStress() => RunStress(TestWriteTx);

        private async Task RunStress(Func<Stopwatch, Task<TimeSpan>> writeFunc)
        {
            // Clear current session pool to eliminate the chance of a previous
            // test altering the pool state (which is validated at end)
            if (!Task.Run(SessionPool.Default.ReleaseAllAsync).Wait(SessionPool.Default.ShutDownTimeout))
            {
                throw new TimeoutException("Deadline exceeded while releasing all sessions");
            }

            //prewarm
            // The maximum roundtrip time for spanner (and mysql) is about 200ms per
            // write.  so if we initialize with the target sustained # sessions,
            // we shouldn't see any more sessions created.
            int countToPreWarm = Math.Min(TargetQps / 4, 800);
            SpannerOptions.Instance.MaximumActiveSessions = Math.Max(countToPreWarm + 50, 400);
            SpannerOptions.Instance.MaximumPooledSessions = Math.Max(countToPreWarm + 50, 400);
            SpannerOptions.Instance.MaximumGrpcChannels = Math.Max(4, 8 * TargetQps / 2000);

            Logger.DefaultLogger.Info(() =>
                $"SpannerOptions.Instance.MaximumActiveSessions:{SpannerOptions.Instance.MaximumActiveSessions}");
            Logger.DefaultLogger.Info(() =>
                $"SpannerOptions.Instance.MaximumGrpcChannels:{SpannerOptions.Instance.MaximumGrpcChannels}");

            //prewarm step.
            using (_fixture.GetConnection())
            {
                var all = new List<SpannerConnection>();
                const int increment = 25;
                while (countToPreWarm > 0)
                {
                    var prewarm = new List<SpannerConnection>();
                    var localCount = Math.Min(increment, countToPreWarm);
                    Logger.DefaultLogger.Info(() => $"prewarming {localCount} spanner sessions");
                    for (var i = 0; i < localCount; i++)
                    {
                        prewarm.Add(new SpannerConnection(_fixture.ConnectionString));
                    }
                    await Task.WhenAll(prewarm.Select(x => x.OpenAsync()));
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                    all.AddRange(prewarm);
                    countToPreWarm -= increment;
                }

                foreach (var preWarmCon in all)
                {
                    preWarmCon.Dispose();
                }
            }

            // Run the test with only info logging enabled.
            var previousLogLevel = Logger.DefaultLogger.LogLevel;
            Logger.DefaultLogger.LogLevel = V1.Internal.Logging.LogLevel.Info;
            double latencyMs;
            try
            {
                latencyMs = await TestWriteLatencyWithQps(TargetQps, TestDuration, writeFunc);
            }
            finally
            {
                Logger.DefaultLogger.LogLevel = previousLogLevel;
            }
            Logger.DefaultLogger.Info(() => $"Spanner latency = {latencyMs}ms");

            ValidatePoolInfo();

            // Spanner latency with 100 qps simulated is usually around 75ms.
            Assert.InRange(latencyMs, 0, 150);
        }
    }
}
