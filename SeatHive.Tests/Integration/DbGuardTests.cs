using System.Data.Common;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Moq;
using SeatHive.Api.Models;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Tests.Integration
{
    [Collection(TestCollections.HoldService)]
    [Trait(TestCategories.Trait, TestCategories.Integration)]
    public class DbGuardTests
    {
        private readonly ContainersFixture _fixture;

        public DbGuardTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task SecondWrite_ShouldBeRejected_EvenWhenLockIsDisabled()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var bus = new Mock<IPublishEndpoint>();

            // The lock always lets everyone in, so only the database can stop a double booking.
            var noLock = new Mock<IRedisLockService>();
            noLock.Setup(x => x.AcquireLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                  .ReturnsAsync(Mock.Of<IAsyncDisposable>());

            // Both requests see no active booking before either of them writes.
            var bothHaveRead = new WaitUntilAllHaveReadSeat(participants: 2);
            var users = await _fixture.CreateUsersAsync(2);

            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

            var attempts = users.Select(userId => Task.Run(async () =>
            {
                await using var db = _fixture.CreateContext(bothHaveRead);
                var service = _fixture.CreateBookingService(db, clock, noLock.Object, bus.Object);
                return await service.HoldSeatAsync(seatId, userId);
            }));

            var results = await Task.WhenAll(attempts);

            // The second insert hits the unique index on active bookings.
            Assert.Equal(1, results.Count(r => r.IsSuccess));
            Assert.Equal(1, results.Count(r => r.Error == BookingError.SeatHeld));
            // The loser's insert was rolled back, so only the winner announced a held seat.
            Assert.Single(ContainersFixture.PublishedTo<SeatHeld>(bus));

            await using var verifyDb = _fixture.CreateContext();
            Assert.Equal(1, await verifyDb.Bookings.CountAsync(b => b.SeatId == seatId));
        }

        private sealed class WaitUntilAllHaveReadSeat : DbCommandInterceptor
        {
            private readonly TaskCompletionSource _allRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _remaining;

            public WaitUntilAllHaveReadSeat(int participants)
            {
                _remaining = participants;
            }

            public override async ValueTask<DbDataReader> ReaderExecutedAsync(
                DbCommand command,
                CommandExecutedEventData eventData,
                DbDataReader result,
                CancellationToken cancellationToken = default)
            {
                // The "is there an active booking?" check, which runs inside the hold transaction.
                if (command.Transaction != null && command.CommandText.Contains("FROM \"Bookings\""))
                {
                    if (Interlocked.Decrement(ref _remaining) == 0) _allRead.TrySetResult();
                    await _allRead.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }

                return result;
            }
        }
    }
}
