using AutoMapper;
using Miningcore.Api.Responses;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Alephium.Configuration;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Ergo.Configuration;
using Miningcore.Blockchain.Handshake.Configuration;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Blockchain.Warthog.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Newtonsoft.Json;
using System.Reflection;

namespace Miningcore.Api.Extensions;

public static class MiningPoolExtensions
{
    public static PoolInfo ToPoolInfo(this PoolConfig poolConfig, IMapper mapper, Persistence.Model.PoolStats stats, IMiningPool pool, bool proxied)
    {
        var poolInfo = mapper.Map<PoolInfo>(poolConfig);

        poolInfo.PoolStats = stats != null ? mapper.Map<PoolStats>(stats) : new PoolStats();
        poolInfo.NetworkStats = pool?.NetworkStats ?? (stats != null ? mapper.Map<BlockchainStats>(stats) : new BlockchainStats());

        // pool wallet link
        var addressInfobaseUrl = poolConfig.Template.ExplorerAccountLink;
        if(!string.IsNullOrEmpty(addressInfobaseUrl))
            poolInfo.AddressInfoLink = string.Format(addressInfobaseUrl, poolInfo.Address);

        // pool fees
        poolInfo.PoolFeePercent = poolConfig.RewardRecipients != null ? (float) poolConfig.RewardRecipients.Sum(x => x.Percentage) : 0;

        // strip security critical stuff
        if(poolInfo.PaymentProcessing.PayoutSchemeConfig != null)
        {
            var props = poolInfo.PaymentProcessing.PayoutSchemeConfig.GetType().GetProperties();

            foreach(var prop in props)
            {
                var attr = prop.GetCustomAttributes(typeof(JsonIgnoreAttribute), false).FirstOrDefault();

                if(attr != null)
                    prop.SetValue(poolInfo.PaymentProcessing.PayoutSchemeConfig, null);
            }
        }
        
        // Strip extra
        if(poolInfo.PaymentProcessing.Extra != null)
        {
            var extra = poolInfo.PaymentProcessing.Extra;
            
            switch(poolInfo.Coin.Family)
            {
                case "alephium":
                    extra.StripValue(nameof(AlephiumPaymentProcessingConfigExtra.WalletPassword));
                    break;
                case "bitcoin":
                    extra.StripValue(nameof(BitcoinPoolPaymentProcessingConfigExtra.WalletPassword));
                    break;
                case "ergo":
                    extra.StripValue(nameof(ErgoPaymentProcessingConfigExtra.WalletPassword));
                    break;
                case "handshake":
                    extra.StripValue(nameof(HandshakePoolPaymentProcessingConfigExtra.WalletPassword));
                    break;
                case "kaspa":
                    extra.StripValue(nameof(KaspaPaymentProcessingConfigExtra.WalletPassword));
                    break;
                case "warthog":
                    extra.StripValue(nameof(WarthogPaymentProcessingConfigExtra.WalletPrivateKey));
                    break;
            }
        }

        if(poolInfo.Ports != null)
        {
            var mappedPorts = new Dictionary<int, PoolEndpoint>();

            foreach(var port in poolInfo.Ports.Keys)
            {
                var portInfo = poolInfo.Ports[port];

                portInfo.TlsPfxFile = null;
                portInfo.TlsPfxPassword = null;

                // Port masking for BTC Stealth Proxy
                var mappedPort = port;
                
                if(proxied) 
                {
                    if(poolInfo.Id == "bitcoin-solo")
                    {
                        switch(mappedPort)
                        {
                            case 13102: mappedPort = 3102; break;
                            case 13112: mappedPort = 3112; break;
                            case 13122: mappedPort = 3122; break;
                            case 13132: mappedPort = 3132; break;
                        }
                    }
                    else if(poolInfo.Id == "bitcoincash-solo")
                    {
                        switch(mappedPort)
                        {
                            case 3103: mappedPort = 13103; break;
                            case 3113: mappedPort = 13113; break;
                            case 3123: mappedPort = 13123; break;
                        }
                    }
                }

                mappedPorts[mappedPort] = portInfo;
            }

            poolInfo.Ports = mappedPorts;
        }
        return poolInfo;
    }
}
