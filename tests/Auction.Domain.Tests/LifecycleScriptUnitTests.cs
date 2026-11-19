using Auction.Infrastructure.Bidding.Persistence;
using Auction.Infrastructure.Lifecycle;
using Auction.Infrastructure.Live;
using Auction.Infrastructure.Redis;
using FluentAssertions;
using Xunit;

namespace Auction.Domain.Tests;

/// <summary>
/// Spec 05 task 2 without Redis: the shared emit part is pasted into every script, answers and keys
/// are read the right way, and the new State fields reach the persister and the live events.
/// The scripts themselves run in Auction.Integration.Tests/LifecycleScriptTests.
/// </summary>
public class LifecycleScriptUnitTests
{
    private static readonly string[] StateScripts = ["load.lua", "open.lua", "soon.lua", "seal.lua", "settle.lua", "control.lua", "rebuild.lua", "freeze.lua"];

    [Theory]
    [MemberData(nameof(ScriptNames))]
    public void Every_lifecycle_script_gets_the_shared_emit_part(string script)
    {
        var text = LuaScripts.Load(script);

        text.Should().Contain("local function emitState").And.NotContain("--@include");
    }

    public static TheoryData<string> ScriptNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in StateScripts)
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void Scripts_without_an_include_load_unchanged()
    {
        LuaScripts.Load("bid.lua").Should().Contain("-- bid.lua").And.NotContain("emitState");
    }

    [Fact]
    public void Emit_writes_the_fields_the_persister_reads()
    {
        var emit = LuaScripts.Load("state_emit.lua");

        emit.Should().Contain("'type', 'State'").And.Contain("'auction'").And.Contain("'status'").And.Contain("'change'")
            .And.Contain("'seq'").And.Contain("'eseq'").And.Contain("'at'");
    }

    [Theory]
    [InlineData("loaded", LifecycleResult.Done)]
    [InlineData("opened", LifecycleResult.Done)]
    [InlineData("sent", LifecycleResult.Done)]
    [InlineData("sealed", LifecycleResult.Done)]
    [InlineData("settled", LifecycleResult.Done)]
    [InlineData("rebuilt", LifecycleResult.Done)]
    [InlineData("ok", LifecycleResult.Done)]
    [InlineData("not_due", LifecycleResult.NotDue)]
    [InlineData("skip", LifecycleResult.Skipped)]
    [InlineData("exists", LifecycleResult.Exists)]
    [InlineData("invalid_state", LifecycleResult.InvalidState)]
    [InlineData("missing", LifecycleResult.Missing)]
    public void Script_answers_are_read(string answer, LifecycleResult expected) =>
        LifecycleScripts.Parse(answer).Should().Be(expected);

    [Fact]
    public void Unknown_answer_throws()
    {
        var parse = () => LifecycleScripts.Parse("what");

        parse.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [MemberData(nameof(ScriptReturnWords))]
    public void Every_word_a_script_returns_is_known(string script, string word)
    {
        LuaScripts.Load(script).Should().Contain($"return '{word}'");
        var parse = () => LifecycleScripts.Parse(word);
        parse.Should().NotThrow();
    }

    public static TheoryData<string, string> ScriptReturnWords() => new()
    {
        { "load.lua", "loaded" }, { "load.lua", "exists" },
        { "open.lua", "opened" }, { "open.lua", "not_due" }, { "open.lua", "skip" }, { "open.lua", "missing" },
        { "soon.lua", "sent" }, { "soon.lua", "not_due" }, { "seal.lua", "sealed" }, { "seal.lua", "not_due" },
        { "settle.lua", "settled" }, { "control.lua", "ok" }, { "control.lua", "invalid_state" },
        { "rebuild.lua", "rebuilt" }, { "rebuild.lua", "exists" },
    };

    [Fact]
    public void Keys_come_in_the_order_the_scripts_expect()
    {
        var id = Guid.NewGuid();
        var keys = new AuctionKeys(id, 3);

        LifecycleScripts.KeysFor(keys).Select(k => k.ToString()).Should().Equal(
            $"{{p3}}:a:{id}:state", $"{{p3}}:a:{id}:events", "{p3}:stream",
            "{p3}:starts", "{p3}:ends", "{p3}:soon", "{p3}:sealed",
            $"{{p3}}:a:{id}:max", $"{{p3}}:a:{id}:bids");
    }

    [Fact]
    public void Persister_reads_change_outcome_final_price_and_winner()
    {
        var auction = Guid.NewGuid();
        var winner = Guid.NewGuid();
        var entry = PersistEntryParser.Parse("1-0", new Dictionary<string, string>
        {
            ["type"] = "State", ["auction"] = auction.ToString(), ["status"] = "Settled", ["change"] = "Settled",
            ["seq"] = "7", ["eseq"] = "9", ["at"] = "100", ["outcome"] = "Sold", ["finalC"] = "12500", ["winner"] = winner.ToString(),
        });

        entry.Should().Match<PersistEntry>(e =>
            e.Status == "Settled" && e.Change == "Settled" && e.Outcome == "Sold" && e.FinalC == 12500 && e.Winner == winner);
    }

    [Fact]
    public void Old_state_entries_without_the_new_fields_still_parse()
    {
        var entry = PersistEntryParser.Parse("1-0", new Dictionary<string, string>
        {
            ["type"] = "State", ["auction"] = Guid.NewGuid().ToString(), ["status"] = "Frozen", ["seq"] = "5", ["eseq"] = "6", ["at"] = "100",
        });

        entry.Change.Should().BeNull();
        entry.Winner.Should().BeNull();
        entry.FinalC.Should().Be(0);
    }

    [Fact]
    public void Live_event_has_the_change_and_the_masked_winner()
    {
        var winner = Guid.NewGuid();
        var fields = new Dictionary<string, string>
        {
            ["type"] = "State", ["auction"] = Guid.NewGuid().ToString(), ["status"] = "Settled", ["change"] = "Settled",
            ["seq"] = "7", ["eseq"] = "9", ["at"] = "100", ["outcome"] = "Sold", ["finalC"] = "12500", ["winner"] = winner.ToString(),
        };
        var entry = PersistEntryParser.Parse("1-0", fields);

        LiveEventMapper.UsersIn(entry).Should().Contain(winner);
        var live = LiveEventMapper.Map(entry, fields, new Dictionary<Guid, string> { [winner] = "b***r" })!;

        live.StateChanged.Change.Should().Be("Settled");
        live.StateChanged.Outcome.Should().Be("Sold");
        live.StateChanged.Winner.Handle.Should().Be("b***r");
        live.StateChanged.FinalPrice.Should().NotBeNull();
    }

    [Fact]
    public void Stored_event_has_the_change_and_the_masked_winner()
    {
        var winner = Guid.NewGuid();
        var stored = StoredEventMapper.Parse(
            $"{{\"seq\":4,\"type\":\"State\",\"status\":\"Settled\",\"change\":\"Settled\",\"atMs\":50,\"outcome\":\"Sold\",\"finalC\":900,\"winner\":\"{winner}\"}}")!;

        StoredEventMapper.UsersIn(stored).Should().Contain(winner);
        var live = StoredEventMapper.Map(Guid.NewGuid(), stored, "USD", new Dictionary<Guid, string> { [winner] = "w***n" });

        live.StateChanged.Change.Should().Be("Settled");
        live.StateChanged.Winner.Handle.Should().Be("w***n");
    }

    [Fact]
    public void Stored_event_with_an_empty_winner_has_none()
    {
        var stored = StoredEventMapper.Parse("{\"seq\":4,\"type\":\"State\",\"status\":\"Settled\",\"change\":\"Settled\",\"atMs\":50,\"outcome\":\"Unsold\",\"finalC\":0,\"winner\":\"\"}")!;

        StoredEventMapper.Map(Guid.NewGuid(), stored, "USD", new Dictionary<Guid, string>()).StateChanged.Winner.Should().BeNull();
    }
}
