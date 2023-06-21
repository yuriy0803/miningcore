using System.Globalization;
using System.Numerics;

namespace Miningcore.Blockchain.Progpow;

public class RavencoinConstants
{
    public const int EpochLength = 7500;
    public static readonly Org.BouncyCastle.Math.BigInteger Diff1B = new Org.BouncyCastle.Math.BigInteger("00ff000000000000000000000000000000000000000000000000000000", 16);
    public static readonly BigInteger Diff1 = BigInteger.Parse("00ff000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
    public const int TargetPaddingLength = 32;
    public const int ExtranoncePlaceHolderLength = 2;
}

public class FiroConstants
{
    public const int EpochLength = 1300;
    public static readonly Org.BouncyCastle.Math.BigInteger Diff1B = new Org.BouncyCastle.Math.BigInteger("00ffff0000000000000000000000000000000000000000000000000000", 16);
    public static readonly BigInteger Diff1 = BigInteger.Parse("00ffff0000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
    public const int TargetPaddingLength = 32;
    public const int ExtranoncePlaceHolderLength = 2;
}