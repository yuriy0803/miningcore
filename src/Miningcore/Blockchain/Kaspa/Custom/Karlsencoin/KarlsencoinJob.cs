using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;

namespace Miningcore.Blockchain.Kaspa.Custom.Karlsencoin;

public class KarlsencoinJob : KaspaJob
{
    public KarlsencoinJob()
    {
        this.coinbaseHasher = new Blake3();
    }
}