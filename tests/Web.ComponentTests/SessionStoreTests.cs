using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

internal static class SessionFixtures
{
    public static readonly DateTimeOffset Start = new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    public static SessionOptions Options { get; } = new() { IdleTimeoutMinutes = 30, AbsoluteTimeoutHours = 12, RefreshSkewSeconds = 30 };

    public static (DistributedSessionStore Store, IDistributedCache Cache, FakeTimeProvider Clock) NewStore(SessionOptions? options = null)
    {
        var clock = new FakeTimeProvider(Start);
        var cache = new DictionaryCache();
        var store = new DistributedSessionStore(cache, new EphemeralDataProtectionProvider(), clock, Microsoft.Extensions.Options.Options.Create(options ?? Options));
        return (store, cache, clock);
    }

    public static PortalSession NewSession(FakeTimeProvider clock, string id = "sid-1", string access = "access-1", string refresh = "refresh-1", int accessLifetimeSeconds = 900, string portal = PortalKinds.Admin) =>
        new()
        {
            Id = id,
            AccessToken = access,
            AccessTokenExpiresAt = clock.GetUtcNow().AddSeconds(accessLifetimeSeconds),
            RefreshToken = refresh,
            UserId = Guid.NewGuid(),
            Email = "admin@nexaverify.test",
            FullName = "Ada Admin",
            Portal = portal,
            Roles = ["SuperAdmin"],
            Permissions = ["dashboard.admin", "clients.read"],
            CreatedAt = clock.GetUtcNow(),
            LastSeenAt = clock.GetUtcNow(),
            AbsoluteExpiresAt = clock.GetUtcNow().AddHours(12),
        };

    /// <summary>Plain dictionary cache: expiry is the store's job (it checks timestamps itself), so the cache never evicts on its own clock.</summary>
    public sealed class DictionaryCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _items = [];

        public byte[]? Get(string key) => _items.GetValueOrDefault(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => _items.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _items[key] = value;

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }
}

public class SessionStoreTests
{
    [Fact]
    public async Task A_saved_session_can_be_read_back()
    {
        var (store, _, clock) = SessionFixtures.NewStore();
        var session = SessionFixtures.NewSession(clock);

        await store.SaveAsync(session);
        var loaded = await store.GetAsync("sid-1");

        loaded.ShouldNotBeNull();
        loaded.AccessToken.ShouldBe("access-1");
        loaded.Permissions.ShouldBe(["dashboard.admin", "clients.read"]);
    }

    [Fact]
    public async Task Tokens_are_encrypted_in_the_backing_cache()
    {
        var (store, cache, clock) = SessionFixtures.NewStore();
        await store.SaveAsync(SessionFixtures.NewSession(clock, access: "ACCESS-SECRET-VALUE", refresh: "REFRESH-SECRET-VALUE"));

        var raw = await cache.GetAsync("nv:session:sid-1");

        raw.ShouldNotBeNull();
        var text = Encoding.UTF8.GetString(raw);
        text.ShouldNotContain("ACCESS-SECRET-VALUE");
        text.ShouldNotContain("REFRESH-SECRET-VALUE");
        text.ShouldNotContain("admin@nexaverify.test");
    }

    [Fact]
    public async Task A_corrupted_or_foreign_payload_counts_as_no_session()
    {
        var (store, cache, _) = SessionFixtures.NewStore();
        await cache.SetAsync("nv:session:sid-9", Encoding.UTF8.GetBytes("{\"AccessToken\":\"forged\"}"));

        (await store.GetAsync("sid-9")).ShouldBeNull();
    }

    [Fact]
    public async Task Idle_sessions_expire_but_activity_slides_the_window()
    {
        var (store, _, clock) = SessionFixtures.NewStore();
        await store.SaveAsync(SessionFixtures.NewSession(clock));

        clock.Advance(TimeSpan.FromMinutes(20));
        await store.TouchAsync("sid-1");
        clock.Advance(TimeSpan.FromMinutes(20));
        (await store.GetAsync("sid-1")).ShouldNotBeNull("40 minutes in, but the user was active 20 minutes ago");

        clock.Advance(TimeSpan.FromMinutes(31));
        (await store.GetAsync("sid-1")).ShouldBeNull("31 idle minutes");
    }

    [Fact]
    public async Task Sessions_end_at_the_absolute_limit_however_active_the_user_is()
    {
        var (store, _, clock) = SessionFixtures.NewStore();
        await store.SaveAsync(SessionFixtures.NewSession(clock));

        for (var i = 0; i < 12; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(59));
            await store.TouchAsync("sid-1");
        }

        clock.Advance(TimeSpan.FromMinutes(10));
        await store.TouchAsync("sid-1");
        (await store.GetAsync("sid-1")).ShouldBeNull("12 hours after sign-in");
    }

    [Fact]
    public async Task Updates_cannot_extend_the_absolute_lifetime_or_change_the_id()
    {
        var (store, _, clock) = SessionFixtures.NewStore();
        var original = SessionFixtures.NewSession(clock);
        await store.SaveAsync(original);

        var updated = await store.UpdateAsync("sid-1", s => s with { Id = "hijack", AbsoluteExpiresAt = s.AbsoluteExpiresAt.AddYears(1), AccessToken = "access-2" });

        updated!.Id.ShouldBe("sid-1");
        updated.AbsoluteExpiresAt.ShouldBe(original.AbsoluteExpiresAt);
        updated.AccessToken.ShouldBe("access-2");
        (await store.GetAsync("hijack")).ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_updates_do_not_lose_each_other()
    {
        var (store, _, clock) = SessionFixtures.NewStore();
        await store.SaveAsync(SessionFixtures.NewSession(clock));

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            store.UpdateAsync("sid-1", s => s with { Email = s.Email + "x" })));

        (await store.GetAsync("sid-1"))!.Email.Length.ShouldBe("admin@nexaverify.test".Length + 50);
    }

    [Fact]
    public async Task Removing_a_session_ends_it_and_unknown_ids_are_harmless()
    {
        var (store, _, clock) = SessionFixtures.NewStore();
        await store.SaveAsync(SessionFixtures.NewSession(clock));

        await store.RemoveAsync("sid-1");
        await store.RemoveAsync("never-existed");

        (await store.GetAsync("sid-1")).ShouldBeNull();
        (await store.UpdateAsync("sid-1", s => s)).ShouldBeNull();
        (await store.GetAsync(new string('x', 500))).ShouldBeNull();
    }

    [Fact]
    public void Session_ids_are_long_random_and_url_safe()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => DistributedSessionStore.NewId()).ToList();

        ids.Distinct().Count().ShouldBe(100);
        ids.All(id => id.Length >= 43 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')).ShouldBeTrue();
    }

    [Fact]
    public void Printing_a_session_never_reveals_tokens()
    {
        var (_, _, clock) = SessionFixtures.NewStore();
        var text = SessionFixtures.NewSession(clock, access: "ACCESS-SECRET-VALUE", refresh: "REFRESH-SECRET-VALUE").ToString();

        text.ShouldNotContain("SECRET");
        text.ShouldContain("sid-1");
    }
}
