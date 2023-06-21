using Miningcore.Extensions;
using Org.BouncyCastle.Math;

namespace Miningcore.Blockchain.Progpow;

public static class ProgpowUtils
{
    public static string FiroEncodeTarget(double difficulty)
    {
        string result;
        var diff = BigInteger.ValueOf((long) (difficulty * 255d));
        var quotient = FiroConstants.Diff1B.Divide(diff).Multiply(BigInteger.ValueOf(255));
        var bytes = quotient.ToByteArray().AsSpan();
        Span<byte> padded = stackalloc byte[FiroConstants.TargetPaddingLength];

        var padLength = FiroConstants.TargetPaddingLength - bytes.Length;

        if(padLength > 0)
        {
            bytes.CopyTo(padded.Slice(padLength, bytes.Length));
            result = padded.ToHexString(0, FiroConstants.TargetPaddingLength);
        }

        else
            result = bytes.ToHexString(0, FiroConstants.TargetPaddingLength);

        return result;
    }
    
    public static string RavencoinEncodeTarget(double difficulty)
    {
        string result;
        var diff = BigInteger.ValueOf((long) (difficulty * 255d));
        var quotient = RavencoinConstants.Diff1B.Divide(diff).Multiply(BigInteger.ValueOf(255));
        var bytes = quotient.ToByteArray().AsSpan();
        Span<byte> padded = stackalloc byte[RavencoinConstants.TargetPaddingLength];

        var padLength = RavencoinConstants.TargetPaddingLength - bytes.Length;

        if(padLength > 0)
        {
            bytes.CopyTo(padded.Slice(padLength, bytes.Length));
            result = padded.ToHexString(0, RavencoinConstants.TargetPaddingLength);
        }

        else
            result = bytes.ToHexString(0, RavencoinConstants.TargetPaddingLength);

        return result;
    }
}