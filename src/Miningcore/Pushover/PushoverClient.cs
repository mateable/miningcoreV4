using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Rest;

namespace Miningcore.Pushover;

public class PushoverClient(ClusterConfig clusterConfig, IHttpClientFactory httpClientFactory)
{
    private readonly SimpleRestClient client = new(httpClientFactory, PushoverConstants.ApiBaseUrl);
    private readonly PushoverConfig config = clusterConfig?.Notifications?.Pushover;

    public async Task<PushoverReponse> PushMessage(string title, string message,
        PushoverMessagePriority priority, CancellationToken ct)
    {
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(title));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(message));

        var msg = new PushoverRequest
        {
            User = config.User,
            Token = config.Token,
            Title = title,
            Message = message,
            Priority = (int) priority,
            Timestamp = (int) DateTimeOffset.Now.ToUnixTimeSeconds(),
        };

        return await client.Post<PushoverReponse>("/messages.json", msg, ct);
    }
}
