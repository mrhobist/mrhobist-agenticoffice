using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MrHobist.AITeam.Api.Scene;

/// <summary>Sahneye yayimlanan tek olay. <see cref="Json"/> UI'a oldugu gibi gider.</summary>
public sealed record SceneEvent(string Type, string Json, DateTimeOffset At);

/// <summary>
/// Bellek ici yayin kanali: her SSE abonesi kendi kuyrugunu alir.
/// Kalici degildir; sahne kozmetiktir, calisma gecmisi <c>runs/</c> JSONL'dedir.
/// Faz 5'te <c>RunService</c> ayni kanala yazar; bugun <c>POST /api/v1/scene/commands</c> yazar.
/// </summary>
public sealed class SceneEventBus
{
    private readonly ConcurrentDictionary<Channel<SceneEvent>, byte> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public ChannelReader<SceneEvent> Subscribe(out Channel<SceneEvent> handle)
    {
        // Yavas bir istemci yayini kilitlemesin: dolunca en eskisi dusurulur.
        handle = Channel.CreateBounded<SceneEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _subscribers[handle] = 0;
        return handle.Reader;
    }

    public void Unsubscribe(Channel<SceneEvent> handle)
    {
        if (_subscribers.TryRemove(handle, out _))
        {
            handle.Writer.TryComplete();
        }
    }

    public void Publish(SceneEvent evt)
    {
        foreach (var ch in _subscribers.Keys)
        {
            ch.Writer.TryWrite(evt);
        }
    }
}
