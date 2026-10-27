// Pearl Coin Family – Miningcore Integration
//
// Pearl is a btcd-based L1 (Proof-of-Useful-Work). The node "pearld" speaks
// standard btcd JSON-RPC (getblocktemplate / submitblock). The proof-of-useful-work
// happens between template fetch and block submission via the py-pearl-mining
// (pearl_mining) Rust/PyO3 module, mediated by the pearl-gateway Python code.
//
// Real flow (mirrors miner/pearl-gateway in pearl-research-labs/pearl):
//   1. getblocktemplate  -> BlockTemplate (incomplete header bytes + target + txns)
//   2. Send MiningJob (incomplete_header_bytes + target) to miners via Stratum
//   3. Miner returns a serialized PlainProof (matrix PoUW solution)
//   4. Bridge: generate_proof(incomplete_header, plain_proof) -> ZKProof
//   5. Bridge assembles PearlBlock:
//        ZKCertificate.serialize() | PearlHeader.serialize() | varint(tx_count) | txns
//   6. submitblock <block_hex>  (returns null on success, error string on reject)

using System.Numerics;

namespace Miningcore.Blockchain.Pearl;

public static class PearlConstants
{
    // -------------------------------------------------------------------------
    // Stratum method names (Bitcoin-compatible subset miners already speak)
    // -------------------------------------------------------------------------
    public const string StratumSubscribe     = "mining.subscribe";
    public const string StratumAuthorize     = "mining.authorize";
    public const string StratumNotify        = "mining.notify";
    public const string StratumSetDifficulty = "mining.set_difficulty";
    public const string StratumSubmit        = "mining.submit";

    // -------------------------------------------------------------------------
    // pearld btcd JSON-RPC methods (standard btcsuite/btcd surface)
    // -------------------------------------------------------------------------
    public const string RpcGetBlockchainInfo = "getblockchaininfo";
    public const string RpcGetBlockTemplate  = "getblocktemplate";
    public const string RpcGetPeerInfo       = "getpeerinfo";
    public const string RpcGetNetworkHashps  = "getnetworkhashps";
    public const string RpcSubmitBlock        = "submitblock";
    public const string RpcValidateAddress   = "validateaddress";
    public const string RpcGetBlock          = "getblock";

    // -------------------------------------------------------------------------
    // Block serialization layout (see pearl_gateway/blockchain_utils/pearl_block.py)
    //   ZKCertificate.serialize()
    //   | PearlHeader.serialize()   (IncompleteBlockHeader + 32-byte proof_commitment)
    //   | TX_COUNT  (varint)
    //   | TRANSACTIONS  (raw bytes, coinbase first)
    // The actual byte assembly is performed by the Python bridge to remain
    // identical to the upstream gateway implementation.
    // -------------------------------------------------------------------------
    public const int ProofCommitmentSize = 32;

    // -------------------------------------------------------------------------
    // Difficulty / target arithmetic
    //   target is a 256-bit big-endian number; a solution is valid when its
    //   hash is below target. Stratum difficulty 1 corresponds to the
    //   Bitcoin-style diff-1 target (2^224 - 1) << 0 region; pearld returns
    //   the explicit target hex in getblocktemplate so we use that directly.
    // -------------------------------------------------------------------------
    public static readonly BigInteger MaxTarget =
        (BigInteger.One << 256) - BigInteger.One;

    // Bitcoin-style diff-1 target (0x00000000FFFF0000...0000), used to convert
    // between pool/share difficulty and a 256-bit target.
    public static readonly BigInteger Diff1Target =
        BigInteger.Parse("00000000FFFF0000000000000000000000000000000000000000000000000000",
            System.Globalization.NumberStyles.HexNumber);

    // -------------------------------------------------------------------------
    // Timing
    // -------------------------------------------------------------------------
    public const int TimeTolerance = 300;  // seconds

    // -------------------------------------------------------------------------
    // Misc
    // -------------------------------------------------------------------------
    public const string DaemonName   = "pearld";
    public const decimal SmallestUnit = 100_000_000m; // Grain per PEARL (btcd 1e8 base)

    public const int ExtraNoncePlaceholderLength = 8;
}

public static class PearlStratumErrors
{
    public const string InvalidProofData   = "Invalid proof data";
    public const string StaleJob           = "Stale job";
    public const string DuplicateShare     = "Duplicate share";
    public const string LowDifficulty      = "Low difficulty share";
    public const string ProofVerifyFailed  = "Proof verification failed";
    public const string ZkProofFailed      = "ZK proof generation failed";
    public const string BlockSubmitFailed  = "Block submission to node failed";
}
