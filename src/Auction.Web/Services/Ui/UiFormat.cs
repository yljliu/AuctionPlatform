using System.Globalization;
using Auction.Contracts;
using Auction.Contracts.Errors;
using Grpc.Core;

namespace Auction.Web.Services.Ui;

/// <summary>An error to show: the safe message, the code, and the correlation id for "Ref: ..." (R8.1).</summary>
public sealed record UiError(string Code, string Message, string? CorrelationId, IReadOnlyDictionary<string, string> Fields, bool Retryable)
{
    /// <summary>"Message (Ref: abc123)" when a correlation id is known.</summary>
    public string Text => string.IsNullOrEmpty(CorrelationId) ? Message : $"{Message} (Ref: {CorrelationId})";

    public static UiError From(RpcException ex)
    {
        var fields = new Dictionary<string, string>();
        string? correlation = null;
        var retryable = ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded;
        if (RpcErrors.TryGetDetail(ex, out var detail))
        {
            correlation = string.IsNullOrEmpty(detail.CorrelationId) ? null : detail.CorrelationId;
            retryable = detail.Retryable || retryable;
            foreach (var f in detail.Fields)
            {
                fields.TryAdd(f.Field, f.Message);
            }
        }

        correlation ??= ex.Trailers.GetValue(RpcErrors.CorrelationHeader);
        return new UiError(RpcErrors.CodeOf(ex), RpcErrors.MessageOf(ex), correlation, fields, retryable);
    }
}

/// <summary>Money, time and label text used across the pages (design 7: en-US, currency code from the data).</summary>
public static class UiFormat
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static decimal Amount(Money? money) => money == null ? 0m : money.ToDomain().Amount;

    /// <summary>"$1,234.50" for USD; "1,234.50 EUR" for others.</summary>
    public static string Money(decimal amount, string? currency)
    {
        currency = string.IsNullOrWhiteSpace(currency) ? "USD" : currency;
        return currency == "USD" ? amount.ToString("C2", Us) : $"{amount.ToString("N2", Us)} {currency}";
    }

    public static string Money(Money? money) => money == null ? "-" : Money(Amount(money), money.Currency);

    public static Contracts.Money ToProto(decimal amount, string currency) => new Domain.Money(amount, currency).ToProto();

    public static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    /// <summary>Local time for the viewer, e.g. "Nov 19, 3:05 PM". offsetMinutes: browser offset from UTC.</summary>
    public static string LocalTime(long ms, int offsetMinutes = 0) =>
        ms <= 0 ? "-" : FromMs(ms).ToOffset(TimeSpan.FromMinutes(offsetMinutes)).ToString("MMM d, h:mm tt", Us);

    /// <summary>"2d 3h", "5m 10s", "Ended".</summary>
    public static string TimeLeft(long endMs, DateTimeOffset now)
    {
        var left = FromMs(endMs) - now;
        if (left <= TimeSpan.Zero)
        {
            return "Ended";
        }

        return left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h"
            : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m"
            : $"{left.Minutes}m {left.Seconds}s";
    }

    /// <summary>"LikeNew" -> "Like new", "ForParts" -> "For parts".</summary>
    public static string Words(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "";
        }

        var text = new System.Text.StringBuilder();
        foreach (var ch in name)
        {
            if (char.IsUpper(ch) && text.Length > 0)
            {
                text.Append(' ').Append(char.ToLowerInvariant(ch));
            }
            else
            {
                text.Append(ch);
            }
        }

        return text.ToString();
    }
}
