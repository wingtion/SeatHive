using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SeatHive.Tests.Integration
{
    // Every database command that failed, with its error. EF Core logs each of them at error level,
    // so a command that fails as part of normal work shows up in the log as a failure.
    public sealed class FailedCommandCounter : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _failures = new();

        public IReadOnlyCollection<string> Failures => _failures;

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        {
            _failures.Enqueue(eventData.Exception.Message);
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            _failures.Enqueue(eventData.Exception.Message);
            return Task.CompletedTask;
        }
    }
}
