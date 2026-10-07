using Microsoft.Extensions.Logging.Abstractions;
using NexaVerify.Application.Abstractions;
using NexaVerify.Infrastructure.Background;

namespace NexaVerify.Infrastructure.UnitTests.Background;

public class EmailDispatcherTests
{
    private sealed class FlakySender : IEmailSender
    {
        private readonly int _failures;
        private int _calls;

        public FlakySender(int failures)
        {
            _failures = failures;
        }

        public int Calls => _calls;

        public List<EmailMessage> Delivered { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) <= _failures)
            {
                throw new InvalidOperationException("smtp down");
            }

            Delivered.Add(message);
            return Task.CompletedTask;
        }
    }

    private static async Task<FlakySender> RunAsync(int failures)
    {
        var outbox = new EmailOutbox(NullLogger<EmailOutbox>.Instance);
        var sender = new FlakySender(failures);
        var dispatcher = new EmailDispatcher(outbox, sender, NullLogger<EmailDispatcher>.Instance, [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5)]);
        outbox.Enqueue(new EmailMessage("a@b.c", "Hi", "Body"));

        await dispatcher.StartAsync(default);
        await Task.Delay(300);
        await dispatcher.StopAsync(default);
        return sender;
    }

    [Fact]
    public async Task Delivers_queued_mail()
    {
        var sender = await RunAsync(failures: 0);

        sender.Delivered.Single().To.ShouldBe("a@b.c");
    }

    [Fact]
    public async Task Retries_transient_failures()
    {
        var sender = await RunAsync(failures: 2);

        sender.Calls.ShouldBe(3);
        sender.Delivered.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Gives_up_after_the_configured_retries_without_throwing()
    {
        var sender = await RunAsync(failures: 100);

        sender.Calls.ShouldBe(3); // first attempt + two retries
        sender.Delivered.ShouldBeEmpty();
    }
}
