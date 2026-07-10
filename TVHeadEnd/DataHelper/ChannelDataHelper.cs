using System;
using System.Collections;
using System.Collections.Generic;
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
        private readonly Dictionary<string, string> _piconData;
        private string _channelType4Other = "Ignore";

        public ChannelDataHelper(ILogger<ChannelDataHelper> logger)
        {
            _logger = logger;

            _data = new Dictionary<long, HTSMessage>();
            _piconData = new Dictionary<string, string>();
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
                _piconData.Clear();
            }
        }

        public void Add(HTSMessage message)
        {
            lock (_data)
            {
                try
                {
                    long channelID = message.getLong("channelId");
                    if (_data.ContainsKey(channelID))
                    {
                        if (storedMessage != null)
                        {
                            foreach (KeyValuePair<string, object> entry in message)
                            {
                                if (storedMessage.ContainsField(entry.Key))
                                {
                                    storedMessage.RemoveField(entry.Key);
                                }

                                storedMessage.PutField(entry.Key, entry.Value);
                            }
                        }
                        else
                        {
                            _logger.LogError("[TVHclient] ChannelDataHelper: updated data for channelID '{Id}' but no initial data found", channelID);
                        }
                    }
                    else
                    {
                        if (message.ContainsField("channelNumber") && message.GetInt("channelNumber") > 0) // use only channels with number > 0
                        {
                            _data.Add(channelID, message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TVHclient] ChannelDataHelper.Add: exception caught. HTSMessage: {M} ", message);
                }
            }
        }

        public string? GetChannelIcon4ChannelId(string channelId)
        {
            lock (_data)
            {
                return _piconData.TryGetValue(channelId, out string result) ? result : null;
            }
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
            return Task.Run<IEnumerable<ChannelInfo>>(() =>
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

                            ci.ImagePath = string.Empty;

                            if (m.ContainsField("channelIcon"))
                            {
                                string channelIcon = m.getString("channelIcon");
                                Uri uriResult;
                                bool uriCheckResult = Uri.TryCreate(channelIcon, UriKind.Absolute, out uriResult)
                                    && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
                                if (uriCheckResult)
                                {
                                    ci.ImageUrl = channelIcon;
                                    _piconData.Remove(ci.Id);
                                }
                                else if (channelIcon != null)
                                {
                                    ci.HasImage = true;
                                    _piconData[ci.Id] = channelIcon;
                                }
                            }

                            if (m.ContainsField("channelName"))
                            {
                                string? name = m.GetString("channelName");
                                if (string.IsNullOrEmpty(name))
                                {
                                    continue;
                                }

                                ci.Name = m.GetString("channelName");
                            }

                            if (m.ContainsField("channelNumber"))
                            {
                                int channelNumber = m.GetInt("channelNumber");
                                ci.Number = string.Empty + channelNumber;
                                if (m.ContainsField("channelNumberMinor"))
                                {
                                    int channelNumberMinor = m.GetInt("channelNumberMinor");
                                    ci.Number = ci.Number + "." + channelNumberMinor;
                                }
                            }

                            bool serviceFound = false;
                            if (m.ContainsField("services"))
                            {
                                IList? tunerInfoList = m.GetList("services");
                                if (tunerInfoList != null && tunerInfoList.Count > 0)
                                {
                                    HTSMessage firstServiceInList = (HTSMessage)tunerInfoList[0];
                                    if (firstServiceInList.containsField("providername"))
                                    {
                                        ci.ChannelGroup = firstServiceInList.getString("providername");
                                    }
                                    if (firstServiceInList.containsField("type"))
                                    {
                                        string? type = firstServiceInList.GetString("type")?.ToLowerInvariant();
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
                                                switch (_channelType4Other.ToLowerInvariant())
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
                                                _logger.LogDebug("[TVHclient] ChannelDataHelper: unkown service tag '{Tag}' - will be ignored.", type);
                                                break;
                                        }
                                    }
                                }
                            }

                            if (!serviceFound)
                            {
                                _logger.LogDebug("[TVHclient] ChannelDataHelper: unable to detect service-type (tvheadend tag) from service list. HTSMessage: {M}", m.ToString());
                                continue;
                            }

                            _logger.LogDebug("[TVHclient] ChannelDataHelper: adding channel: {M}", ci.Name);

                            result.Add(ci);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[TVHclient] ChannelDataHelper.BuildChannelInfos: exception caught. HTSMessage: {M}", m.ToString());
                        }
                    }

                    return result;
                }
            });
        }
    }
}
