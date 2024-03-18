using System.Text;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;

namespace Miningcore.Blockchain.Kaspa.Custom.KaspaClassic;

public class KaspaClassicJob : KaspaJob
{
    public KaspaClassicJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher) : base(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher)
    {
    } 
}