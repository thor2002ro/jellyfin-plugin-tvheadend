using System.Collections.Generic;
using System.Threading;
using System;

namespace TVHeadEnd.Helper
{
    public class ByteList
    {
        private readonly List<byte> _data;

        public ByteList()
        {
            _data = new List<byte>();
        }

        public byte[] getFromStart(int count)
        {
            lock (_data)
            {
                while (_data.Count < count)
                {
                    Monitor.Wait(_data);
                }
                return _data.GetRange(0, count).ToArray();
            }
        }

        public bool TryGetFromStart(int count, out byte[] result, CancellationToken cancellationToken, TimeSpan waitTimeout)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            result = null;
            lock (_data)
            {
                while (_data.Count < count)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    if (!Monitor.Wait(_data, waitTimeout))
                    {
                        return false;
                    }
                }

                result = _data.GetRange(0, count).ToArray();
                return true;
            }
        }

        public byte[] extractFromStart(int count)
        {
            lock (_data)
            {
                while (_data.Count < count)
                {
                    Monitor.Wait(_data);
                }
                byte[] result = _data.GetRange(0, count).ToArray();
                _data.RemoveRange(0, count);
                return result;
            }
        }

        public bool TryExtractFromStart(int count, out byte[] result, CancellationToken cancellationToken, TimeSpan waitTimeout)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            result = null;
            lock (_data)
            {
                while (_data.Count < count)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    if (!Monitor.Wait(_data, waitTimeout))
                    {
                        return false;
                    }
                }

                result = _data.GetRange(0, count).ToArray();
                _data.RemoveRange(0, count);
                return true;
            }
        }

        public void appendAll(byte[] data)
        {
            lock (_data)
            {
                _data.AddRange(data);
                if (_data.Count >= 1)
                {
                    // wake up any blocked dequeue
                    Monitor.PulseAll(_data);
                }
            }
        }

        public void appendCount(byte[] data, long count)
        {
            if (count < 0 || count > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            lock (_data)
            {
                int length = (int)count;
                byte[] dataRange = new byte[length];
                Array.Copy(data, 0, dataRange, 0, dataRange.Length);
                appendAll(dataRange);
            }
        }

        public int Count()
        {
            lock (_data)
            {
                return _data.Count;
            }
        }
    }
}
