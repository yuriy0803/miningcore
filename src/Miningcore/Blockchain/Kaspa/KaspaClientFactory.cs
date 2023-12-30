using System.Net.Http;
using Grpc.Core;
using Grpc.Net.Client;
using System.Net;
using System.Threading.Tasks;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using NLog;
using kaspaWalletd = Miningcore.Blockchain.Kaspa.KaspaWalletd;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa;

public static class KaspaClientFactory
{
    public static kaspad.KaspadRPC.KaspadRPCClient CreateKaspadRPCClient(IHttpClientFactory factory, DaemonEndpointConfig[] daemonEndpoints, string protobufDaemonRpcServiceName)
    {
        var daemonEndpoint = daemonEndpoints.First();

        var baseUrl = new UriBuilder(daemonEndpoint.Ssl || daemonEndpoint.Http2 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            daemonEndpoint.Host, daemonEndpoint.Port, daemonEndpoint.HttpPath);
        
        var channel = GrpcChannel.ForAddress(baseUrl.ToString(), new GrpcChannelOptions()
        {
            HttpClient = factory.CreateClient(),
            DisposeHttpClient = true
        });

        return new kaspad.KaspadRPC.KaspadRPCClient(new kaspad.KaspadRPC(protobufDaemonRpcServiceName), channel);
    }

        public static kaspaWalletd.KaspaWalletdRPC.KaspaWalletdRPCClient CreateKaspaWalletdRPCClient(IHttpClientFactory factory, DaemonEndpointConfig[] daemonEndpoints, string protobufWalletRpcServiceName)
    {
        var daemonEndpoint = daemonEndpoints.First();

        var baseUrl = new UriBuilder(daemonEndpoint.Ssl || daemonEndpoint.Http2 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            daemonEndpoint.Host, daemonEndpoint.Port, daemonEndpoint.HttpPath);
        
        var channel = GrpcChannel.ForAddress(baseUrl.ToString(), new GrpcChannelOptions()
        {
            HttpClient = factory.CreateClient(),
            DisposeHttpClient = true
        });

        return new kaspaWalletd.KaspaWalletdRPC.KaspaWalletdRPCClient(new kaspaWalletd.KaspaWalletdRPC(protobufWalletRpcServiceName), channel);
    }
}