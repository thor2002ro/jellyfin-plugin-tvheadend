using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.DataHelper
{
    public class AutorecDataHelper
    {
        private readonly ILogger<AutorecDataHelper> _logger;
        private readonly Dictionary<string, HTSMessage> _data;

        private readonly DateTime _initialDateTimeUTC = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public AutorecDataHelper(ILogger<AutorecDataHelper> logger)
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

        public void autorecEntryAdd(HTSMessage message)
        {
            string id = message.getString("id");
            lock (_data)
            {
                if (_data.ContainsKey(id))
                {
                    _logger.LogDebug("[TVHclient] AutorecDataHelper.autorecEntryAdd: id already in database - skipping");
                    return;
                }
                _data.Add(id, message);
            }
        }

        public void autorecEntryUpdate(HTSMessage message)
        {
            string id = message.getString("id");
            lock (_data)
            {
                HTSMessage oldMessage = _data[id];
                if (oldMessage == null)
                {
                    _logger.LogDebug("[TVHclient] AutorecDataHelper.autorecEntryAdd: id not in database - skipping");
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

        public void autorecEntryDelete(HTSMessage message)
        {
            string id = message.getString("id");
            lock (_data)
            {
                _data.Remove(id);
            }
        }

        public Task<IEnumerable<SeriesTimerInfo>> buildAutorecInfos(CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew<IEnumerable<SeriesTimerInfo>>(() =>
            {
                lock (_data)
                {
                    List<SeriesTimerInfo> result = new List<SeriesTimerInfo>();

                    foreach (KeyValuePair<string, HTSMessage> entry in _data)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogDebug("[TVHclient] AutorecDataHelper.buildAutorecInfos: call cancelled - returning partial list");
                            return result;
                        }

                        HTSMessage m = entry.Value;
                        SeriesTimerInfo sti = new SeriesTimerInfo();

                        if (m.TryGetString("id", out var id))
                        {
                            sti.Id = id;
                        }

                        if (m.TryGetInt("broadcastType", out var broadcastType))
                        {
                            sti.RecordNewOnly = broadcastType == 1 || broadcastType == 3;
                        }

                        if (m.TryGetInt("daysOfWeek", out var daysOfWeek))
                        {
                            sti.Days = getDayOfWeekListFromInt(daysOfWeek);
                        }

                        sti.StartDate = DateTime.Now.ToUniversalTime();

                        if (m.TryGetInt("retention", out var retentionInDays))
                        {
                            try
                            {
                                if (DateTime.MaxValue.AddDays(-retentionInDays) < DateTime.Now)
                                {
                                    _logger.LogError("[TVHclient] AutorecDataHelper.buildAutorecInfos: change during 'EndDate' calculation: set retention value from '{days}' to '365' days", retentionInDays);
                                    sti.EndDate = DateTime.Now.AddDays(365).ToUniversalTime();
                                }
                                else
                                {
                                    sti.EndDate = DateTime.Now.AddDays(retentionInDays).ToUniversalTime();
                                }
                            }
                            catch (ArgumentOutOfRangeException e)
                            {
                                _logger.LogError(e, "[TVHclient] AutorecDataHelper.buildAutorecInfos: exception during 'EndDate' calculation. HTSMessage: {m}", m.ToString());
                            }
                        }

                        if (m.TryGetLong("channel", out var channel))
                        {
                            sti.ChannelId = channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }

                        if (m.TryGetLong("startExtra", out var startExtra))
                        {
                            sti.PrePaddingSeconds = (int)startExtra * 60;
                            sti.IsPrePaddingRequired = true;
                        }

                        if (m.TryGetLong("stopExtra", out var stopExtra))
                        {
                            sti.PostPaddingSeconds = (int)stopExtra * 60;
                            sti.IsPostPaddingRequired = true;
                        }

                        if (m.TryGetString("title", out var title))
                        {
                            sti.Name = title;
                            sti.SeriesId = title;
                        }

                        if (m.TryGetString("description", out var description))
                        {
                            sti.Overview = description;
                        }

                        if (string.IsNullOrWhiteSpace(sti.Overview) && m.TryGetString("comment", out var comment))
                        {
                            sti.Overview = comment;
                        }

                        if (m.TryGetInt("priority", out var priority))
                        {
                            sti.Priority = priority;
                        }

                        /*
                                public string ProgramId { get; set; }
                                public bool RecordAnyChannel { get; set; }
                                public bool RecordAnyTime { get; set; }
                                public bool RecordNewOnly { get; set; }
                         */

                        result.Add(sti);
                    }

                    return result;
                }
            });
        }

        private List<DayOfWeek> getDayOfWeekListFromInt(int daysOfWeek)
        {
            List<DayOfWeek> result = new List<DayOfWeek>();
            if ((daysOfWeek & 0x01) != 0)
            {
                result.Add(DayOfWeek.Monday);
            }
            if ((daysOfWeek & 0x02) != 0)
            {
                result.Add(DayOfWeek.Tuesday);
            }
            if ((daysOfWeek & 0x04) != 0)
            {
                result.Add(DayOfWeek.Wednesday);
            }
            if ((daysOfWeek & 0x08) != 0)
            {
                result.Add(DayOfWeek.Thursday);
            }
            if ((daysOfWeek & 0x10) != 0)
            {
                result.Add(DayOfWeek.Friday);
            }
            if ((daysOfWeek & 0x20) != 0)
            {
                result.Add(DayOfWeek.Saturday);
            }
            if ((daysOfWeek & 0x40) != 0)
            {
                result.Add(DayOfWeek.Sunday);
            }
            return result;
        }

        public static int getDaysOfWeekFromList(List<DayOfWeek> days)
        {
            int result = 0;
            foreach (DayOfWeek currDay in days)
            {
                switch (currDay)
                {
                    case DayOfWeek.Monday:
                        result = result | 0x1;
                        break;
                    case DayOfWeek.Tuesday:
                        result = result | 0x2;
                        break;
                    case DayOfWeek.Wednesday:
                        result = result | 0x4;
                        break;
                    case DayOfWeek.Thursday:
                        result = result | 0x8;
                        break;
                    case DayOfWeek.Friday:
                        result = result | 0x10;
                        break;
                    case DayOfWeek.Saturday:
                        result = result | 0x20;
                        break;
                    case DayOfWeek.Sunday:
                        result = result | 0x40;
                        break;
                }
            }
            return result;
        }

        public static int getMinutesFromMidnight(DateTime time)
        {
            DateTime utcTime = time.ToUniversalTime();
            int hours = utcTime.Hour;
            int minute = utcTime.Minute;
            int minutes = (hours * 60) + minute;
            return minutes;
        }
    }
}
