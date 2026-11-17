using System.Diagnostics.CodeAnalysis;
using Auction.Domain.Errors;
using Google.Protobuf;
using Grpc.Core;

namespace Auction.Contracts.Errors;

/// <summary>Builds and reads the ErrorDetail that rides on a failed gRPC call.</summary>
public static class RpcErrors
{
    public const string DetailTrailer = "error-detail-bin";
    public const string CorrelationHeader = "x-correlation-id";

    public static RpcException Create(string code, string message, string? correlationId,
        IEnumerable<FieldIssue>? fields = null, long retryAfterMs = 0)
    {
        var (status, retryable) = ErrorMap.For(code);
        var detail = new ErrorDetail
        {
            Code = code,
            Message = message,
            Retryable = retryable,
            CorrelationId = correlationId ?? "",
            RetryAfterMs = retryAfterMs,
        };

        if (fields != null)
        {
            foreach (var field in fields)
            {
                detail.Fields.Add(new FieldError { Field = field.Field, Rule = field.Rule, Message = field.Message });
            }
        }

        var trailers = new Metadata { { DetailTrailer, detail.ToByteArray() } };
        if (!string.IsNullOrEmpty(correlationId))
        {
            trailers.Add(CorrelationHeader, correlationId);
        }

        return new RpcException(new Status(status, message), trailers, message);
    }

    public static RpcException From(DomainException ex, string? correlationId) =>
        Create(ex.Code, ex.Message, correlationId, ex.Fields, ex.RetryAfterMs);

    /// <summary>Unknown errors: never pass on the exception text, only the correlation id.</summary>
    public static RpcException Internal(string? correlationId) =>
        Create(ErrorCodes.Internal, "Something went wrong on our side.", correlationId);

    public static bool TryGetDetail(RpcException ex, [NotNullWhen(true)] out ErrorDetail? detail)
    {
        var entry = ex.Trailers.Get(DetailTrailer);
        if (entry is { IsBinary: true })
        {
            try
            {
                detail = ErrorDetail.Parser.ParseFrom(entry.ValueBytes);
                return true;
            }
            catch (InvalidProtocolBufferException)
            {
                // fall through
            }
        }

        detail = null;
        return false;
    }

    /// <summary>The error code of a failed call, even when the server sent no ErrorDetail.</summary>
    public static string CodeOf(RpcException ex)
    {
        if (TryGetDetail(ex, out var detail))
        {
            return detail.Code;
        }

        return ex.StatusCode switch
        {
            StatusCode.Unauthenticated => ErrorCodes.Unauthenticated,
            StatusCode.Unavailable => ErrorCodes.ServiceUnavailable,
            StatusCode.DeadlineExceeded => ErrorCodes.Timeout,
            _ => ErrorCodes.Internal,
        };
    }

    /// <summary>A message that is safe to show the user.</summary>
    public static string MessageOf(RpcException ex) =>
        TryGetDetail(ex, out var detail) ? detail.Message : "The service is not available. Please try again.";
}
