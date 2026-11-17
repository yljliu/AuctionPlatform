using System.Reflection;
using Auction.Contracts.Errors;
using Auction.Domain.Errors;
using FluentAssertions;
using Grpc.Core;
using Xunit;

namespace Auction.Domain.Tests;

public class ErrorMapTests
{
    // Copied from design table 5.4. If the table changes, change this list too.
    public static TheoryData<string, StatusCode> Table => new()
    {
        { "VALIDATION_FAILED", StatusCode.InvalidArgument },
        { "UNAUTHENTICATED", StatusCode.Unauthenticated },
        { "INVALID_CREDENTIALS", StatusCode.Unauthenticated },
        { "TOTP_REQUIRED", StatusCode.Unauthenticated },
        { "TOTP_INVALID", StatusCode.Unauthenticated },
        { "REFRESH_REUSED", StatusCode.Unauthenticated },
        { "WEBHOOK_SIGNATURE_INVALID", StatusCode.Unauthenticated },
        { "ACCOUNT_LOCKED", StatusCode.PermissionDenied },
        { "EMAIL_NOT_VERIFIED", StatusCode.FailedPrecondition },
        { "ACCOUNT_SUSPENDED", StatusCode.PermissionDenied },
        { "ACCOUNT_BANNED", StatusCode.PermissionDenied },
        { "ROLE_REQUIRED", StatusCode.PermissionDenied },
        { "NOT_OWNER", StatusCode.PermissionDenied },
        { "SELLER_NOT_VERIFIED", StatusCode.PermissionDenied },
        { "OWN_LOT", StatusCode.PermissionDenied },
        { "NOT_FOUND", StatusCode.NotFound },
        { "USERNAME_TAKEN", StatusCode.AlreadyExists },
        { "SLUG_TAKEN", StatusCode.AlreadyExists },
        { "ALREADY_APPLIED", StatusCode.AlreadyExists },
        { "ALREADY_RATED", StatusCode.AlreadyExists },
        { "TOKEN_INVALID", StatusCode.FailedPrecondition },
        { "BELOW_MINIMUM", StatusCode.FailedPrecondition },
        { "NOT_LIVE", StatusCode.FailedPrecondition },
        { "ENDED", StatusCode.FailedPrecondition },
        { "AUCTION_FROZEN", StatusCode.FailedPrecondition },
        { "INSUFFICIENT_FUNDS", StatusCode.FailedPrecondition },
        { "EDIT_LOCKED", StatusCode.FailedPrecondition },
        { "INVALID_STATE", StatusCode.FailedPrecondition },
        { "DEADLINE_PASSED", StatusCode.FailedPrecondition },
        { "BUY_NOW_UNAVAILABLE", StatusCode.FailedPrecondition },
        { "IMAGE_REQUIRED", StatusCode.FailedPrecondition },
        { "UPLOAD_INVALID", StatusCode.InvalidArgument },
        { "PAYMENT_DECLINED", StatusCode.FailedPrecondition },
        { "INSTALLMENTS_NOT_ALLOWED", StatusCode.FailedPrecondition },
        { "DEMO_MODE_ONLY", StatusCode.FailedPrecondition },
        { "RUN_ALREADY_ACTIVE", StatusCode.FailedPrecondition },
        { "IDEMPOTENCY_MISMATCH", StatusCode.Aborted },
        { "VERSION_CONFLICT", StatusCode.Aborted },
        { "RATE_LIMITED", StatusCode.ResourceExhausted },
        { "SERVICE_UNAVAILABLE", StatusCode.Unavailable },
        { "TIMEOUT", StatusCode.DeadlineExceeded },
        { "INTERNAL", StatusCode.Internal },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Code_maps_to_the_status_in_table_5_4(string code, StatusCode expected)
    {
        ErrorMap.For(code).Status.Should().Be(expected);
    }

    [Fact]
    public void Every_code_constant_is_in_the_map()
    {
        var constants = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        constants.Should().HaveCount(42);
        ErrorMap.KnownCodes.Should().BeEquivalentTo(constants);
    }

    [Fact]
    public void Unknown_code_maps_to_internal()
    {
        ErrorMap.For("NO_SUCH_CODE").Status.Should().Be(StatusCode.Internal);
    }

    [Theory]
    [InlineData("RATE_LIMITED", true)]
    [InlineData("SERVICE_UNAVAILABLE", true)]
    [InlineData("TIMEOUT", true)]
    [InlineData("VALIDATION_FAILED", false)]
    [InlineData("INTERNAL", false)]
    public void Retryable_flag(string code, bool retryable)
    {
        ErrorMap.For(code).Retryable.Should().Be(retryable);
    }

    [Fact]
    public void Domain_exception_becomes_rpc_exception_with_detail_trailer()
    {
        var ex = new DomainException(ErrorCodes.ValidationFailed, "Bad input",
            [new FieldIssue("username", "length", "Too short"), new FieldIssue("email", "format", "Not an email")]);

        var rpc = RpcErrors.From(ex, "trace-123");

        rpc.StatusCode.Should().Be(StatusCode.InvalidArgument);
        RpcErrors.TryGetDetail(rpc, out var detail).Should().BeTrue();
        detail!.Code.Should().Be("VALIDATION_FAILED");
        detail.CorrelationId.Should().Be("trace-123");
        detail.Retryable.Should().BeFalse();
        detail.Fields.Select(f => f.Field).Should().Equal("username", "email");
        rpc.Trailers.GetValue(RpcErrors.CorrelationHeader).Should().Be("trace-123");
    }

    [Fact]
    public void Retry_after_is_carried()
    {
        var ex = new DomainException(ErrorCodes.RateLimited, "Slow down") { RetryAfterMs = 1500 };

        RpcErrors.TryGetDetail(RpcErrors.From(ex, null), out var detail).Should().BeTrue();

        detail!.RetryAfterMs.Should().Be(1500);
        detail.Retryable.Should().BeTrue();
    }

    [Fact]
    public void Internal_error_does_not_leak_text()
    {
        var rpc = RpcErrors.Internal("trace-9");

        rpc.StatusCode.Should().Be(StatusCode.Internal);
        rpc.Status.Detail.Should().NotContain("Exception");
        RpcErrors.CodeOf(rpc).Should().Be("INTERNAL");
    }

    [Fact]
    public void Code_of_call_without_detail_uses_the_status()
    {
        var rpc = new RpcException(new Status(StatusCode.Unauthenticated, "no token"));

        RpcErrors.CodeOf(rpc).Should().Be("UNAUTHENTICATED");
    }
}
