using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using TVHeadEnd.HTSP;


namespace TVHeadEnd.DataHelper
{
    public class DvrDataHelper
    {
        private readonly ILogger<DvrDataHelper> _logger;
        private readonly Dictionary<string, HTSMessage> _data;

        private readonly DateTime _initialDateTimeUTC = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public DvrDataHelper(ILogger<DvrDataHelper> logger)
        {
            _logger = logger;
            _data = new Dictionary<string, HTSMessage>();
        }

        public void clean()
        {
            lock (_data)
            {
                _data.Clear();
            }
        }

        public void dvrEntryAdd(HTSMessage message)
        {
            string id = message.getString("id");
            lock (_data)
            {
                if (_data.ContainsKey(id))
                {
                    _logger.LogDebug("[TVHclient] DvrDataHelper.dvrEntryAdd id already in database - skipping");
                    return;
                }
                _data.Add(id, message);
            }
        }

        public void dvrEntryUpdate(HTSMessage message)
        {
            string id = message.getString("id");
            lock (_data)
            {
                if (!_data.TryGetValue(id, out HTSMessage oldMessage) || oldMessage == null)
                {
                    _logger.LogDebug("[TVHclient] DvrDataHelper.dvrEntryUpdate id not in database - skipping");
                    return;
                }
                foreach (KeyValuePair<string, object> entry in message)
                {
                    if (oldMessage.containsField(entry.Key))
                    {
                        oldMessage.removeField(entry.Key);
                    }
                    oldMessage.putField(entry.Key, entry.Value);
                }
            }
        }

        public void dvrEntryDelete(HTSMessage message)
        {
            string id = message.getString("id");
            lock (_data)
            {
                _data.Remove(id);
            }
        }

        public long ResolveDvrId(string dvrId)
        {
            if (uint.TryParse(dvrId, out var numericId))
            {
                return numericId;
            }

            lock (_data)
            {
                foreach (var entry in _data)
                {
                    if (entry.Value.containsField("idStr")
                        && string.Equals(entry.Value.getString("idStr"), dvrId, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Value.getLong("id");
                    }
                }
            }

            throw new ArgumentException("Unknown TVHeadend DVR identifier.", nameof(dvrId));
        }

        public Task<IEnumerable<MyRecordingInfo>> buildDvrInfos(CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew<IEnumerable<MyRecordingInfo>>(() =>
            {
                lock (_data)
                {
                    List<MyRecordingInfo> result = new List<MyRecordingInfo>();
                    foreach (KeyValuePair<string, HTSMessage> entry in _data)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogDebug("[TVHclient] DvrDataHelper.buildDvrInfos: call cancelled - returning partial list");
                            return result;
                        }

                        HTSMessage m = entry.Value;
                        MyRecordingInfo ri = new MyRecordingInfo();

                        if (m.TryGetString("error", out var error)
                            && error.Contains("missing", StringComparison.OrdinalIgnoreCase))
                        {
                            // TVHeadend retains deleted recordings as completed with "File missing".
                            continue;
                        }

                        if (m.TryGetString("idStr", out var id))
                        {
                            ri.Id = id;
                        }
                        else if (m.TryGetLong("id", out var numericId))
                        {
                            ri.Id = numericId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }

                        if (m.TryGetString("ratingLabel", out var ratingLabel))
                        {
                            ri.OfficialRating = ratingLabel;
                        }
                        else if (m.TryGetInt("ageRating", out var ageRating) && ageRating > 0)
                        {
                            ri.OfficialRating = ageRating + "+";
                        }

                        if (m.TryGetString("path", out var path))
                        {
                            ri.Path = path;
                        }

                        if (m.TryGetString("url", out var url))
                        {
                            ri.Url = url;
                        }

                        if (m.TryGetLong("channel", out var channel))
                        {
                            ri.ChannelId = channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }

                        if (m.TryGetLong("start", out var start))
                        {
                            ri.StartDate = _initialDateTimeUTC.AddSeconds(start).ToUniversalTime();
                        }

                        if (m.TryGetLong("stop", out var stop))
                        {
                            ri.EndDate = _initialDateTimeUTC.AddSeconds(stop).ToUniversalTime();
                        }

                        if (m.TryGetString("title", out var title))
                        {
                            ri.Name = title;
                        }

                        if (m.TryGetString("description", out var description))
                        {
                            ri.Overview = description;
                        }

                        if (string.IsNullOrWhiteSpace(ri.Overview) && m.TryGetString("comment", out var comment))
                        {
                            ri.Overview = comment;
                        }

                        if (m.TryGetString("subtitle", out var subtitle))
                        {
                            ri.EpisodeTitle = subtitle;
                            ri.IsSeries = true;
                        }

                        ri.HasImage = false;
                        // public string ImagePath { get; set; }
                        // public string ImageUrl { get; set; }

                        if (m.TryGetString("state", out var state))
                        {
                            switch (state)
                            {
                                case "completed":
                                    ri.Status = RecordingStatus.Completed;
                                    break;
                                case "scheduled":
                                    ri.Status = RecordingStatus.New;
                                    continue;
                                case "missed":
                                    ri.Status = RecordingStatus.Error;
                                    break;
                                case "recording":
                                    ri.Status = RecordingStatus.InProgress;
                                    break;
                                default:
                                    _logger.LogCritical("[TVHclient] DvrDataHelper.buildDvrInfos: state '{state}' not handled", state);
                                    continue;
                            }
                        }

                        // Path must not be set to force emby use of the LiveTvService methods!!!!
                        //if (m.containsField("path"))
                        //{
                        //    ri.Path = m.getString("path");
                        //}

                        if (m.TryGetString("autorecId", out var autorecId))
                        {
                            ri.SeriesTimerId = autorecId;
                        }

                        if (m.TryGetLong("eventId", out var eventId))
                        {
                            ri.ProgramId = eventId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }

                        /*
                                public ProgramAudio? Audio { get; set; }
                                public ChannelType ChannelType { get; set; }
                                public float? CommunityRating { get; set; }
                                public List<string> Genres { get; set; }
                                public bool? IsHD { get; set; }
                                public bool IsKids { get; set; }
                                public bool IsLive { get; set; }
                                public bool IsMovie { get; set; }
                                public bool IsNews { get; set; }
                                public bool IsPremiere { get; set; }
                                public bool IsRepeat { get; set; }
                                public bool IsSeries { get; set; }
                                public bool IsSports { get; set; }
                                public string OfficialRating { get; set; }
                                public DateTime? OriginalAirDate { get; set; }
                                public string Url { get; set; }
                         */

                        result.Add(ri);
                    }
                    return result;
                }
            });
        }

        public Task<IEnumerable<TimerInfo>> buildPendingTimersInfos(CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew<IEnumerable<TimerInfo>>(() =>
            {
                lock (_data)
                {
                    List<TimerInfo> result = new List<TimerInfo>();
                    foreach (KeyValuePair<string, HTSMessage> entry in _data)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogDebug("[TVHclient] DvrDataHelper.buildDvrInfos: call cancelled - returning partial list");
                            return result;
                        }

                        HTSMessage m = entry.Value;
                        TimerInfo ti = new TimerInfo();

                        if (m.TryGetString("idStr", out var id))
                        {
                            ti.Id = id;
                        }
                        else if (m.TryGetLong("id", out var numericId))
                        {
                            ti.Id = numericId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }

                        if (m.TryGetLong("channel", out var channel))
                        {
                            ti.ChannelId = channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }

                        if (m.TryGetLong("start", out var start))
                        {
                            ti.StartDate = _initialDateTimeUTC.AddSeconds(start).ToUniversalTime();
                        }

                        if (m.TryGetLong("stop", out var stop))
                        {
                            ti.EndDate = _initialDateTimeUTC.AddSeconds(stop).ToUniversalTime();
                        }

                        if (m.TryGetString("title", out var title))
                        {
                            ti.Name = title;
                        }

                        if (m.TryGetString("description", out var description))
                        {
                            ti.Overview = description;
                        }

                        if (string.IsNullOrWhiteSpace(ti.Overview) && m.TryGetString("comment", out var comment))
                        {
                            ti.Overview = comment;
                        }

                        if (m.TryGetString("state", out var state) && state != "scheduled")
                        {
                            // Only scheduled timers need to be delivered.
                            continue;
                        }

                        if (state == "scheduled")
                        {
                            ti.Status = RecordingStatus.New;
                        }

                        if (m.TryGetLong("startExtra", out var startExtra))
                        {
                            ti.PrePaddingSeconds = (int)startExtra * 60;
                            ti.IsPrePaddingRequired = true;
                        }

                        if (m.TryGetLong("stopExtra", out var stopExtra))
                        {
                            ti.PostPaddingSeconds = (int)stopExtra * 60;
                            ti.IsPostPaddingRequired = true;
                        }

                        if (m.TryGetInt("priority", out var priority))
                        {
                            ti.Priority = priority;
                        }

                        if (m.TryGetString("autorecId", out var autorecId))
                        {
                            ti.SeriesTimerId = autorecId;
                        }

                        if (m.TryGetLong("eventId", out var eventId))
                        {
                            ti.ProgramId = eventId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }

                        result.Add(ti);
                    }
                    return result;
                }
            });
        }
    }
}
