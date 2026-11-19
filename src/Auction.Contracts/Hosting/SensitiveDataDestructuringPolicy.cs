using Serilog.Core;
using Serilog.Events;

namespace Auction.Contracts.Hosting;

/// <summary>
/// Keeps secrets out of structured logs (spec 09 R3). When an object is logged with {@Obj}, properties named
/// Password, Token or AccessToken are left out, and Email is masked as j***@domain.
/// </summary>
public sealed class SensitiveDataDestructuringPolicy : IDestructuringPolicy
{
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase) { "Password", "Token", "AccessToken" };

    public bool TryDestructure(object value, ILogEventPropertyValueFactory factory, out LogEventPropertyValue result)
    {
        result = null!;
        var type = value.GetType();
        if (value is string || type.IsPrimitive || type.IsEnum || type.Namespace is null || type.Namespace.StartsWith("System", StringComparison.Ordinal))
        {
            return false;
        }

        var props = type.GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0).ToArray();
        if (!props.Any(p => Dropped.Contains(p.Name) || p.Name.Equals("Email", StringComparison.OrdinalIgnoreCase)))
        {
            return false;   // nothing sensitive: let Serilog do its normal work
        }

        var list = new List<LogEventProperty>();
        foreach (var p in props)
        {
            if (Dropped.Contains(p.Name))
            {
                continue;
            }

            var raw = p.GetValue(value);
            var shown = p.Name.Equals("Email", StringComparison.OrdinalIgnoreCase) && raw is string email ? MaskEmail(email) : raw;
            list.Add(new LogEventProperty(p.Name, factory.CreatePropertyValue(shown, destructureObjects: true)));
        }

        result = new StructureValue(list, type.Name);
        return true;
    }

    /// <summary>"justin@example.com" becomes "j***@example.com". No @ gives "***".</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }
}