using System.Data.Common;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Tests.Integration
{
    [Collection(ContainersCollection.Name)]
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

            // Both requests read the seat as free before either of them writes.
            var bothHaveRead = new WaitUntilAllHaveReadSeat(participants: 2);

            var attempts = new[] { 1, 2 }.Select(userId => Task.Run(async () =>
            {
                await using var db = _fixture.CreateContext(bothHaveRead);
                var service = new BookingService(db, noLock.Object, bus.Object);
                return await service.BookSeatAsync(new BookingRequest { SeatId = seatId, UserId = userId });
            }));

            var results = await Task.WhenAll(attempts);

            Assert.Equal(1, results.Count(r => r == "Booking successful!"));
            Assert.Equal(1, results.Count(r => r == "Seat is already booked."));
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
                if (command.CommandText.Contains("FROM \"Seats\""))
                {
                    if (Interlocked.Decrement(ref _remaining) == 0) _allRead.TrySetResult();
                    await _allRead.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }

                return result;
            }
        }
    }
}
