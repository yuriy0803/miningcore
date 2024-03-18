using System.Data;
using Miningcore.Persistence.Model;

namespace Miningcore.Persistence.Repositories;

public interface IBlockRepository
{
    Task InsertAsync(IDbConnection con, IDbTransaction tx, Block block);
    Task DeleteBlockAsync(IDbConnection con, IDbTransaction tx, Block block);
    Task UpdateBlockAsync(IDbConnection con, IDbTransaction tx, Block block);

    Task<Block[]> PageBlocksAsync(IDbConnection con, string poolId, BlockStatus[] status, int page, int pageSize, CancellationToken ct);
    Task<Block[]> PageBlocksAsync(IDbConnection con, BlockStatus[] status, int page, int pageSize, CancellationToken ct);
    Task<Block[]> PageMinerBlocksAsync(IDbConnection con, string poolId, string address, BlockStatus[] status, int page, int pageSize, CancellationToken ct);
    Task<Block[]> GetPendingBlocksForPoolAsync(IDbConnection con, string poolId);
    Task<Block> GetBlockBeforeAsync(IDbConnection con, string poolId, BlockStatus[] status, DateTime before);
    Task<uint> GetBlockBeforeCountAsync(IDbConnection con, string poolId, BlockStatus[] status, DateTime before);
    Task<Block> GetMinerBlockBeforeAsync(IDbConnection con, string poolId, string miner, BlockStatus[] status, DateTime before);
    Task<uint> GetPoolBlockCountAsync(IDbConnection con, string poolId, CancellationToken ct);
    Task<uint> GetMinerBlockCountAsync(IDbConnection con, string poolId, string address, CancellationToken ct);
    Task<DateTime?> GetLastPoolBlockTimeAsync(IDbConnection con, string poolId);
    Task<DateTime?> GetLastMinerBlockTimeAsync(IDbConnection con, string poolId, string address);
    Task<Block> GetBlockByPoolHeightAndTypeAsync(IDbConnection con, string poolId, long height, string type);
    Task<uint> GetPoolDuplicateBlockCountByPoolHeightNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, BlockStatus[] status);
    Task<uint> GetPoolDuplicateBlockBeforeCountByPoolHeightNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, BlockStatus[] status, DateTime before);
    Task<uint> GetPoolDuplicateBlockAfterCountByPoolHeightNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, BlockStatus[] status, DateTime after);
}
