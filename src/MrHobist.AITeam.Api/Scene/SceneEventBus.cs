using System.Collections.Concurrent;
using System.Threading.Channels;
using MrHobist.AITeam.Application.Abstractions;

namespace MrHobist.AITeam.Api.Scene;

/// <summary>Sahneye yayimlanan tek olay. <see cref="Json"/> UI'a oldugu gibi gider.</summary>
public sealed record SceneEvent(string Type, string Json, DateTimeOffset At);

/// <summary>
/// <see cref="ISceneEventPublisher"/>'in bellek ici uygulamasi: her SSE abonesi kendi kuyrugunu alir.
/// Kalici degildir; sahne kozmetiktir, calisma gecmisi veritabanindadir.
/// </summary>
public sealed class SceneEventBus : ISceneEventPublisher
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

    public void Publish(string type, string json)
    {
        var evt = new SceneEvent(type, json, DateTimeOffset.UtcNow);
        foreach (var ch in _subscribers.Keys)
        {
            ch.Writer.TryWrite(evt);
        }
    }
}
