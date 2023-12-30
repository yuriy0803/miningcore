using System.Text;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;

namespace Miningcore.Blockchain.Kaspa.Custom.Pyrin;

public class PyrinJob : KaspaJob
{
    public PyrinJob(long blockHeight)
    {
        if(blockHeight >= PyrinConstants.Blake3ForkHeight)
        {
            string coinbaseBlockHash = KaspaConstants.CoinbaseBlockHash;
            byte[] hashBytes = Encoding.UTF8.GetBytes(coinbaseBlockHash.PadRight(32, '\0')).Take(32).ToArray();
            this.blockHeaderHasher = new Blake3(hashBytes);
            this.coinbaseHasher = new Blake3();
            this.shareHasher = new Blake3();
        }
    } 
}