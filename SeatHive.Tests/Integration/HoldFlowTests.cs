using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Moq;
using SeatHive.Api.Models;
using SeatHive.Api.Services;

namespace SeatHive.Tests.Integration
{
    // Hold timing, expiry and limits. Time only moves when a test advances the clock; nothing here waits.
    [Collection(ContainersCollection.Name)]
    public class HoldFlowTests
    {
        private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(5);

        private readonly ContainersFixture _fixture;
        private readonly FakeTimeProvider _clock = new(Start);

        public HoldFlowTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        // Each call uses its own context, like a separate HTTP request.
        private async Task<BookingResult> HoldAsync(int seatId, int userId, TimeProvider? clock = null, HoldOptions? options = null)
        {
            await using var db = _fixture.CreateContext();
            return await _fixture.CreateBookingService(db, clock ?? _clock, options: options).HoldSeatAsync(seatId, userId);
        }

        private async Task<BookingResult> ConfirmAsync(int bookingId, int userId, TimeProvider? clock = null, IPublishEndpoint? bus = null)
        {
            await using var db = _fixture.CreateContext();
            return await _fixture.CreateBookingService(db, clock ?? _clock, bus: bus).ConfirmAsync(bookingId, userId);
        }

        private async Task<BookingResult> ReleaseAsync(int bookingId, int userId)
        {
            await using var db = _fixture.CreateContext();
            return await _fixture.CreateBookingService(db, _clock).ReleaseAsync(bookingId, userId);
        }

        private async Task<int> SweepAsync(TimeProvider? clock = null)
        {
            await using var db = _fixture.CreateContext();
            return await _fixture.CreateBookingService(db, clock ?? _clock).ExpireDueHoldsAsync();
        }

        private async Task<Booking> ReadBookingAsync(int bookingId)
        {
            await using var db = _fixture.CreateContext();
            return await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        }

        [Fact]
        public async Task Hold_ShouldUseTheConfiguredDuration()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();

            var result = await HoldAsync(seatId, userId, options: new HoldOptions { DurationSeconds = 60 });

            Assert.Equal(Start.UtcDateTime.AddSeconds(60), result.Booking!.ExpiresAt);
            Assert.Equal(Start.UtcDateTime.AddSeconds(60), (await ReadBookingAsync(result.Booking.Id)).ExpiresAt);
        }

        [Fact]
        public async Task Hold_ShouldReturnTheExistingHold_WhenTheSameUserHoldsTheSeatAgain()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();
            var first = await HoldAsync(seatId, userId);
            _clock.Advance(TimeSpan.FromMinutes(1));

            var second = await HoldAsync(seatId, userId);

            Assert.True(first.Changed);
            Assert.True(second.IsSuccess);
            Assert.False(second.Changed);
            Assert.Equal(first.Booking!.Id, second.Booking!.Id);
            // Holding again does not extend the hold.
            Assert.Equal(first.Booking.ExpiresAt, second.Booking.ExpiresAt);

            await using var db = _fixture.CreateContext();
            Assert.Equal(1, await db.Bookings.CountAsync(b => b.SeatId == seatId));
        }

        [Fact]
        public async Task ExpiredHold_ShouldLetAnotherUserHoldTheSeat_WithoutTheSweeper()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var users = await _fixture.CreateUsersAsync(2);
            var first = await HoldAsync(seatId, users[0]);

            // Still held one second before it runs out.
            _clock.Advance(HoldDuration - TimeSpan.FromSeconds(1));
            Assert.Equal(BookingError.SeatHeld, (await HoldAsync(seatId, users[1])).Error);

            _clock.Advance(TimeSpan.FromSeconds(1));
            var second = await HoldAsync(seatId, users[1]);

            Assert.True(second.IsSuccess);
            Assert.Equal(users[1], second.Booking!.UserId);
            Assert.Equal(BookingStatus.Expired, (await ReadBookingAsync(first.Booking!.Id)).Status);
            Assert.Equal(BookingStatus.Held, (await ReadBookingAsync(second.Booking.Id)).Status);
        }

        [Fact]
        public async Task Confirm_ShouldSucceed_JustBeforeTheHoldExpires()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();
            var hold = await HoldAsync(seatId, userId);
            _clock.Advance(HoldDuration - TimeSpan.FromSeconds(1));

            var result = await ConfirmAsync(hold.Booking!.Id, userId);

            Assert.True(result.IsSuccess);
            Assert.Equal(BookingStatus.Confirmed, (await ReadBookingAsync(hold.Booking.Id)).Status);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(60)]
        public async Task Confirm_ShouldBeRejected_OnceTheHoldHasExpired(int secondsAfterExpiry)
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();
            var bus = new Mock<IPublishEndpoint>();
            var hold = await HoldAsync(seatId, userId);
            _clock.Advance(HoldDuration + TimeSpan.FromSeconds(secondsAfterExpiry));

            var result = await ConfirmAsync(hold.Booking!.Id, userId, bus: bus.Object);

            Assert.Equal(BookingError.HoldExpired, result.Error);
            Assert.NotEqual(BookingStatus.Confirmed, (await ReadBookingAsync(hold.Booking.Id)).Status);
            Assert.Empty(bus.Invocations);
        }

        [Fact]
        public async Task Confirm_ShouldBeRejected_AfterTheSweeperExpiredTheHold()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();
            var hold = await HoldAsync(seatId, userId);
            _clock.Advance(HoldDuration);
            await SweepAsync();

            var result = await ConfirmAsync(hold.Booking!.Id, userId);

            Assert.Equal(BookingError.HoldExpired, result.Error);
            Assert.Equal(BookingStatus.Expired, (await ReadBookingAsync(hold.Booking.Id)).Status);
        }

        [Fact]
        public async Task ConfirmAndRelease_ShouldBeRejected_ForAnotherUsersBooking()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var users = await _fixture.CreateUsersAsync(2);
            var hold = await HoldAsync(seatId, users[0]);

            Assert.Equal(BookingError.NotHoldOwner, (await ConfirmAsync(hold.Booking!.Id, users[1])).Error);
            Assert.Equal(BookingError.NotHoldOwner, (await ReleaseAsync(hold.Booking.Id, users[1])).Error);
            Assert.Equal(BookingStatus.Held, (await ReadBookingAsync(hold.Booking.Id)).Status);

            // Confirming again is idempotent for the owner only.
            Assert.True((await ConfirmAsync(hold.Booking.Id, users[0])).IsSuccess);
            Assert.Equal(BookingError.NotHoldOwner, (await ConfirmAsync(hold.Booking.Id, users[1])).Error);
        }

        [Fact]
        public async Task ConfirmAndRelease_ShouldReturnNotFound_ForUnknownBooking()
        {
            var userId = await _fixture.CreateUserAsync();

            Assert.Equal(BookingError.BookingNotFound, (await ConfirmAsync(int.MaxValue, userId)).Error);
            Assert.Equal(BookingError.BookingNotFound, (await ReleaseAsync(int.MaxValue, userId)).Error);
        }

        [Fact]
        public async Task Release_ShouldFreeTheSeatImmediately()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var users = await _fixture.CreateUsersAsync(2);
            var hold = await HoldAsync(seatId, users[0]);

            var released = await ReleaseAsync(hold.Booking!.Id, users[0]);
            var next = await HoldAsync(seatId, users[1]);

            Assert.True(released.IsSuccess);
            Assert.Equal(BookingStatus.Released, (await ReadBookingAsync(hold.Booking.Id)).Status);
            Assert.True(next.IsSuccess);

            // A released hold can be neither confirmed nor released again.
            Assert.Equal(BookingError.HoldNotActive, (await ConfirmAsync(hold.Booking.Id, users[0])).Error);
            Assert.Equal(BookingError.HoldNotActive, (await ReleaseAsync(hold.Booking.Id, users[0])).Error);
        }

        [Fact]
        public async Task Release_ShouldBeRejected_ForAConfirmedBooking()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();
            var hold = await HoldAsync(seatId, userId);
            await ConfirmAsync(hold.Booking!.Id, userId);

            var result = await ReleaseAsync(hold.Booking.Id, userId);

            Assert.Equal(BookingError.HoldNotActive, result.Error);
            Assert.Equal(BookingStatus.Confirmed, (await ReadBookingAsync(hold.Booking.Id)).Status);
        }

        [Fact]
        public async Task Hold_ShouldBeRejected_WhenTheUserIsAtTheLimit_UntilAHoldIsGivenUp()
        {
            var userId = await _fixture.CreateUserAsync();
            var holds = new List<BookingResult>();
            for (var i = 0; i < 4; i++)
            {
                holds.Add(await HoldAsync(await _fixture.CreateFreeSeatAsync(), userId));
                // Spread the expiry times so the holds run out one by one.
                _clock.Advance(TimeSpan.FromSeconds(10));
            }
            Assert.All(holds, hold => Assert.True(hold.IsSuccess));

            // Default limit is 4.
            var fifthSeat = await _fixture.CreateFreeSeatAsync();
            Assert.Equal(BookingError.HoldLimitReached, (await HoldAsync(fifthSeat, userId)).Error);

            // Releasing one makes room.
            await ReleaseAsync(holds[3].Booking!.Id, userId);
            Assert.True((await HoldAsync(fifthSeat, userId)).IsSuccess);
            Assert.Equal(BookingError.HoldLimitReached, (await HoldAsync(await _fixture.CreateFreeSeatAsync(), userId)).Error);

            // So does a hold that ran out, even before the sweeper has marked it.
            _clock.SetUtcNow(Start + HoldDuration);
            Assert.True((await HoldAsync(await _fixture.CreateFreeSeatAsync(), userId)).IsSuccess);

            // A confirmed booking is not a hold any more.
            Assert.Equal(BookingError.HoldLimitReached, (await HoldAsync(await _fixture.CreateFreeSeatAsync(), userId)).Error);
            Assert.True((await ConfirmAsync(holds[1].Booking!.Id, userId)).IsSuccess);
            Assert.True((await HoldAsync(await _fixture.CreateFreeSeatAsync(), userId)).IsSuccess);
        }

        [Fact]
        public async Task Hold_ShouldUseTheConfiguredLimit()
        {
            var userId = await _fixture.CreateUserAsync();
            var options = new HoldOptions { MaxActivePerUser = 1 };

            var first = await HoldAsync(await _fixture.CreateFreeSeatAsync(), userId, options: options);
            var second = await HoldAsync(await _fixture.CreateFreeSeatAsync(), userId, options: options);

            Assert.True(first.IsSuccess);
            Assert.Equal(BookingError.HoldLimitReached, second.Error);
        }

        [Fact]
        public async Task ConcurrentHoldsByOneUser_ShouldNotExceedTheLimit()
        {
            const int rounds = 10;
            const int seatsPerRound = 10;
            var failedRounds = new List<string>();

            for (var round = 1; round <= rounds; round++)
            {
                var userId = await _fixture.CreateUserAsync();
                var seatIds = new List<int>();
                for (var i = 0; i < seatsPerRound; i++) seatIds.Add(await _fixture.CreateFreeSeatAsync());

                var results = await Task.WhenAll(seatIds.Select(seatId => Task.Run(() => HoldAsync(seatId, userId))));

                await using var db = _fixture.CreateContext();
                var held = await db.Bookings.CountAsync(b => b.UserId == userId && b.Status == BookingStatus.Held);
                var successes = results.Count(r => r.IsSuccess);
                var rejected = results.Count(r => r.Error == BookingError.HoldLimitReached);

                if (held != 4 || successes != 4 || rejected != seatsPerRound - 4)
                {
                    failedRounds.Add($"round {round}: held={held}, successes={successes}, limit rejections={rejected}");
                }
            }

            Assert.True(failedRounds.Count == 0,
                $"{failedRounds.Count}/{rounds} rounds broke the per-user limit:\n" + string.Join("\n", failedRounds));
        }

        [Fact]
        public async Task Sweeper_ShouldExpireOnlyHoldsThatRanOut()
        {
            var users = await _fixture.CreateUsersAsync(3);
            var expiring = await HoldAsync(await _fixture.CreateFreeSeatAsync(), users[0]);
            var confirmed = await HoldAsync(await _fixture.CreateFreeSeatAsync(), users[1]);
            await ConfirmAsync(confirmed.Booking!.Id, users[1]);
            _clock.Advance(TimeSpan.FromMinutes(1));
            var stillValid = await HoldAsync(await _fixture.CreateFreeSeatAsync(), users[2]);

            // The first hold has run out, the last one has a minute left.
            _clock.SetUtcNow(Start + HoldDuration);
            var expiredCount = await SweepAsync();

            Assert.True(expiredCount >= 1);
            Assert.Equal(BookingStatus.Expired, (await ReadBookingAsync(expiring.Booking!.Id)).Status);
            Assert.Equal(BookingStatus.Confirmed, (await ReadBookingAsync(confirmed.Booking.Id)).Status);
            Assert.Equal(BookingStatus.Held, (await ReadBookingAsync(stillValid.Booking!.Id)).Status);
        }

        [Fact]
        public async Task ConfirmRacingTheSweeper_ShouldEndInExactlyOneOutcome()
        {
            const int rounds = 25;
            var failedRounds = new List<string>();

            // The two sides read the clock at slightly different moments around the expiry:
            // confirm just before it, the sweeper just after. Only one of the two updates may win.
            var confirmClock = new FakeTimeProvider(Start + HoldDuration - TimeSpan.FromSeconds(1));
            var sweeperClock = new FakeTimeProvider(Start + HoldDuration + TimeSpan.FromSeconds(1));

            for (var round = 1; round <= rounds; round++)
            {
                var seatId = await _fixture.CreateFreeSeatAsync();
                var userId = await _fixture.CreateUserAsync();
                var bus = new Mock<IPublishEndpoint>();
                var hold = await HoldAsync(seatId, userId);

                var confirmTask = Task.Run(() => ConfirmAsync(hold.Booking!.Id, userId, confirmClock, bus.Object));
                var sweepTask = Task.Run(() => SweepAsync(sweeperClock));
                await sweepTask;
                var confirm = await confirmTask;

                var status = (await ReadBookingAsync(hold.Booking!.Id)).Status;
                var published = bus.Invocations.Count(i => i.Method.Name == nameof(IPublishEndpoint.Publish));

                var confirmWon = confirm.IsSuccess && status == BookingStatus.Confirmed && published == 1;
                var sweeperWon = confirm.Error == BookingError.HoldExpired && status == BookingStatus.Expired && published == 0;

                if (!confirmWon && !sweeperWon)
                {
                    failedRounds.Add(
                        $"round {round}: confirm success={confirm.IsSuccess} error={confirm.Error}, " +
                        $"status={status}, events={published}");
                }
            }

            Assert.True(failedRounds.Count == 0,
                $"{failedRounds.Count}/{rounds} rounds ended in a mixed outcome:\n" + string.Join("\n", failedRounds));
        }
    }
}
