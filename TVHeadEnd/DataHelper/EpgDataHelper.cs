using System.Collections.Generic;
using System.Linq;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.DataHelper;

public sealed class EpgDataHelper
{
    private readonly Dictionary<long, HTSMessage> _events = new();
    private readonly Dictionary<long, HashSet<long>> _channels = new();

    public void Clean()
    {
        lock (_events)
        {
            _events.Clear();
            _channels.Clear();
        }
    }

    public bool Update(HTSMessage message)
    {
        if (!message.TryGetLong("eventId", out var id)) return false;
        lock (_events)
        {
            _events.TryGetValue(id, out var previous);
            if (message.Method == "eventDelete")
            {
                if (previous == null) return false;
                RemoveFromChannel(id, previous);
                return _events.Remove(id);
            }

            var merged = new HTSMessage();
            if (previous != null)
                foreach (var field in previous) merged.putField(field.Key, field.Value);
            var changed = previous == null;
            foreach (var field in message)
            {
                if (field.Key == "method") continue;
                changed |= !merged.containsField(field.Key) || !Equals(merged.GetField(field.Key), field.Value);
                merged.putField(field.Key, field.Value);
            }
            if (!changed) return false;
            if (previous != null) RemoveFromChannel(id, previous);
            _events[id] = merged;
            if (merged.TryGetLong("channelId", out var channel))
            {
                if (!_channels.TryGetValue(channel, out var ids)) _channels[channel] = ids = new();
                ids.Add(id);
            }
            return true;
        }
    }

    public bool RemoveChannel(long channelId)
    {
        lock (_events)
        {
            if (!_channels.Remove(channelId, out var ids)) return false;
            foreach (var id in ids) _events.Remove(id);
            return true;
        }
    }

    public HTSMessage[] GetEvents(long channel, long startUnix = long.MinValue, long endUnix = long.MaxValue)
    {
        lock (_events)
        {
            if (!_channels.TryGetValue(channel, out var ids)) return [];
            return ids.Select(id => _events[id])
                .Where(message => message.TryGetLong("start", out var start)
                    && message.TryGetLong("stop", out var stop)
                    && start >= 0 && stop >= start && stop <= 253402300799L
                    && stop >= startUnix && start <= endUnix)
                .Select(Clone).ToArray();
        }
    }

    private static HTSMessage Clone(HTSMessage source)
    {
        var copy = new HTSMessage();
        foreach (var field in source) copy.putField(field.Key, field.Value);
        return copy;
    }

    private void RemoveFromChannel(long id, HTSMessage message)
    {
        if (message.TryGetLong("channelId", out var channel) && _channels.TryGetValue(channel, out var ids))
        {
            ids.Remove(id);
            if (ids.Count == 0) _channels.Remove(channel);
        }
    }
}
