﻿using System.Collections.Concurrent;
using System.Diagnostics;
using Prometheus;
using PrometheusNet.MongoDb.Events;
#pragma warning disable SA1118
#pragma warning disable SA1118

// ReSharper disable ComplexConditionExpression
namespace PrometheusNet.MongoDb.Handlers;

/// <summary>
/// Provides functionality to track metrics related to MongoDB cursors.
/// Implements the <see cref="IMetricProvider"/> interface.
/// </summary>
internal class CursorMetricsProvider : IMetricProvider, IDisposable
{
    private const int CursorTimeoutMilliseconds = 1000 * 60 * 10; // 10 min

    private readonly ConcurrentDictionary<long, (int DocumentCount, DateTime LastUpdated, string Collection, string Database)> _cursorDocumentCount = new();

    /// <summary>
    /// A thread-safe dictionary to store Stopwatch instances for each cursor.
    /// </summary>
    private readonly ConcurrentDictionary<long, Stopwatch> _cursorDurationTimers = new();

    /// <summary>
    /// Histogram metric for tracking the duration a MongoDB cursor is open.
    /// </summary>
    /// <remarks>This is done in seconds</remarks>
    public readonly Histogram OpenCursorDuration = Metrics.CreateHistogram(
        "mongodb_client_open_cursors_duration",
        "Duration a MongoDB cursor is open (seconds)",
        new HistogramConfiguration
        {
            LabelNames = new[] { "target_collection", "target_db" },
        });

    /// <summary>
    /// A Gauge metric to monitor the number of open MongoDB cursors.
    /// </summary>
    public readonly Gauge OpenCursors = Metrics.CreateGauge(
        "mongodb_client_open_cursors_count",
        "Number of open cursors",
        new GaugeConfiguration
        {
            LabelNames = new[] { "target_collection", "target_db" },
        });

    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CursorMetricsProvider"/> class.
    /// </summary>
    public CursorMetricsProvider()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };

        // Start a task to clean up old entries every minute
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                CleanUpOldEntries();
                await Task.Delay(TimeSpan.FromMinutes(1), _cts.Token);
            }
        });
    }

    private void CleanUpOldEntries()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _cursorDocumentCount)
        {
            _cts.Token.ThrowIfCancellationRequested();
            if (now - entry.Value.LastUpdated > TimeSpan.FromMilliseconds(CursorTimeoutMilliseconds))
            {
                if (_cursorDocumentCount.TryRemove(entry.Key, out var dcd))
                {
                    OpenCursors
                        .WithLabels(dcd.Collection, dcd.Database)
                        .Dec();
                }

                _cursorDurationTimers.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>
    /// Handles the successful completion event of a MongoDB command.
    /// </summary>
    /// <param name="e">Event data.</param>
    public void Handle(MongoCommandEventSuccess e)
    {
        if (e.OperationType is MongoOperationType.Find or MongoOperationType.GetMore or MongoOperationType.Aggregate)
        {
            if (e.OperationType is MongoOperationType.Find or MongoOperationType.Aggregate)
            {
                OpenCursors
                    .WithLabels(e.TargetCollection, e.TargetDatabase)
                    .Inc();

                _cursorDurationTimers.TryAdd(e.OperationId, Stopwatch.StartNew());
            }

            if (TryGetDocumentCountFromReply(e.Reply, out var documentCount))
            {
                IncrementDocumentCount(e, e.OperationId, documentCount);
            }

            // final batch done -> cursor will close
            if (IsFinalBatch(e.Reply))
            {
                DecrementOpenCursors(e);

                if (_cursorDurationTimers.TryRemove(e.OperationId, out var timer))
                {
                    timer.Stop();
                    OpenCursorDuration
                        .WithLabels(e.TargetCollection, e.TargetDatabase)
                        .Observe(timer.Elapsed.TotalSeconds);
                }
            }
        }
    }

    /// <summary>
    /// Handles the failure event of a MongoDB command.
    /// </summary>
    /// <param name="e">Event data.</param>
    public void Handle(MongoCommandEventFailure e)
    {
        // failure means cursor won't be open anymore
        if (e.OperationType is MongoOperationType.Find or MongoOperationType.GetMore)
        {
            OpenCursors
                .WithLabels(e.TargetCollection, e.TargetDatabase)
                .Dec();

            if (_cursorDurationTimers.TryRemove(e.OperationId, out var timer))
            {
                timer.Stop();
                OpenCursorDuration
                    .WithLabels(e.TargetCollection, e.TargetDatabase)
                    .Observe(timer.Elapsed.TotalSeconds);
            }

        }
    }

    private void DecrementOpenCursors(MongoCommandEventSuccess e) =>
        OpenCursors
            .WithLabels(e.TargetCollection, e.TargetDatabase)
            .Dec();

    private void IncrementDocumentCount(MongoCommandEventSuccess e, long cursorId, int documentCount)
    {
        if (IsFirstBatch(e.Reply))
        {
            _cursorDocumentCount.TryAdd(cursorId,
                (documentCount, DateTime.UtcNow, e.TargetCollection, e.TargetDatabase));
        }
        else
        {
            _cursorDocumentCount.TryUpdate(
                cursorId,
                newValue: (
                    _cursorDocumentCount.TryGetValue(cursorId, out var fetched) ?
                        fetched.DocumentCount + documentCount : documentCount,
                    DateTime.UtcNow,
                    e.TargetCollection,
                    e.TargetDatabase),
                comparisonValue: _cursorDocumentCount.TryGetValue(cursorId, out var comparison) ? 
                    comparison : default);
        }
    }

    private static bool IsFinalBatch(Dictionary<string, object> commandReply)
    {
        if (commandReply.TryGetValue("cursor", out var cursorAsObject) &&
            cursorAsObject is Dictionary<string, object> cursor)
        {
            if (cursor.TryGetValue("id", out var cursorIdAsObject) &&
                cursorIdAsObject is long cursorId)
            {
                return cursorId == 0;
            }
        }

        return false;
    }

    private static bool IsFirstBatch(Dictionary<string, object> commandReply)
    {
        if (commandReply.TryGetValue("cursor", out var cursorAsObject) &&
            cursorAsObject is Dictionary<string, object> cursor)
        {
            return cursor.ContainsKey("firstBatch");
        }

        return false;
    }

    private static bool TryGetDocumentCountFromReply(Dictionary<string, object> commandReply, out int documentCount)
    {
        documentCount = 0;

        if (commandReply.TryGetValue("cursor", out var cursorAsObject) &&
            cursorAsObject is Dictionary<string, object> cursor)
        {
            if (cursor.TryGetValue("firstBatch", out var batchDocumentAsObject) &&
                batchDocumentAsObject is object[] firstBatchDocuments)
            {
                documentCount = firstBatchDocuments.Length;
                return true;
            }

            if (cursor.TryGetValue("nextBatch", out var nextBatchDocumentAsObject) &&
                nextBatchDocumentAsObject is object[] nextBatchDocuments)
            {
                documentCount = nextBatchDocuments.Length;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Disposes of the resources used by the CursorMetricsProvider.
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
