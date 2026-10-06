using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.DataHelper
{
    public class ChannelDataHelper
    {
        private readonly ILogger<ChannelDataHelper> _logger;
        private readonly Dictionary<long, HTSMessage> _data;
        private readonly Dictionary<long, string> _tags = new Dictionary<long, string>();
        private string _channelType4Other = "Ignore";

        public ChannelDataHelper(ILogger<ChannelDataHelper> logger)
        {
            _logger = logger;

            _data = new Dictionary<long, HTSMessage>();
        }

        public void SetChannelType4Other(string channelType4Other)
        {
            _channelType4Other = channelType4Other;
        }

        public void Clean()
        {
            lock (_data)
            {
                _data.Clear();
                _tags.Clear();
            }
        }

        public bool UpdateTag(HTSMessage message)
        {
            if (!message.TryGetLong("tagId", out var id))
            {
                return false;
            }

            lock (_data)
            {
                if (message.Method == "tagDelete")
                {
                    return _tags.Remove(id);
                }
                else if (message.containsField("tagName") && message.GetField("tagName") is string name)
                {
                    name = name.Trim();
                    if (_tags.TryGetValue(id, out var previous) && previous == name) return false;
                    _tags[id] = name;
                    return true;
                }
                return false;
            }
        }

        public bool Add(HTSMessage message)
        {
            lock (_data)
            {
                try
                {
                    if (!message.TryGetLong("channelId", out var channelID)) return false;
                    if (!_data.TryGetValue(channelID, out var storedMessage))
                    {
                        if (!message.TryGetInt("channelNumber", out var number) || number <= 0) return false;
                        storedMessage = new HTSMessage();
                        foreach (var entry in message) storedMessage.putField(entry.Key, entry.Value);
                        _data.Add(channelID, storedMessage);
                        return true;
                    }

                    var changed = false;
                    foreach (var entry in message)
                    {
                        if (entry.Key is "channelIdStr" or "channelName" or "channelNumber" or "channelNumberMinor"
                            or "channelIcon" or "services" or "tags")
                        {
                            changed |= !storedMessage.containsField(entry.Key)
                                || !FieldsEqual(storedMessage.GetField(entry.Key), entry.Value, entry.Key == "tags");
                        }
                        storedMessage.putField(entry.Key, entry.Value);
                    }
                    return changed;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TVHclient] ChannelDataHelper.Add: exception caught. HTSMessage: {m} ", message);
                    return false;
                }
            }
        }

        public bool Remove(long channelId)
        {
            lock (_data) return _data.Remove(channelId);
        }

        internal static bool FieldsEqual(object left, object right, bool unordered = false)
        {
            if (left is IList leftList && right is IList rightList)
            {
                if (unordered) return new HashSet<object>(leftList.Cast<object>()).SetEquals(rightList.Cast<object>());
                return leftList.Count == rightList.Count && leftList.Cast<object>().Zip(rightList.Cast<object>())
                    .All(pair => FieldsEqual(pair.First, pair.Second));
            }
            if (left is HTSMessage leftMessage && right is HTSMessage rightMessage)
            {
                var leftCount = 0;
                foreach (var field in leftMessage)
                {
                    leftCount++;
                    if (!rightMessage.containsField(field.Key)
                        || !FieldsEqual(field.Value, rightMessage.GetField(field.Key))) return false;
                }
                var rightCount = 0;
                foreach (var field in rightMessage) rightCount++;
                return leftCount == rightCount;
            }
            return Equals(left, right);
        }

        public long ResolveChannelId(string channelId)
        {
            if (uint.TryParse(channelId, out var numericId))
            {
                return numericId;
            }

            lock (_data)
            {
                foreach (var entry in _data)
                {
                    if (entry.Value.containsField("channelIdStr")
                        && string.Equals(entry.Value.getString("channelIdStr"), channelId, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Key;
                    }
                }
            }

            throw new ArgumentException("Unknown TVHeadend channel identifier.", nameof(channelId));
        }

        internal long[] ResolveChannelIds(IEnumerable<string> channelIds)
        {
            lock (_data)
            {
                var external = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in _data)
                    if (entry.Value.TryGetString("channelIdStr", out var id) && !string.IsNullOrWhiteSpace(id)) external.TryAdd(id, entry.Key);
                var result = new List<long>();
                foreach (var id in channelIds)
                {
                    if (uint.TryParse(id, out var numeric)) result.Add(numeric);
                    else if (id != null && external.TryGetValue(id, out var known)) result.Add(known);
                }
                return result.ToArray();
            }
        }

        public string GetExternalChannelId(long channelId)
        {
            lock (_data)
            {
                return _data.TryGetValue(channelId, out var message) && message.containsField("channelIdStr")
                    ? message.getString("channelIdStr")
                    : channelId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        public Task<IEnumerable<ChannelInfo>> BuildChannelInfos(CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew<IEnumerable<ChannelInfo>>(() =>
            {
                lock (_data)
                {
                    List<ChannelInfo> result = new List<ChannelInfo>();
                    foreach (KeyValuePair<long, HTSMessage> entry in _data)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogDebug("[TVHclient] ChannelDataHelper.buildChannelInfos: call cancelled - returning partial list");
                            return result;
                        }

                        HTSMessage m = entry.Value;

                        try
                        {
                            ChannelInfo ci = new ChannelInfo();
                            ci.Id = m.containsField("channelIdStr") ? m.getString("channelIdStr") : "" + entry.Key;

                            ci.ImagePath = "";

                            ci.Tags = m.containsField("tags") && m.GetField("tags") is IList tags
                                ? tags.Cast<object>().OfType<BigInteger>()
                                    .Where(id => id >= long.MinValue && id <= long.MaxValue)
                                    .Select(id => _tags.GetValueOrDefault((long)id))
                                    .Where(name => !string.IsNullOrWhiteSpace(name))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()
                                : Array.Empty<string>();

                            if (m.containsField("channelIcon"))
                            {
                                ci.ImageUrl = m.getString("channelIcon");
                            }
                            if (m.containsField("channelName"))
                            {
                                string name = m.getString("channelName");
                                if (string.IsNullOrEmpty(name))
                                {
                                    continue;
                                }
                                ci.Name = m.getString("channelName");
                            }

                            if (m.containsField("channelNumber"))
                            {
                                int channelNumber = m.getInt("channelNumber");
                                ci.Number = "" + channelNumber;
                                if (m.containsField("channelNumberMinor"))
                                {
                                    int channelNumberMinor = m.getInt("channelNumberMinor");
                                    ci.Number = ci.Number + "." + channelNumberMinor;
                                }
                            }

                            Boolean serviceFound = false;
                            if (m.containsField("services"))
                            {
                                IList tunerInfoList = m.getList("services");
                                if (tunerInfoList != null && tunerInfoList.Count > 0)
                                {
                                    HTSMessage firstServiceInList = (HTSMessage)tunerInfoList[0];
                                    if (firstServiceInList.containsField("providername"))
                                    {
                                        ci.ChannelGroup = firstServiceInList.getString("providername");
                                    }
                                    if (firstServiceInList.containsField("type"))
                                    {
                                        string type = firstServiceInList.getString("type").ToLower();
                                        switch (type)
                                        {
                                            case "radio":
                                                ci.ChannelType = ChannelType.Radio;
                                                serviceFound = true;
                                                break;
                                            case "sdtv":
                                            case "hdtv":
                                            case "fhdtv":
                                            case "uhdtv":
                                                ci.ChannelType = ChannelType.TV;
                                                ci.IsHD = type != "sdtv";
                                                serviceFound = true;
                                                break;
                                            case "other":
                                                switch (_channelType4Other.ToLower())
                                                {
                                                    case "tv":
                                                        _logger.LogDebug("[TVHclient] ChannelDataHelper: map service tag 'Other' to 'TV'");
                                                        ci.ChannelType = ChannelType.TV;
                                                        serviceFound = true;
                                                        break;
                                                    case "radio":
                                                        _logger.LogDebug("[TVHclient] ChannelDataHelper: map service tag 'Other' to 'Radio'");
                                                        ci.ChannelType = ChannelType.Radio;
                                                        serviceFound = true;
                                                        break;
                                                    default:
                                                        _logger.LogDebug("[TVHclient] ChannelDataHelper: don't map service tag 'Other' - will be ignored");
                                                        break;
                                                }
                                                break;
                                            default:
                                                _logger.LogDebug("[TVHclient] ChannelDataHelper: unkown service tag '{tag}' - will be ignored.", type);
                                                break;
                                        }
                                    }
                                }
                            }
                            if (!serviceFound)
                            {
                                _logger.LogDebug("[TVHclient] ChannelDataHelper: unable to detect service-type (tvheadend tag) from service list. HTSMessage: {m}", m.ToString());
                                continue;
                            }

                            _logger.LogDebug("[TVHclient] ChannelDataHelper: adding channel: {m}", ci.Name);

                            result.Add(ci);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[TVHclient] ChannelDataHelper.BuildChannelInfos: exception caught. HTSMessage: {m}", m.ToString());
                        }
                    }
                    return result;
                }
            });
        }
    }
}
