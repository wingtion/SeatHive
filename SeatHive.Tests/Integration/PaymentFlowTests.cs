using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Moq;
using SeatHive.Api.Models;
using SeatHive.Api.Services;
using SeatHive.Shared.Events;

namespace SeatHive.Tests.Integration
{
    // The booking state machine around the payment, driven directly through the service.
    // Events go to a mocked bus; the real outbox is covered by the API tests.
    [Collection(TestCollections.PaymentService)]
    [Trait(TestCategories.Trait, TestCategories.Integration)]
    public class PaymentFlowTests
    {
        private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(5);
        // Default: a booking waiting for its payment is kept 30 seconds past its hold.
        private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

        private readonly ContainersFixture _fixture;
        private readonly FakeTimeProvider _clock = new(Start);
        private readonly Mock<IPublishEndpoint> _bus = new();

        public PaymentFlowTests(ContainersFixture fixture)
        {
            _fixture = fixture;
        }

        // Each call uses its own context, like a separate request or consumed message.
        private async Task<T> WithServiceAsync<T>(Func<BookingService, Task<T>> action, TimeProvider? clock = null, IPublishEndpoint? bus = null)
        {
            await using var db = _fixture.CreateContext();
            return await action(_fixture.CreateBookingService(db, clock ?? _clock, bus: bus ?? _bus.Object));
        }

        private Task<BookingResult> HoldAsync(int seatId, int userId) => WithServiceAsync(s => s.HoldSeatAsync(seatId, userId));
        private Task<BookingResult> RequestAsync(int bookingId, int userId, bool forceFailure = false) =>
            WithServiceAsync(s => s.RequestPaymentAsync(bookingId, userId, forceFailure));
        // The payment result as the Worker would send it: seat and user are the ones the payment was requested for.
        private Task<BookingResult> CompleteAsync(Booking booking, Guid paymentId) =>
            CompleteAsync(booking.Id, paymentId, booking.SeatId, booking.UserId);
        private Task<BookingResult> CompleteAsync(int bookingId, Guid paymentId, int seatId, int userId) =>
            WithServiceAsync(s => s.CompletePaymentAsync(ContainersFixture.SuccessfulPayment(bookingId, paymentId, seatId, userId)));
        private Task<BookingResult> FailAsync(int bookingId, Guid paymentId) => WithServiceAsync(s => s.FailPaymentAsync(bookingId, paymentId));
        private Task<BookingResult> ReleaseAsync(int bookingId, int userId) => WithServiceAsync(s => s.ReleaseAsync(bookingId, userId));
        private Task<int> SweepAsync() => WithServiceAsync(s => s.ExpireDueHoldsAsync());

        private List<T> Published<T>() => ContainersFixture.PublishedTo<T>(_bus);

        private async Task<Booking> ReadBookingAsync(int bookingId)
        {
            await using var db = _fixture.CreateContext();
            return await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        }

        // A held seat whose owner has confirmed; the payment result is still to come.
        private async Task<(Booking Booking, int UserId, Guid PaymentId)> HoldWithPendingPaymentAsync()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();
            var hold = await HoldAsync(seatId, userId);
            var requested = await RequestAsync(hold.Booking!.Id, userId);

            return (requested.Booking!, userId, requested.Booking!.PaymentId!.Value);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RequestPayment_ShouldMoveTheHoldToPaymentPending_AndPublishPaymentRequested(bool forceFailure)
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var userId = await _fixture.CreateUserAsync();
            var hold = await HoldAsync(seatId, userId);

            var result = await RequestAsync(hold.Booking!.Id, userId, forceFailure);

            Assert.True(result.IsSuccess);
            var booking = await ReadBookingAsync(hold.Booking.Id);
            Assert.Equal(BookingStatus.PaymentPending, booking.Status);
            Assert.NotNull(booking.PaymentId);
            Assert.Null(booking.ConfirmedAt);

            var requested = Assert.Single(Published<PaymentRequested>());
            Assert.Equal(
                new PaymentRequested(booking.Id, seatId, userId, Start.UtcDateTime, booking.PaymentId.Value, forceFailure),
                requested);
        }

        [Fact]
        public async Task RequestPayment_ShouldNotStartASecondPayment_WhileOneIsPending()
        {
            var (booking, userId, paymentId) = await HoldWithPendingPaymentAsync();

            var again = await RequestAsync(booking.Id, userId);

            Assert.True(again.IsSuccess);
            Assert.False(again.Changed);
            Assert.Equal(BookingStatus.PaymentPending, again.Booking!.Status);
            Assert.Equal(paymentId, (await ReadBookingAsync(booking.Id)).PaymentId);
            Assert.Single(Published<PaymentRequested>());
        }

        [Fact]
        public async Task PaymentSucceeded_ShouldConfirmTheBooking_AndPublishBookingConfirmed()
        {
            var (booking, userId, paymentId) = await HoldWithPendingPaymentAsync();
            _clock.Advance(TimeSpan.FromSeconds(2));

            var result = await CompleteAsync(booking, paymentId);

            Assert.True(result.IsSuccess);
            var saved = await ReadBookingAsync(booking.Id);
            Assert.Equal(BookingStatus.Confirmed, saved.Status);
            Assert.Equal(_clock.GetUtcNow().UtcDateTime, saved.ConfirmedAt);

            var confirmed = Assert.Single(Published<BookingConfirmed>());
            Assert.Equal(new BookingConfirmed(booking.Id, booking.SeatId, userId, _clock.GetUtcNow().UtcDateTime, paymentId), confirmed);
            Assert.Empty(Published<RefundRequested>());

            // Confirming again is a no-op for the owner.
            var again = await RequestAsync(booking.Id, userId);
            Assert.True(again.IsSuccess);
            Assert.False(again.Changed);
            Assert.Equal(BookingStatus.Confirmed, again.Booking!.Status);
            Assert.Single(Published<PaymentRequested>());
        }

        [Fact]
        public async Task PaymentSucceeded_ShouldHaveOneEffect_WhenHandledTwice()
        {
            var (booking, _, paymentId) = await HoldWithPendingPaymentAsync();

            var first = await CompleteAsync(booking, paymentId);
            var second = await CompleteAsync(booking, paymentId);

            Assert.True(first.Changed);
            Assert.True(second.IsSuccess);
            Assert.False(second.Changed);
            Assert.Single(Published<BookingConfirmed>());
            // The booking is confirmed with this very payment, so there is nothing to refund.
            Assert.Empty(Published<RefundRequested>());
        }

        [Fact]
        public async Task PaymentFailed_ShouldReturnTheBookingToAHold_ThatCanBePaidAgain()
        {
            var (booking, userId, firstPaymentId) = await HoldWithPendingPaymentAsync();
            _clock.Advance(TimeSpan.FromSeconds(2));

            var failed = await FailAsync(booking.Id, firstPaymentId);

            Assert.True(failed.IsSuccess);
            var held = await ReadBookingAsync(booking.Id);
            Assert.Equal(BookingStatus.Held, held.Status);
            // The hold keeps its original deadline.
            Assert.Equal(Start.UtcDateTime + HoldDuration, held.ExpiresAt);
            Assert.Empty(Published<BookingConfirmed>());

            // The owner tries again: a new payment attempt with a new id.
            var retry = await RequestAsync(booking.Id, userId);
            Assert.True(retry.Changed);
            var secondPaymentId = retry.Booking!.PaymentId!.Value;
            Assert.NotEqual(firstPaymentId, secondPaymentId);
            Assert.Equal(2, Published<PaymentRequested>().Count);

            // A second "failed" for the first attempt must not touch the new one.
            await FailAsync(booking.Id, firstPaymentId);
            Assert.Equal(BookingStatus.PaymentPending, (await ReadBookingAsync(booking.Id)).Status);

            Assert.True((await CompleteAsync(booking, secondPaymentId)).IsSuccess);
            Assert.Equal(BookingStatus.Confirmed, (await ReadBookingAsync(booking.Id)).Status);
        }

        [Fact]
        public async Task PaymentSucceeded_ForAnEarlierAttempt_ShouldBeRefunded_AndLeaveTheCurrentAttemptAlone()
        {
            var (booking, userId, firstPaymentId) = await HoldWithPendingPaymentAsync();
            await FailAsync(booking.Id, firstPaymentId);
            var retry = await RequestAsync(booking.Id, userId);

            var stale = await CompleteAsync(booking, firstPaymentId);

            Assert.False(stale.IsSuccess);
            var saved = await ReadBookingAsync(booking.Id);
            Assert.Equal(BookingStatus.PaymentPending, saved.Status);
            Assert.Equal(retry.Booking!.PaymentId, saved.PaymentId);
            Assert.Equal(firstPaymentId, Assert.Single(Published<RefundRequested>()).PaymentId);
            Assert.Empty(Published<BookingConfirmed>());
        }

        [Fact]
        public async Task PaymentFailed_AfterTheHoldRanOut_ShouldLeaveAHoldThatTheSweeperExpires()
        {
            var (booking, _, paymentId) = await HoldWithPendingPaymentAsync();
            _clock.Advance(HoldDuration + TimeSpan.FromSeconds(1));

            await FailAsync(booking.Id, paymentId);
            await SweepAsync();

            Assert.Equal(BookingStatus.Expired, (await ReadBookingAsync(booking.Id)).Status);
            Assert.Contains(Published<HoldExpired>(), e => e.BookingId == booking.Id);
        }

        [Fact]
        public async Task PaymentSucceeded_WithinTheGracePeriod_ShouldStillConfirm()
        {
            var (booking, _, paymentId) = await HoldWithPendingPaymentAsync();

            // The hold itself has run out, but the booking is still waiting for its payment result.
            _clock.Advance(HoldDuration + Grace - TimeSpan.FromSeconds(1));
            await SweepAsync();
            Assert.Equal(BookingStatus.PaymentPending, (await ReadBookingAsync(booking.Id)).Status);

            var result = await CompleteAsync(booking, paymentId);

            Assert.True(result.IsSuccess);
            Assert.Equal(BookingStatus.Confirmed, (await ReadBookingAsync(booking.Id)).Status);
            Assert.Empty(Published<RefundRequested>());
        }

        [Fact]
        public async Task PaymentSucceeded_AfterTheBookingExpired_ShouldRequestARefund()
        {
            var (booking, userId, paymentId) = await HoldWithPendingPaymentAsync();

            // No payment result arrived in time: the sweeper gives the seat up.
            _clock.Advance(HoldDuration + Grace);
            await SweepAsync();
            Assert.Equal(BookingStatus.Expired, (await ReadBookingAsync(booking.Id)).Status);
            Assert.Contains(Published<HoldExpired>(), e => e.BookingId == booking.Id);

            var result = await CompleteAsync(booking, paymentId);

            Assert.False(result.IsSuccess);
            Assert.Equal(BookingStatus.Expired, (await ReadBookingAsync(booking.Id)).Status);
            Assert.Empty(Published<BookingConfirmed>());

            var refund = Assert.Single(Published<RefundRequested>());
            Assert.Equal((booking.Id, booking.SeatId, userId, paymentId), (refund.BookingId, refund.SeatId, refund.UserId, refund.PaymentId));
            Assert.Equal("hold_expired", refund.Reason);
        }

        [Fact]
        public async Task PaymentSucceeded_ForABookingThatNoLongerExists_ShouldRequestARefund_FromThePaymentMessage()
        {
            var paymentId = Guid.NewGuid();

            var result = await CompleteAsync(int.MaxValue, paymentId, seatId: 12, userId: 34);

            Assert.Equal(BookingError.BookingNotFound, result.Error);
            var refund = Assert.Single(Published<RefundRequested>());
            Assert.Equal(new RefundRequested(int.MaxValue, 12, 34, Start.UtcDateTime, paymentId, "booking_not_found"), refund);
            Assert.Empty(Published<BookingConfirmed>());
        }

        [Fact]
        public async Task PaymentSucceeded_ForAReleasedHold_ShouldRequestARefund()
        {
            var (booking, userId, paymentId) = await HoldWithPendingPaymentAsync();
            // The payment failed first, the owner released the hold, and then a "succeeded" for the same attempt arrives.
            await FailAsync(booking.Id, paymentId);
            await ReleaseAsync(booking.Id, userId);

            var result = await CompleteAsync(booking, paymentId);

            Assert.False(result.IsSuccess);
            var refund = Assert.Single(Published<RefundRequested>());
            Assert.Equal((booking.Id, booking.SeatId, userId, paymentId, "hold_not_active"),
                (refund.BookingId, refund.SeatId, refund.UserId, refund.PaymentId, refund.Reason));
            Assert.Equal(BookingStatus.Released, (await ReadBookingAsync(booking.Id)).Status);
        }

        [Fact]
        public async Task PaymentPendingBooking_ShouldKeepTheSeat_CountTowardsTheLimit_AndNotBeReleasable()
        {
            var (booking, userId, _) = await HoldWithPendingPaymentAsync();
            var otherUserId = await _fixture.CreateUserAsync();

            Assert.Equal(BookingError.SeatHeld, (await HoldAsync(booking.SeatId, otherUserId)).Error);
            Assert.Equal(BookingError.PaymentInProgress, (await ReleaseAsync(booking.Id, userId)).Error);
            Assert.Equal(BookingStatus.PaymentPending, (await ReadBookingAsync(booking.Id)).Status);

            // Three more holds reach the default limit of 4.
            for (var i = 0; i < 3; i++)
            {
                Assert.True((await HoldAsync(await _fixture.CreateFreeSeatAsync(), userId)).IsSuccess);
            }
            Assert.Equal(BookingError.HoldLimitReached, (await HoldAsync(await _fixture.CreateFreeSeatAsync(), userId)).Error);
        }

        [Fact]
        public async Task Hold_ShouldTakeOverASeat_WhosePendingPaymentRanPastTheGracePeriod()
        {
            var (booking, _, _) = await HoldWithPendingPaymentAsync();
            var otherUserId = await _fixture.CreateUserAsync();

            _clock.Advance(HoldDuration + Grace - TimeSpan.FromSeconds(1));
            Assert.Equal(BookingError.SeatHeld, (await HoldAsync(booking.SeatId, otherUserId)).Error);

            _clock.Advance(TimeSpan.FromSeconds(1));
            var takeover = await HoldAsync(booking.SeatId, otherUserId);

            Assert.True(takeover.IsSuccess);
            Assert.Equal(BookingStatus.Expired, (await ReadBookingAsync(booking.Id)).Status);
            Assert.Single(Published<HoldExpired>(), e => e.BookingId == booking.Id);
        }

        [Fact]
        public async Task HoldEvents_ShouldBePublishedOncePerChange()
        {
            var seatId = await _fixture.CreateFreeSeatAsync();
            var users = await _fixture.CreateUsersAsync(3);

            // Held; holding it again changes nothing and announces nothing.
            var first = await HoldAsync(seatId, users[0]);
            await HoldAsync(seatId, users[0]);
            var held = Assert.Single(Published<SeatHeld>());
            Assert.Equal(new SeatHeld(first.Booking!.Id, seatId, users[0], Start.UtcDateTime, Start.UtcDateTime + HoldDuration), held);

            // Released; a second release is rejected and announces nothing.
            _clock.Advance(TimeSpan.FromSeconds(10));
            await ReleaseAsync(first.Booking.Id, users[0]);
            await ReleaseAsync(first.Booking.Id, users[0]);
            Assert.Equal(new HoldReleased(first.Booking.Id, seatId, users[0], _clock.GetUtcNow().UtcDateTime), Assert.Single(Published<HoldReleased>()));

            // Expired lazily, by the next user's hold.
            var second = await HoldAsync(seatId, users[1]);
            _clock.Advance(HoldDuration);
            var third = await HoldAsync(seatId, users[2]);
            Assert.Equal(new HoldExpired(second.Booking!.Id, seatId, users[1], _clock.GetUtcNow().UtcDateTime), Assert.Single(Published<HoldExpired>()));
            Assert.Equal(3, Published<SeatHeld>().Count);

            // Expired by the sweeper; a second sweep finds nothing new.
            _clock.Advance(HoldDuration);
            await SweepAsync();
            await SweepAsync();
            Assert.Single(Published<HoldExpired>(), e => e.BookingId == third.Booking!.Id);
        }

        [Fact]
        public async Task PaymentResultRacingTheSweeper_ShouldEndInExactlyOneOutcome()
        {
            const int rounds = 25;
            var failedRounds = new List<string>();

            // Around the end of the grace period the two sides read the clock at slightly different moments:
            // the payment result just before it, the sweeper just after. Only one of the two updates may win.
            var sweeperClock = new FakeTimeProvider(Start + HoldDuration + Grace + TimeSpan.FromSeconds(1));

            for (var round = 1; round <= rounds; round++)
            {
                var bus = new Mock<IPublishEndpoint>();
                var (booking, _, paymentId) = await HoldWithPendingPaymentAsync();

                var completeTask = Task.Run(() => WithServiceAsync(s => s.CompletePaymentAsync(ContainersFixture.SuccessfulPayment(booking.Id, paymentId, booking.SeatId, booking.UserId)), bus: bus.Object));
                var sweepTask = Task.Run(() => WithServiceAsync(s => s.ExpireDueHoldsAsync(), sweeperClock, bus.Object));
                await sweepTask;
                var complete = await completeTask;

                var status = (await ReadBookingAsync(booking.Id)).Status;
                var confirmed = ContainersFixture.PublishedTo<BookingConfirmed>(bus).Count(e => e.BookingId == booking.Id);
                var refunds = ContainersFixture.PublishedTo<RefundRequested>(bus).Count(e => e.BookingId == booking.Id);
                var expired = ContainersFixture.PublishedTo<HoldExpired>(bus).Count(e => e.BookingId == booking.Id);

                var paymentWon = complete.IsSuccess && status == BookingStatus.Confirmed && confirmed == 1 && refunds == 0 && expired == 0;
                var sweeperWon = !complete.IsSuccess && status == BookingStatus.Expired && confirmed == 0 && refunds == 1 && expired == 1;

                if (!paymentWon && !sweeperWon)
                {
                    failedRounds.Add(
                        $"round {round}: complete success={complete.IsSuccess} error={complete.Error}, status={status}, " +
                        $"confirmed={confirmed}, refunds={refunds}, expired={expired}");
                }
            }

            Assert.True(failedRounds.Count == 0,
                $"{failedRounds.Count}/{rounds} rounds ended in a mixed outcome:\n" + string.Join("\n", failedRounds));
        }
    }
}
