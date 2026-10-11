using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexaVerify.Application.Common;
using NexaVerify.Domain.Messaging;
using NexaVerify.Infrastructure.Background;
using NexaVerify.Infrastructure.Messaging;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace NexaVerify.Infrastructure.IntegrationTests.Messaging;

[Collection(SqlServerCollection.Name)]
public class EmailDispatcherTests
{
    private readonly SqlServerFixture _fixture;

    public EmailDispatcherTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private static Func<OutgoingEmail, CancellationToken, Task> Fail(EmailFailureKind kind, string text = "SMTP 451 4.3.0 try later") =>
        (_, _) => throw new EmailSendException(kind, text);

    // ---------------------------------------------------------------- staging

    [Fact]
    public async Task A_staged_mail_is_stored_only_when_the_unit_of_work_saves()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);

        await using (var scope = rig.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NexaVerify.Application.Messaging.IEmailSender>().SendAsync(EmailRig.Reset("ada@example.com"), default);
            // never saved: the business change was abandoned
        }

        (await rig.ScalarAsync<int>("SELECT COUNT(*) FROM tenancy.OutboundEmails")).ShouldBe(0);
    }

    [Fact]
    public async Task A_mail_staged_in_a_transaction_that_rolls_back_is_not_stored_and_one_that_commits_is()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);
        await using var scope = rig.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<NexaVerify.Application.Messaging.IEmailSender>();

        await Should.ThrowAsync<InvalidOperationException>(() => db.ExecuteInTransactionAsync<bool>(async ct =>
        {
            await sender.SendAsync(EmailRig.Reset("rolled@example.com"), ct);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("business rule failed after the mail was staged");
        }));
        (await rig.ScalarAsync<int>("SELECT COUNT(*) FROM tenancy.OutboundEmails")).ShouldBe(0);

        db.ChangeTracker.Clear();
        await db.ExecuteInTransactionAsync(async ct =>
        {
            await sender.SendAsync(EmailRig.Reset("kept@example.com"), ct);
            await db.SaveChangesAsync(ct);
            return true;
        });
        (await rig.ScalarAsync<int>("SELECT COUNT(*) FROM tenancy.OutboundEmails WHERE ToAddress = 'kept@example.com'")).ShouldBe(1);
    }

    [Fact]
    public async Task Queueing_stores_the_rendered_message_with_an_unsent_state_and_never_calls_the_mail_server()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);

        var id = (await rig.EnqueueAsync(EmailRig.Reset("Ada@Example.com")))[0];

        var mail = await rig.GetAsync(id);
        mail.Status.ShouldBe(EmailStatus.Pending);
        mail.ToAddress.ShouldBe("Ada@Example.com");
        mail.ToDomain.ShouldBe("example.com");
        mail.Subject.ShouldBe("Reset your NexaVerify password");
        mail.TextBody!.ShouldContain("https://portal.example.com/reset-password?email=Ada%40Example.com&token=TOKEN");
        mail.HtmlBody!.ShouldContain("<!DOCTYPE html>");
        mail.HasOneTimeToken.ShouldBeTrue();
        mail.MessageId.ShouldEndWith("@example.com");
        mail.CorrelationId.ShouldNotBeNullOrWhiteSpace();
        mail.Attempts.ShouldBe(0);
        rig.Transport.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unusable_recipient_address_is_dropped_with_a_warning_and_no_row()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);
        await using var scope = rig.Services.CreateAsyncScope();

        var id = await scope.ServiceProvider.GetRequiredService<NexaVerify.Application.Messaging.IEmailSender>().SendAsync(EmailRig.Reset("not an address"), default);
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync();

        id.ShouldBeNull();
        (await rig.ScalarAsync<int>("SELECT COUNT(*) FROM tenancy.OutboundEmails")).ShouldBe(0);
        rig.Logs.Lines.ShouldContain(l => l.EventId == 8310 && l.Level == LogLevel.Warning);
        rig.Logs.Lines.ShouldNotContain(l => l.Message.Contains("not an address", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- delivery

    [Fact]
    public async Task Due_mail_is_delivered_marked_sent_and_one_time_bodies_are_erased()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);
        var ids = await rig.EnqueueAsync(EmailRig.Reset("a@example.com"), EmailRig.Changed("b@example.com"));

        var attempted = await rig.Dispatcher.DispatchDueAsync(default);

        attempted.ShouldBe(2);
        rig.Transport.Sent.Count.ShouldBe(2);
        var token = await rig.GetAsync(ids[0]);
        token.Status.ShouldBe(EmailStatus.Sent);
        token.Attempts.ShouldBe(1);
        token.SentAt.ShouldNotBeNull();
        token.TextBody.ShouldBeNull();
        token.HtmlBody.ShouldBeNull();
        token.BodiesPurgedAt.ShouldNotBeNull();
        var plain = await rig.GetAsync(ids[1]);
        plain.Status.ShouldBe(EmailStatus.Sent);
        plain.TextBody.ShouldNotBeNull(); // kept until the 30-day purge
        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(0);
        rig.Logs.Lines.ShouldNotContain(l => l.Message.Contains("TOKEN", StringComparison.Ordinal)); // no link token in any log line
    }

    [Fact]
    public async Task A_message_is_not_attempted_before_it_is_due()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);
        var id = (await rig.EnqueueAsync(EmailRig.Changed("a@example.com")))[0];
        await rig.ExecuteAsync("UPDATE tenancy.OutboundEmails SET NextAttemptAt = @at WHERE Id = @id", new SqlParameter("@at", rig.Time.GetUtcNow().UtcDateTime.AddMinutes(5)), new SqlParameter("@id", id));

        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(0);
        rig.Time.Advance(TimeSpan.FromMinutes(6));
        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(1);
    }

    [Fact]
    public async Task Two_dispatchers_never_send_the_same_message_twice()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture, o => { o.Dispatcher.BatchSize = 8; o.Dispatcher.Parallelism = 4; o.MaxPerMinute = 100000; o.Dispatcher.MaxPerDomainPerCycle = 100; });
        rig.Transport.Behaviour = async (_, ct) => await Task.Delay(15, ct);
        var requests = Enumerable.Range(0, 120).Select(i => EmailRig.Changed($"user{i}@d{i % 7}.example")).ToArray();
        var ids = await rig.EnqueueAsync(requests);
        var nodeA = rig.Dispatcher;
        var nodeB = rig.AnotherNode();

        async Task<int> DrainAsync(EmailDispatcher node)
        {
            var total = 0;
            for (var idle = 0; idle < 3;)
            {
                var n = await node.DispatchDueAsync(default);
                total += n;
                idle = n == 0 ? idle + 1 : 0;
            }

            return total;
        }

        var counts = await Task.WhenAll(Task.Run(() => DrainAsync(nodeA)), Task.Run(() => DrainAsync(nodeB)));

        counts.Sum().ShouldBe(120);
        counts.ShouldAllBe(c => c > 0); // both nodes took part
        rig.Transport.Sent.Select(m => m.Id).Distinct().Count().ShouldBe(120);
        rig.Transport.Sent.Count.ShouldBe(120); // not one duplicate
        (await rig.ScalarAsync<int>("SELECT COUNT(*) FROM tenancy.OutboundEmails WHERE Status = 'Sent' AND Attempts = 1")).ShouldBe(120);
        ids.Count.ShouldBe(120);
    }

    // ---------------------------------------------------------------- failures and retries

    [Fact]
    public async Task A_transient_failure_is_retried_with_exponential_backoff_and_then_delivered()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture, o => { o.Dispatcher.MaxAttempts = 5; o.Dispatcher.JitterPercent = 0; });
        var id = (await rig.EnqueueAsync(EmailRig.Changed("a@example.com")))[0];
        var failures = 2;
        rig.Transport.Behaviour = (_, _) => failures-- > 0 ? throw new EmailSendException(EmailFailureKind.Transient, "SMTP 451 4.3.0 try later") : Task.CompletedTask;

        await rig.Dispatcher.DispatchDueAsync(default);
        var first = await rig.GetAsync(id);
        first.Status.ShouldBe(EmailStatus.Pending);
        first.Attempts.ShouldBe(1);
        first.LastError.ShouldBe("SMTP 451 4.3.0 try later");
        first.NextAttemptAt.ShouldBe(rig.Time.GetUtcNow().UtcDateTime.AddSeconds(30), TimeSpan.FromMilliseconds(5));

        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(0); // not due yet
        rig.Time.Advance(TimeSpan.FromSeconds(31));
        await rig.Dispatcher.DispatchDueAsync(default);
        var second = await rig.GetAsync(id);
        second.Attempts.ShouldBe(2);
        second.NextAttemptAt.ShouldBe(rig.Time.GetUtcNow().UtcDateTime.AddSeconds(60), TimeSpan.FromMilliseconds(5)); // doubled

        rig.Time.Advance(TimeSpan.FromSeconds(61));
        await rig.Dispatcher.DispatchDueAsync(default);
        var done = await rig.GetAsync(id);
        done.Status.ShouldBe(EmailStatus.Sent);
        done.Attempts.ShouldBe(3);
        done.LastError.ShouldBeNull();
        rig.Logs.Lines.Count(l => l.EventId == 8321 && l.Level == LogLevel.Warning).ShouldBe(2);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_abandoned_with_a_critical_log_and_a_sanitised_error()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture, o => { o.Dispatcher.MaxAttempts = 3; o.Dispatcher.MaxDelaySeconds = 60; });
        var id = (await rig.EnqueueAsync(EmailRig.Changed("a@example.com")))[0];
        rig.Transport.Behaviour = Fail(EmailFailureKind.Transient, "SMTP 421 4.4.2 service not available for a@example.com");

        for (var i = 0; i < 3; i++)
        {
            await rig.Dispatcher.DispatchDueAsync(default);
            rig.Time.Advance(TimeSpan.FromMinutes(5));
        }

        var mail = await rig.GetAsync(id);
        mail.Status.ShouldBe(EmailStatus.Abandoned);
        mail.Attempts.ShouldBe(3);
        mail.LastError.ShouldNotBeNull();
        rig.Logs.Lines.Single(l => l.EventId == 8323).Level.ShouldBe(LogLevel.Critical);
        rig.Logs.Lines.Single(l => l.EventId == 8323).Message.ShouldNotContain("a@example.com");
        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(0); // abandoned means abandoned
    }

    [Fact]
    public async Task A_permanent_failure_ends_the_message_immediately_and_a_refused_recipient_is_suppressed()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);
        var id = (await rig.EnqueueAsync(EmailRig.Changed("Gone@Example.com")))[0];
        rig.Transport.Behaviour = Fail(EmailFailureKind.PermanentRecipient, "SMTP 550 5.1.1 user unknown");

        await rig.Dispatcher.DispatchDueAsync(default);

        var mail = await rig.GetAsync(id);
        mail.Status.ShouldBe(EmailStatus.Failed);
        mail.Attempts.ShouldBe(1);
        (await rig.ScalarAsync<string>("SELECT Address FROM tenancy.EmailSuppressions")).ShouldBe("gone@example.com");
        rig.Logs.Lines.ShouldContain(l => l.EventId == 8322);
    }

    [Fact]
    public async Task Mail_to_a_suppressed_address_is_not_sent_until_an_administrator_clears_it()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);
        await rig.ExecuteAsync(
            "INSERT INTO tenancy.EmailSuppressions (Id, Address, Reason, CreatedAt) VALUES (NEWID(), 'blocked@example.com', 'test', SYSUTCDATETIME())");
        var id = (await rig.EnqueueAsync(EmailRig.Changed("Blocked@Example.com")))[0];

        await rig.Dispatcher.DispatchDueAsync(default);

        rig.Transport.Sent.ShouldBeEmpty();
        var mail = await rig.GetAsync(id);
        mail.Status.ShouldBe(EmailStatus.Failed);
        mail.LastError!.ShouldContain("suppression");

        await rig.ExecuteAsync("DELETE FROM tenancy.EmailSuppressions");
        await rig.ExecuteAsync("UPDATE tenancy.OutboundEmails SET Status = 'Pending', Attempts = 0, LastError = NULL WHERE Id = @id", new SqlParameter("@id", id));
        await rig.Dispatcher.DispatchDueAsync(default);
        (await rig.GetAsync(id)).Status.ShouldBe(EmailStatus.Sent);
    }

    [Fact]
    public async Task A_permanent_failure_that_is_not_about_the_recipient_does_not_suppress_the_address()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);
        var id = (await rig.EnqueueAsync(EmailRig.Changed("a@example.com")))[0];
        rig.Transport.Behaviour = Fail(EmailFailureKind.Permanent, "SMTP 554 5.7.1 sender not verified");

        await rig.Dispatcher.DispatchDueAsync(default);

        (await rig.GetAsync(id)).Status.ShouldBe(EmailStatus.Failed);
        (await rig.ScalarAsync<int>("SELECT COUNT(*) FROM tenancy.EmailSuppressions")).ShouldBe(0);
    }

    [Fact]
    public async Task An_unexpected_transport_exception_counts_as_a_transient_failure()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);
        var id = (await rig.EnqueueAsync(EmailRig.Changed("a@example.com")))[0];
        rig.Transport.Behaviour = (_, _) => throw new InvalidOperationException("secret-looking detail: hunter2");

        await rig.Dispatcher.DispatchDueAsync(default);

        var mail = await rig.GetAsync(id);
        mail.Status.ShouldBe(EmailStatus.Pending);
        mail.LastError!.ShouldNotContain("hunter2");
    }

    // ---------------------------------------------------------------- leases, crashes, shutdown

    [Fact]
    public async Task A_message_whose_node_died_is_picked_up_again_after_its_lease_expires_but_not_before()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);
        var id = (await rig.EnqueueAsync(EmailRig.Changed("a@example.com")))[0];
        var now = rig.Time.GetUtcNow().UtcDateTime;
        await rig.ExecuteAsync(
            "UPDATE tenancy.OutboundEmails SET Status = 'Sending', Attempts = 1, LeaseToken = NEWID(), LeaseUntil = @until WHERE Id = @id",
            new SqlParameter("@until", now.AddMinutes(10)), new SqlParameter("@id", id));

        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(0); // still leased to the (maybe alive) node

        rig.Time.Advance(TimeSpan.FromMinutes(11));
        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(1);
        var mail = await rig.GetAsync(id);
        mail.Status.ShouldBe(EmailStatus.Sent);
        mail.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task A_message_that_keeps_crashing_its_node_is_abandoned_without_another_send()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture, o => o.Dispatcher.MaxAttempts = 3);
        var id = (await rig.EnqueueAsync(EmailRig.Changed("a@example.com")))[0];
        await rig.ExecuteAsync(
            "UPDATE tenancy.OutboundEmails SET Status = 'Sending', Attempts = 3, LeaseToken = NEWID(), LeaseUntil = @until WHERE Id = @id",
            new SqlParameter("@until", rig.Time.GetUtcNow().UtcDateTime.AddMinutes(-1)), new SqlParameter("@id", id));

        await rig.Dispatcher.DispatchDueAsync(default);

        rig.Transport.Sent.ShouldBeEmpty();
        (await rig.GetAsync(id)).Status.ShouldBe(EmailStatus.Abandoned);
        rig.Logs.Lines.ShouldContain(l => l.EventId == 8323 && l.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task A_node_that_lost_its_lease_does_not_overwrite_the_other_nodes_result()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);
        var id = (await rig.EnqueueAsync(EmailRig.Changed("a@example.com")))[0];
        rig.Transport.Behaviour = async (_, _) =>
        {
            // while this node is "sending", another node takes the message over
            await rig.ExecuteAsync("UPDATE tenancy.OutboundEmails SET LeaseToken = NEWID() WHERE Id = @id", new SqlParameter("@id", id));
        };

        await rig.Dispatcher.DispatchDueAsync(default);

        var mail = await rig.GetAsync(id);
        mail.Status.ShouldBe(EmailStatus.Sending); // untouched: the new owner decides
        rig.Logs.Lines.ShouldContain(l => l.EventId == 8326);
    }

    [Fact]
    public async Task Shutdown_hands_back_claimed_messages_without_counting_the_attempt()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture, o => { o.Dispatcher.BatchSize = 10; o.Dispatcher.Parallelism = 2; });
        var ids = await rig.EnqueueAsync(Enumerable.Range(0, 6).Select(i => EmailRig.Changed($"u{i}@d{i}.example")).ToArray());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transport.Behaviour = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        using var cts = new CancellationTokenSource();

        var running = rig.Dispatcher.DispatchDueAsync(cts.Token);
        await started.Task;
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => running);

        foreach (var id in ids)
        {
            var mail = await rig.GetAsync(id);
            mail.Status.ShouldBe(EmailStatus.Pending);
            mail.Attempts.ShouldBe(0);
            mail.LeaseToken.ShouldBeNull();
        }

        rig.Logs.Lines.ShouldContain(l => l.EventId == 8329);
        rig.Transport.Behaviour = null;
        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(6); // all go out on the next start
    }

    // ---------------------------------------------------------------- throttles

    [Fact]
    public async Task The_platform_wide_per_minute_limit_pauses_sending_until_the_window_moves_on()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture, o => { o.MaxPerMinute = 5; o.Dispatcher.BatchSize = 50; o.Dispatcher.MaxPerDomainPerCycle = 50; });
        await rig.EnqueueAsync(Enumerable.Range(0, 12).Select(i => EmailRig.Changed($"u{i}@d{i}.example")).ToArray());

        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(5);
        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(0);
        rig.Logs.Lines.ShouldContain(l => l.EventId == 8325);

        rig.Time.Advance(TimeSpan.FromSeconds(61));
        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(5);
        rig.Time.Advance(TimeSpan.FromSeconds(61));
        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(2);
        rig.Transport.Sent.Count.ShouldBe(12);
    }

    [Fact]
    public async Task The_limit_is_shared_by_all_nodes()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture, o => { o.MaxPerMinute = 6; o.Dispatcher.BatchSize = 50; o.Dispatcher.MaxPerDomainPerCycle = 50; });
        await rig.EnqueueAsync(Enumerable.Range(0, 20).Select(i => EmailRig.Changed($"u{i}@d{i}.example")).ToArray());
        var other = rig.AnotherNode();

        var a = await rig.Dispatcher.DispatchDueAsync(default);
        var b = await other.DispatchDueAsync(default);

        (a + b).ShouldBe(6);
        b.ShouldBe(0);
    }

    [Fact]
    public async Task One_recipient_domain_cannot_take_a_whole_cycle()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture, o => { o.Dispatcher.BatchSize = 50; o.Dispatcher.MaxPerDomainPerCycle = 2; o.MaxPerMinute = 1000; });
        await rig.EnqueueAsync([.. Enumerable.Range(0, 8).Select(i => EmailRig.Changed($"u{i}@big-provider.example")), EmailRig.Changed("x@other-one.example"), EmailRig.Changed("y@other-two.example")]);

        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(4); // 2 from the big provider + 1 + 1
        rig.Transport.Sent.Count(m => m.ToAddress.EndsWith("@big-provider.example", StringComparison.Ordinal)).ShouldBe(2);
        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(2);
        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(2);
        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(2);
        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(0);
        rig.Transport.Sent.Count.ShouldBe(10);
    }

    // ---------------------------------------------------------------- retention

    [Fact]
    public async Task Housekeeping_deletes_old_finished_mail_erases_dead_links_and_keeps_everything_else()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);
        var ids = await rig.EnqueueAsync(
            EmailRig.Changed("oldsent@example.com"), EmailRig.Changed("newsent@example.com"), EmailRig.Changed("oldpending@example.com"),
            EmailRig.Reset("oldfailedtoken@example.com"), EmailRig.Reset("newfailedtoken@example.com"), EmailRig.Changed("oldfailed@example.com"));
        var now = rig.Time.GetUtcNow().UtcDateTime;
        async Task Set(Guid id, string status, int ageDays) => await rig.ExecuteAsync(
            "UPDATE tenancy.OutboundEmails SET Status = @s, CreatedAt = @c WHERE Id = @id",
            new SqlParameter("@s", status), new SqlParameter("@c", now.AddDays(-ageDays)), new SqlParameter("@id", id));
        await Set(ids[0], "Sent", 31);
        await Set(ids[1], "Sent", 2);
        await Set(ids[2], "Pending", 40);
        await Set(ids[3], "Failed", 10);
        await Set(ids[4], "Failed", 1);
        await Set(ids[5], "Failed", 45);

        await rig.Dispatcher.PurgeAsync(default);

        var remaining = (await rig.ScalarAsync<string>("SELECT STRING_AGG(CONVERT(varchar(40), Id), ',') FROM tenancy.OutboundEmails")).Split(',').Select(Guid.Parse).ToHashSet();
        remaining.ShouldBe([ids[1], ids[2], ids[3], ids[4]], ignoreOrder: true);
        (await rig.GetAsync(ids[3])).TextBody.ShouldBeNull(); // a dead reset link older than a week is erased
        (await rig.GetAsync(ids[3])).BodiesPurgedAt.ShouldNotBeNull();
        (await rig.GetAsync(ids[4])).TextBody.ShouldNotBeNull(); // a recent failure can still be retried
        (await rig.GetAsync(ids[1])).TextBody.ShouldNotBeNull();
    }

    [Fact]
    public async Task Housekeeping_runs_at_most_hourly()
    {
        await using var rig = await EmailRig.CreateAsync(_fixture);
        var id = (await rig.EnqueueAsync(EmailRig.Changed("a@example.com")))[0];
        await rig.Dispatcher.PurgeAsync(default);
        await rig.ExecuteAsync("UPDATE tenancy.OutboundEmails SET Status = 'Sent', CreatedAt = @c WHERE Id = @id", new SqlParameter("@c", rig.Time.GetUtcNow().UtcDateTime.AddDays(-60)), new SqlParameter("@id", id));

        await rig.Dispatcher.PurgeAsync(default);
        (await rig.ScalarAsync<int>("SELECT COUNT(*) FROM tenancy.OutboundEmails")).ShouldBe(1);

        rig.Time.Advance(TimeSpan.FromMinutes(61));
        await rig.Dispatcher.PurgeAsync(default);
        (await rig.ScalarAsync<int>("SELECT COUNT(*) FROM tenancy.OutboundEmails")).ShouldBe(0);
    }
}
