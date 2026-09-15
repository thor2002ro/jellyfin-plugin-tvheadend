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
                if (!_data.TryGetValue(id, out HTSMessage oldMessage) || oldMessage == null)
                {
                    _logger.LogDebug("[TVHclient] AutorecDataHelper.autorecEntryUpdate: id not in database - skipping");
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

        public string GetTitle(string id)
        {
            lock (_data)
            {
                return _data.TryGetValue(id, out HTSMessage message) ? message.getString("title", null) : null;
            }
        }

        public Task<IEnumerable<SeriesTimerInfo>> buildAutorecInfos(CancellationToken cancellationToken, int serverUtcOffsetMinutes = 0)
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
                            sti.RecordNewOnly = broadcastType is 1 or 3;
                        }

                        if (m.TryGetInt("daysOfWeek", out var daysOfWeek))
                        {
                            sti.Days = getDayOfWeekListFromInt(daysOfWeek);
                        }

                        int start = m.getInt("start", -1);
                        int startWindow = m.getInt("startWindow", -1);
                        sti.RecordAnyTime = start < 0 || startWindow < 0;
                        if (!sti.RecordAnyTime)
                        {
                            DateTime serverMidnight = DateTime.UtcNow.AddMinutes(serverUtcOffsetMinutes).Date;
                            sti.StartDate = DateTime.SpecifyKind(serverMidnight.AddMinutes(start - serverUtcOffsetMinutes), DateTimeKind.Utc);
                            sti.EndDate = DateTime.SpecifyKind(serverMidnight.AddMinutes(startWindow - serverUtcOffsetMinutes), DateTimeKind.Utc);
                            if (sti.EndDate < sti.StartDate)
                            {
                                sti.EndDate = sti.EndDate.AddDays(1);
                            }
                        }
                        else
                        {
                            sti.StartDate = DateTime.UtcNow;
                            sti.EndDate = sti.StartDate;
                        }

                        if (m.TryGetLong("channel", out var channel))
                        {
                            sti.ChannelId = channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            sti.RecordAnyChannel = false;
                        }
                        else
                        {
                            sti.RecordAnyChannel = true;
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

                        m.TryGetString("title", out var title);
                        sti.Name = m.getString("name", title);

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

        public static int getMinutesFromMidnight(DateTime time, int serverUtcOffsetMinutes = 0)
        {
            DateTime serverTime = time.ToUniversalTime().AddMinutes(serverUtcOffsetMinutes);
            return (serverTime.Hour * 60) + serverTime.Minute;
        }
    }
}
