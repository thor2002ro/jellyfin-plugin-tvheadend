using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TVHeadEnd.HTSP;
using TVHeadEnd.HTSP_Responses;
using TVHeadEnd.Helper;

namespace TVHeadEnd;

public class AccessTicketHandler
{
    private readonly ILogger<AccessTicketHandler> _logger;

    private readonly HTSConnectionHandler _htsConnectionHandler;
    private readonly TicketType _ticketType;
    private readonly string _ticketItemType;
    private readonly TimeSpan _requestTimeout;
    private readonly int _requestRetries;
    private readonly TimeSpan _ticketLifeSpan;

    private volatile int _ticketIdSequence;

    public enum TicketType : byte { Channel, Recording };

    public record Ticket
    {
        public string Id { get; init; }
        public string Path { get; init; }
        public string TicketParam { get; init; }
        public string Url => $"{Path}{(Path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}ticket={Uri.EscapeDataString(TicketParam)}";
        public DateTime Expires { get; init; }
    }

    private readonly ConcurrentDictionary<string, Lazy<Task<Ticket>>> _ticketCache = new();

    internal AccessTicketHandler(
        ILoggerFactory loggerFactory, HTSConnectionHandler htsConnectionHandler,
        TimeSpan requestTimeout, int requestRetries, TimeSpan ticketLifeSpan, TicketType ticketType)
    {
        _logger = loggerFactory.CreateLogger<AccessTicketHandler>();
        _htsConnectionHandler = htsConnectionHandler;
        _requestTimeout = requestTimeout;
        _requestRetries = requestRetries;
        _ticketLifeSpan = ticketLifeSpan;
        _ticketType = ticketType;

        _ticketItemType = ticketType switch
        {
            TicketType.Channel => "channelId",
            TicketType.Recording => "dvrId",
            _ => throw new ArgumentException("undefined ticketType")
        };
    }

    public async Task<Ticket> GetTicket(string itemId, CancellationToken cancellationToken)
    {
        Ticket currentTicket = null;
        while (true)
        {
            var entry = _ticketCache.GetOrAdd(itemId, _ => new Lazy<Task<Ticket>>(
                () => GetTicketRecord(itemId, currentTicket),
                LazyThreadSafetyMode.ExecutionAndPublication));

            Ticket ticket;
            try
            {
                ticket = await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _ticketCache.TryRemove(new KeyValuePair<string, Lazy<Task<Ticket>>>(itemId, entry));
                throw;
            }

            if (ticket.Expires > DateTime.UtcNow)
            {
                return ticket; // non-expired ticket from cache
            }

            _logger.LogDebug("[TVHclient] AccessTicketHandler.GetAccessTicket: Cache expired for {ItemType}={ItemId}. Revalidating ticket (#{TicketId})", _ticketItemType, itemId, ticket.Id);
            currentTicket = ticket;
            _ticketCache.TryRemove(new KeyValuePair<string, Lazy<Task<Ticket>>>(itemId, entry));
        }
    }

    private async Task<Ticket> GetTicketRecord(string itemId, Ticket currentRecord)
    {
        var response = await RequestTicket(itemId).ConfigureAwait(false);
        var path = response.getString("path");
        var ticket = response.getString("ticket");

        var id = (currentRecord != null && path == currentRecord.Path && ticket == currentRecord.TicketParam)
            ? currentRecord.Id
            : $"{NextTicketId()}";

        if (id != currentRecord?.Id)
        {
            _logger.LogInformation("[TVHclient] AccessTicketHandler.GetAccessTicket: New ticket (#{TicketId}) created for {ItemType}={ItemId}", id, _ticketItemType, itemId);
        }

        return new Ticket
        {
            Id = id,
            Path = path,
            TicketParam = ticket,
            Expires = DateTime.UtcNow + _ticketLifeSpan,
        };
    }

    private async Task<HTSMessage> RequestTicket(string itemId)
    {
        var request = new HTSMessage { Method = "getTicket" };
        var numericId = _ticketType == TicketType.Channel
            ? _htsConnectionHandler.ResolveChannelId(itemId)
            : _htsConnectionHandler.ResolveDvrId(itemId);
        request.putField(_ticketItemType, numericId);

        for (int attempt = 1, lastAttempt = 1 + _requestRetries; attempt <= lastAttempt; attempt++)
        {
            try
            {
                var response = new LoopBackResponseHandler();
                int sequence = _htsConnectionHandler.SendMessage(request, response);
                try
                {
                    return await response.GetResponseAsync(CancellationToken.None, _requestTimeout * attempt).ConfigureAwait(false);
                }
                finally
                {
                    _htsConnectionHandler.RemoveResponseHandler(sequence);
                }
            }
            catch (TimeoutException)
            {
                // Retry with the longer timeout for the next attempt.
            }
        }

        _logger.LogError("[TVHclient] AccessTicketHandler.GetAccessTicket: can't obtain playback authentication ticket from TVH because the timeout was reached");

        throw new TimeoutException("Obtaining playback authentication ticket from TVH caused a network timeout");
    }

    private int NextTicketId()
    {
        int id;
        while ((id = Interlocked.Increment(ref _ticketIdSequence)) < 0)
        {
            _ticketIdSequence = Math.Max(0, _ticketIdSequence);
        }

        return id;
    }
}
