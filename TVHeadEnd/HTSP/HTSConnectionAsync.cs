using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP_Responses;

namespace TVHeadEnd.HTSP
{
    public class HTSConnectionAsync
    {
        private const long BytesPerGiga = 1024 * 1024 * 1024;
        private const int SocketIoTimeoutMilliseconds = 1000;
        private const int SocketReceiveBufferSize = 8192;
        private const int MaxHtsMessageLength = 64 * 1024 * 1024;

        private static readonly TimeSpan QueuePollInterval = TimeSpan.FromMilliseconds(250);
        private static readonly HTSResponseHandler NoOpResponseHandler = new NoOpHTSResponseHandler();

        private volatile Boolean _needsRestart = false;
        private volatile Boolean _connected;
        private volatile Boolean _expectedClose;
        private int _seq = 0;

        private readonly object _lock;
        private readonly HTSConnectionListener _listener;
        private readonly String _clientName;
        private readonly String _clientVersion;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<HTSConnectionAsync> _logger;

        private int _serverProtocolVersion;
        private int _negotiatedProtocolVersion;
        private string _servername;
        private string _serverversion;
        private string _serverWebRoot;
        private string _diskSpace;
        private readonly HashSet<string> _serverCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly ByteList _buffer;
        private readonly SizeQueue<HTSMessage> _receivedMessagesQueue;
        private readonly SizeQueue<HTSMessage> _messagesForSendQueue;
        private readonly ConcurrentDictionary<int, HTSResponseHandler> _responseHandlers;

        private Thread _receiveHandlerThread;
        private Thread _messageBuilderThread;
        private Thread _sendingHandlerThread;
        private Thread _messageDistributorThread;

        private CancellationTokenSource _receiveHandlerThreadTokenSource;
        private CancellationTokenSource _messageBuilderThreadTokenSource;
        private CancellationTokenSource _sendingHandlerThreadTokenSource;
        private CancellationTokenSource _messageDistributorThreadTokenSource;

        private Socket _socket = null;

        public HTSConnectionAsync(HTSConnectionListener listener, String clientName, String clientVersion, ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<HTSConnectionAsync>();

            _connected = false;
            _lock = new object();

            _listener = listener;
            _clientName = clientName;
            _clientVersion = clientVersion;

            _buffer = new ByteList();
            _receivedMessagesQueue = new SizeQueue<HTSMessage>(int.MaxValue);
            _messagesForSendQueue = new SizeQueue<HTSMessage>(int.MaxValue);
            _responseHandlers = new ConcurrentDictionary<int, HTSResponseHandler>();

            _receiveHandlerThreadTokenSource = new CancellationTokenSource();
            _messageBuilderThreadTokenSource = new CancellationTokenSource();
            _sendingHandlerThreadTokenSource = new CancellationTokenSource();
            _messageDistributorThreadTokenSource = new CancellationTokenSource();
        }

        public void stop()
        {
            _expectedClose = true;
            _connected = false;

            try
            {
                if (_receiveHandlerThread != null && _receiveHandlerThread.IsAlive)
                {
                    _receiveHandlerThreadTokenSource.Cancel();
                }
                if (_messageBuilderThread != null && _messageBuilderThread.IsAlive)
                {
                    _messageBuilderThreadTokenSource.Cancel();
                }
                if (_sendingHandlerThread != null && _sendingHandlerThread.IsAlive)
                {
                    _sendingHandlerThreadTokenSource.Cancel();
                }
                if (_messageDistributorThread != null && _messageDistributorThread.IsAlive)
                {
                    _messageDistributorThreadTokenSource.Cancel();
                }
            }
            catch
            {

            }

            CloseSocket();
            _responseHandlers.Clear();

            _needsRestart = true;
        }

        public Boolean needsRestart()
        {
            return _needsRestart;
        }

        public void BeginExpectedClose()
        {
            _expectedClose = true;
        }

        public void open(String hostname, int port)
        {
            open(hostname, port, CancellationToken.None, 0);
        }

        public void open(String hostname, int port, CancellationToken cancellationToken, int maxAttempts)
        {
            if (_connected)
            {
                return;
            }

            lock (_lock)
            {
                if (_connected)
                {
                    return;
                }

                _needsRestart = false;
                _expectedClose = false;
                ResetCancellationTokenSources();

                var attempts = 0;
                Exception lastException = null;

                while (!_connected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    attempts++;

                    try
                    {
                        // Establish the remote endpoint for the socket.

                        IPAddress ipAddress;
                        if (!IPAddress.TryParse(hostname, out ipAddress))
                        {
                            // no IP --> ask DNS
                            IPHostEntry ipHostInfo = Dns.GetHostEntry(hostname);
                            ipAddress = ipHostInfo.AddressList[0];
                        }

                        IPEndPoint remoteEP = new IPEndPoint(ipAddress, port);

                        _logger.LogDebug("[TVHclient] HTSConnectionAsync.open: IPEndPoint = '{IP}'; AddressFamily = '{AF}'",
                            remoteEP.ToString(), ipAddress.AddressFamily);

                        // Create a TCP/IP  socket.
                        _socket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        _socket.ReceiveTimeout = SocketIoTimeoutMilliseconds;
                        _socket.SendTimeout = SocketIoTimeoutMilliseconds;

                        // connect to server
                        _socket.Connect(remoteEP);

                        _connected = true;
                        _logger.LogDebug("[TVHclient] HTSConnectionAsync.open: socket connected");
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
                    {
                        lastException = ex;
                        CloseSocket();
                        _logger.LogWarning(ex, "[TVHclient] HTSConnectionAsync.open: connection attempt {Attempt} failed", attempts);

                        if (maxAttempts > 0 && attempts >= maxAttempts)
                        {
                            throw new IOException("Unable to open HTSP socket after " + attempts + " attempt(s).", lastException);
                        }

                        if (cancellationToken.WaitHandle.WaitOne(2000))
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }
                    }
                }

                ThreadStart ReceiveHandlerRef = new ThreadStart(ReceiveHandler);
                _receiveHandlerThread = new Thread(ReceiveHandlerRef);
                _receiveHandlerThread.IsBackground = true;
                _receiveHandlerThread.Start();

                ThreadStart MessageBuilderRef = new ThreadStart(MessageBuilder);
                _messageBuilderThread = new Thread(MessageBuilderRef);
                _messageBuilderThread.IsBackground = true;
                _messageBuilderThread.Start();

                ThreadStart SendingHandlerRef = new ThreadStart(SendingHandler);
                _sendingHandlerThread = new Thread(SendingHandlerRef);
                _sendingHandlerThread.IsBackground = true;
                _sendingHandlerThread.Start();

                ThreadStart MessageDistributorRef = new ThreadStart(MessageDistributor);
                _messageDistributorThread = new Thread(MessageDistributorRef);
                _messageDistributorThread.IsBackground = true;
                _messageDistributorThread.Start();
            }
        }

        public Boolean authenticate(String username, String password)
        {
            return authenticate(username, password, true);
        }

        public Boolean authenticate(String username, String password, bool enableAsyncMetadata)
        {
            return authenticate(username, password, enableAsyncMetadata, CancellationToken.None, TimeSpan.Zero);
        }

        public Boolean authenticate(String username, String password, bool enableAsyncMetadata, CancellationToken cancellationToken, TimeSpan responseTimeout)
        {
            _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: start");

            HTSMessage helloMessage = new HTSMessage();
            helloMessage.Method = "hello";
            helloMessage.putField("clientname", _clientName);
            helloMessage.putField("clientversion", _clientVersion);
            helloMessage.putField("htspversion", HTSMessage.HTSP_VERSION);
            helloMessage.putField("username", username);

            LoopBackResponseHandler loopBackResponseHandler = new LoopBackResponseHandler();
            sendMessage(helloMessage, loopBackResponseHandler);
            HTSMessage helloResponse = GetResponse(loopBackResponseHandler, cancellationToken, responseTimeout);
            if (helloResponse != null)
            {
                if (helloResponse.containsField("htspversion"))
                {
                    _serverProtocolVersion = helloResponse.getInt("htspversion");
                }
                else
                {
                    _serverProtocolVersion = -1;
                    _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: hello didn't include required field 'htspversion' - htsp incorrectly implemented by tvheadend");
                }

                if (_serverProtocolVersion < HTSMessage.HTSP_MIN_SERVER_VERSION)
                {
                    _logger.LogError("[TVHclient] HTSConnectionAsync.authenticate: server HTSP protocol version {serverVersion} is below minimum supported version {minimumVersion}",
                        _serverProtocolVersion, HTSMessage.HTSP_MIN_SERVER_VERSION);
                    return false;
                }

                if (helloResponse.containsField("servername"))
                {
                    _servername = helloResponse.getString("servername");
                }
                else
                {
                    _servername = "n/a";
                    _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: hello didn't include required field 'servername' - htsp incorrectly implemented by tvheadend");
                }

                if (helloResponse.containsField("serverversion"))
                {
                    _serverversion = helloResponse.getString("serverversion");
                }
                else
                {
                    _serverversion = "n/a";
                    _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: hello didn't include required field 'serverversion' - htsp incorrectly implemented by tvheadend");
                }

                _negotiatedProtocolVersion = Math.Min(_serverProtocolVersion, (int)HTSMessage.HTSP_CLIENT_VERSION);
                _serverCapabilities.Clear();
                if (helloResponse.containsField("servercapability"))
                {
                    try
                    {
                        foreach (object capability in helloResponse.getList("servercapability"))
                        {
                            var value = capability?.ToString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                _serverCapabilities.Add(value.Trim());
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Capabilities are optional diagnostics. A malformed field
                        // must not prevent an otherwise compatible server from
                        // authenticating and streaming.
                        _logger.LogDebug(ex, "[TVHclient] HTSConnectionAsync.authenticate: could not parse servercapability list");
                    }
                }

                _serverWebRoot = helloResponse.getString("webroot", string.Empty);

                byte[] salt = null;
                if (helloResponse.containsField("challenge"))
                {
                    salt = helloResponse.getByteArray("challenge");
                }
                else
                {
                    salt = new byte[0];
                    _logger.LogInformation("[TVHclient] HTSConnectionAsync.authenticate: hello didn't include required field 'challenge' - htsp incorrectly implemented by tvheadend");
                }

                byte[] digest = SHA1helper.GenerateSaltedSHA1(password, salt);
                HTSMessage authMessage = new HTSMessage();
                authMessage.Method = "authenticate";
                authMessage.putField("username", username);
                authMessage.putField("digest", digest);
                sendMessage(authMessage, loopBackResponseHandler);
                HTSMessage authResponse = GetResponse(loopBackResponseHandler, cancellationToken, responseTimeout);
                if (authResponse != null)
                {
                    Boolean auth = authResponse.getInt("noaccess", 0) != 1;
                    if (auth)
                    {
                        HTSMessage getDiskSpaceMessage = new HTSMessage();
                        getDiskSpaceMessage.Method = "getDiskSpace";
                        sendMessage(getDiskSpaceMessage, loopBackResponseHandler);
                        HTSMessage diskSpaceResponse = GetResponse(loopBackResponseHandler, cancellationToken, responseTimeout);
                        if (diskSpaceResponse != null)
                        {
                            long freeDiskSpace = -1;
                            long totalDiskSpace = -1;
                            if (diskSpaceResponse.containsField("freediskspace"))
                            {
                                freeDiskSpace = diskSpaceResponse.getLong("freediskspace") / BytesPerGiga;
                            }
                            else
                            {
                                _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: getDiskSpace didn't include required field 'freediskspace' - htsp incorrectly implemented by tvheadend");
                            }
                            if (diskSpaceResponse.containsField("totaldiskspace"))
                            {
                                totalDiskSpace = diskSpaceResponse.getLong("totaldiskspace") / BytesPerGiga;
                            }
                             else
                            {
                                _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: getDiskSpace didn't include required field 'totaldiskspace' - htsp incorrectly implemented by tvheadend");
                            }

                            _diskSpace = freeDiskSpace  + "GB / "  + totalDiskSpace + "GB";
                        }

                        if (enableAsyncMetadata)
                        {
                            HTSMessage enableAsyncMetadataMessage = new HTSMessage();
                            enableAsyncMetadataMessage.Method = "enableAsyncMetadata";
                            sendMessage(enableAsyncMetadataMessage, null);
                        }
                    }

                    _logger.LogDebug("[TVHclient] HTSConnectionAsync.authenticate: authenticated = {m}", auth);
                    return auth;
                }
            }
            _logger.LogError("[TVHclient] HTSConnectionAsync.authenticate: no hello response");
            return false;
        }

        private static HTSMessage GetResponse(LoopBackResponseHandler responseHandler, CancellationToken cancellationToken, TimeSpan responseTimeout)
        {
            return responseTimeout <= TimeSpan.Zero
                ? responseHandler.getResponse()
                : responseHandler.getResponse(cancellationToken, responseTimeout);
        }

        public int getServerProtocolVersion()
        {
            return _serverProtocolVersion;
        }

        public int getNegotiatedProtocolVersion()
        {
            return _negotiatedProtocolVersion;
        }

        public IReadOnlyCollection<string> getServerCapabilities()
        {
            return new List<string>(_serverCapabilities).AsReadOnly();
        }

        public bool hasServerCapability(string capability)
        {
            return !string.IsNullOrWhiteSpace(capability) && _serverCapabilities.Contains(capability);
        }

        public string getServerWebRoot()
        {
            return _serverWebRoot ?? string.Empty;
        }

        public string getServername()
        {
            return _servername;
        }

        public string getServerversion()
        {
            return _serverversion;
        }

        public string getDiskspace()
        {
            return _diskSpace;
        }

        public void sendMessage(HTSMessage message, HTSResponseHandler responseHandler)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            int seq = unchecked(Interlocked.Increment(ref _seq));
            HTSResponseHandler handler = responseHandler ?? NoOpResponseHandler;

            // Register the handler before queueing the message so a very fast response
            // cannot arrive before the dispatcher knows about its sequence number.
            message.putField("seq", seq);
            _responseHandlers[seq] = handler;
            _messagesForSendQueue.Enqueue(message);
        }

        private void SendingHandler()
        {
            CancellationToken cancellationToken = _sendingHandlerThreadTokenSource.Token;

            while (_connected && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!_messagesForSendQueue.TryDequeue(out HTSMessage message, cancellationToken, QueuePollInterval))
                    {
                        continue;
                    }

                    if (message == null)
                    {
                        continue;
                    }

                    byte[] data2send = message.BuildBytes();
                    SendAll(data2send, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || !_connected)
                {
                    return;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested || !_connected)
                {
                    return;
                }
                catch (Exception ex)
                {
                    HandleConnectionError(ex, nameof(SendingHandler));
                    return;
                }
            }
        }

        private void ReceiveHandler()
        {
            CancellationToken cancellationToken = _receiveHandlerThreadTokenSource.Token;
            byte[] readBuffer = new byte[SocketReceiveBufferSize];

            while (_connected && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Socket socket = _socket ?? throw new IOException("HTSP socket is not connected.");
                    int bytesReceived = socket.Receive(readBuffer);
                    if (bytesReceived == 0)
                    {
                        throw new IOException("Tvheadend closed the HTSP socket.");
                    }

                    _buffer.appendCount(readBuffer, bytesReceived);
                }
                catch (SocketException ex) when (IsSocketTimeout(ex) && _connected && !cancellationToken.IsCancellationRequested)
                {
                    // Bounded receive timeout: wake periodically so cancellation and
                    // connection state changes are observed promptly.
                    continue;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || !_connected)
                {
                    return;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested || !_connected)
                {
                    return;
                }
                catch (Exception ex)
                {
                    HandleConnectionError(ex, nameof(ReceiveHandler));
                    return;
                }
            }
        }

        private void MessageBuilder()
        {
            CancellationToken cancellationToken = _messageBuilderThreadTokenSource.Token;

            while (_connected && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!_buffer.TryGetFromStart(4, out byte[] lengthInformation, cancellationToken, QueuePollInterval))
                    {
                        continue;
                    }

                    long messageDataLength = HTSMessage.uIntToLong(lengthInformation[0], lengthInformation[1], lengthInformation[2], lengthInformation[3]);
                    if (messageDataLength < 0 || messageDataLength > MaxHtsMessageLength)
                    {
                        throw new InvalidDataException($"Invalid HTSP message length: {messageDataLength} bytes.");
                    }

                    long frameLength = messageDataLength + 4;
                    if (frameLength > int.MaxValue)
                    {
                        throw new InvalidDataException($"HTSP message frame is too large: {frameLength} bytes.");
                    }

                    if (!_buffer.TryExtractFromStart((int)frameLength, out byte[] messageData, cancellationToken, QueuePollInterval))
                    {
                        continue;
                    }

                    HTSMessage response = HTSMessage.parse(messageData, _loggerFactory.CreateLogger<HTSMessage>());
                    if (response == null)
                    {
                        _logger.LogWarning("[TVHclient] HTSConnectionAsync.MessageBuilder: dropping invalid HTSP message frame");
                        continue;
                    }

                    _receivedMessagesQueue.Enqueue(response);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    HandleConnectionError(ex, nameof(MessageBuilder));
                    return;
                }
            }
        }

        private void MessageDistributor()
        {
            CancellationToken cancellationToken = _messageDistributorThreadTokenSource.Token;

            while (_connected && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!_receivedMessagesQueue.TryDequeue(out HTSMessage response, cancellationToken, QueuePollInterval))
                    {
                        continue;
                    }

                    if (response == null)
                    {
                        continue;
                    }

                    if (response.containsField("seq"))
                    {
                        int seqNo = response.getInt("seq");
                        if (_responseHandlers.TryRemove(seqNo, out HTSResponseHandler currHTSResponseHandler))
                        {
                            if (!ReferenceEquals(currHTSResponseHandler, NoOpResponseHandler))
                            {
                                currHTSResponseHandler.handleResponse(response);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("[TVHclient] HTSConnectionAsync.MessageDistributor: HTSResponseHandler for seq = '{seq}' not found", seqNo);
                        }
                    }
                    else
                    {
                        // auto update messages
                        if (_listener != null)
                        {
                            _listener.onMessage(response);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    HandleConnectionError(ex, nameof(MessageDistributor));
                    return;
                }
            }
        }

        private void SendAll(byte[] data, CancellationToken cancellationToken)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            int offset = 0;
            while (offset < data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    Socket socket = _socket ?? throw new IOException("HTSP socket is not connected.");
                    int bytesSent = socket.Send(data, offset, data.Length - offset, SocketFlags.None);
                    if (bytesSent <= 0)
                    {
                        throw new IOException("HTSP socket closed while sending data.");
                    }

                    offset += bytesSent;
                }
                catch (SocketException ex) when (IsSocketTimeout(ex) && _connected && !cancellationToken.IsCancellationRequested)
                {
                    // Bounded send timeout: retry so partial sends are completed while
                    // still allowing cancellation to interrupt a stalled socket.
                    continue;
                }
            }
        }

        private void HandleConnectionError(Exception ex, string source)
        {
            bool expectedClose = IsExpectedCloseInProgress() && IsExpectedCloseException(ex);

            _connected = false;
            if (!expectedClose)
            {
                _needsRestart = true;
            }

            CloseSocket();

            if (expectedClose)
            {
                _logger.LogDebug(ex, "[TVHclient] HTSConnectionAsync.{source}: expected HTSP socket close during shutdown", source);
                return;
            }

            _logger.LogError(ex, "[TVHclient] HTSConnectionAsync.{source}: exception caught", source);
            if (_listener != null)
            {
                _listener.onError(ex);
            }
            else
            {
                _logger.LogError(ex, "[TVHclient] HTSConnectionAsync.{source}: exception caught, but no error listener is configured", source);
            }
        }

        private void CloseSocket()
        {
            Socket socket = _socket;
            if (socket == null)
            {
                return;
            }

            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {

            }

            try
            {
                socket.Close();
            }
            catch
            {

            }

            _socket = null;
        }

        private void ResetCancellationTokenSources()
        {
            _receiveHandlerThreadTokenSource?.Dispose();
            _messageBuilderThreadTokenSource?.Dispose();
            _sendingHandlerThreadTokenSource?.Dispose();
            _messageDistributorThreadTokenSource?.Dispose();

            _receiveHandlerThreadTokenSource = new CancellationTokenSource();
            _messageBuilderThreadTokenSource = new CancellationTokenSource();
            _sendingHandlerThreadTokenSource = new CancellationTokenSource();
            _messageDistributorThreadTokenSource = new CancellationTokenSource();
        }

        private bool IsExpectedCloseInProgress()
        {
            return _expectedClose
                   || !_connected
                   || (_receiveHandlerThreadTokenSource?.IsCancellationRequested ?? false)
                   || (_messageBuilderThreadTokenSource?.IsCancellationRequested ?? false)
                   || (_sendingHandlerThreadTokenSource?.IsCancellationRequested ?? false)
                   || (_messageDistributorThreadTokenSource?.IsCancellationRequested ?? false);
        }

        private static bool IsExpectedCloseException(Exception ex)
        {
            return ex is IOException
                   || ex is ObjectDisposedException
                   || ex is SocketException;
        }

        private static bool IsSocketTimeout(SocketException ex)
        {
            return ex.SocketErrorCode == SocketError.TimedOut
                   || ex.SocketErrorCode == SocketError.WouldBlock
                   || ex.SocketErrorCode == SocketError.TryAgain
                   || ex.SocketErrorCode == SocketError.Interrupted;
        }

        private sealed class NoOpHTSResponseHandler : HTSResponseHandler
        {
            public void handleResponse(HTSMessage response)
            {
            }
        }
    }
}
