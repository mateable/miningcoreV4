#include <string.h>
#include "sha3/sph_whirlpool.h"
#include "whirlpool.h"

/*
 * Mirrors MateableCoin's own crypto/whirlpool/whirlpool.c byte-for-byte:
 * four chained passes of sph_whirlpool1 (the corrected/final NESSIE
 * whirlpool variant, not the older sph_whirlpool/whirlpool0 revisions).
 * sph_whirlpool.c itself (src/Native/libmultihash/sha3/sph_whirlpool.c in
 * this repo) was confirmed byte-identical to MateableCoin's own copy before
 * this wrapper was written -- only this chaining wrapper is new.
 */
void whirlpool_hash(const char* input, char* output, uint32_t len)
{
    unsigned char hash[64];
    memset(hash, 0, sizeof(hash));

    sph_whirlpool1_context ctx_whirlpool;

    sph_whirlpool1_init(&ctx_whirlpool);
    sph_whirlpool1(&ctx_whirlpool, input, len);
    sph_whirlpool1_close(&ctx_whirlpool, (void*)hash);

    sph_whirlpool1_init(&ctx_whirlpool);
    sph_whirlpool1(&ctx_whirlpool, (const void*)hash, 64);
    sph_whirlpool1_close(&ctx_whirlpool, (void*)hash);

    sph_whirlpool1_init(&ctx_whirlpool);
    sph_whirlpool1(&ctx_whirlpool, (const void*)hash, 64);
    sph_whirlpool1_close(&ctx_whirlpool, (void*)hash);

    sph_whirlpool1_init(&ctx_whirlpool);
    sph_whirlpool1(&ctx_whirlpool, (const void*)hash, 64);
    sph_whirlpool1_close(&ctx_whirlpool, (void*)hash);

    memcpy(output, hash, 32);
}
