namespace Auction.Web.Services.Ui;

/// <summary>
/// Demo helpers (R7.1). Bound from config section "Demo" (Demo__Enabled, Demo__Password).
/// The password never goes to the browser: the quick-login form sends only the user name.
/// </summary>
public sealed class DemoOptions
{
    public bool Enabled { get; set; }

    /// <summary>The seeded users' password (SEED_PASSWORD). Quick login is hidden when it is empty.</summary>
    public string Password { get; set; } = "";

    public string[] Users { get; set; } = ["admin", "seller1", "seller2", "seller3", "alice", "bob", "carol", "dave", "erin"];

    public bool QuickLoginOn => Enabled && Password.Length > 0;

    public static string EmailOf(string user) => $"{user}@demo.local";
}