namespace ExamArchive.Services;

public interface IPaperParseQueue
{
    void Enqueue(int paperId);

    IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class PaperParseQueue : IPaperParseQueue
{
    private readonly System.Threading.Channels.Channel<int> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<int>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

    public void Enqueue(int paperId) => _channel.Writer.TryWrite(paperId);

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
