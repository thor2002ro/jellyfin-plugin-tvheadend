using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace TVHeadEnd;

public sealed class NativeListingsProvider(NativeTunerHost host) : IListingsProvider
{
    public string Name => "TVHeadend";
    public string Type => NativeTunerHost.TunerType;
    private bool Includes(ListingsProviderInfo info, string channelId) => info == null || string.IsNullOrEmpty(info.ListingsId)
        || channelId.StartsWith(NativeTunerHost.Prefix(info.ListingsId), StringComparison.OrdinalIgnoreCase);
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(ListingsProviderInfo info, string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        => Includes(info, channelId) ? host.GetProgramsAsync(channelId, startDateUtc, endDateUtc, cancellationToken)
            : Task.FromResult<IEnumerable<ProgramInfo>>([]);
    public async Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
        => (await host.GetChannels(true, cancellationToken).ConfigureAwait(false)).Where(channel => Includes(info, channel.Id)).ToList();
    public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings) => Task.CompletedTask;
    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
        => Task.FromResult(host.GetGuideLineups());
}
