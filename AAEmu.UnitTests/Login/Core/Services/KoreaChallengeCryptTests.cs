using AAEmu.Login.Core.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Login.Core.Services;

/// <summary>
/// Korea V2 challenge-response crypto (sha256_crypt, Drepper's <c>$5$</c>
/// scheme) — the credential primitives behind <c>korea_challenge_hash</c>.
///
/// <para>
/// This file replaces <c>KoreaSeedDataGeneratorTests.GenerateKoreaTestAccount</c>,
/// whose own header said "Not a correctness test — run manually": it printed a
/// seed INSERT to stdout and asserted nothing. It passed with
/// <c>KoreaChallengeCrypt.Compute</c> returning a constant string. Seed rows
/// are reproducible on demand and belong in a script, not in the suite, so the
/// printer is replaced by the contract that actually matters: the hash this
/// implementation produces is the hash the protocol specifies.
/// </para>
///
/// <para>
/// Named for <see cref="KoreaChallengeCrypt"/> — the type actually under test.
/// The protocol as a whole is served by <see cref="KoreanChallengeVerifier"/>
/// (the AES response half) and <see cref="Sha256Crypt"/> (the primitive
/// underneath); this file covers the hashing half.
/// </para>
/// </summary>
public class KoreaChallengeCryptTests
{
    /// <summary>Minimum legal stretch count for sha256_crypt.</summary>
    private const int MinRounds = 1000;

    /// <summary>
    /// Canonical Drepper SHA-crypt known-answer vectors (reference:
    /// akkadia.org/drepper/SHA-crypt.txt). The 10000-round entry is published
    /// verbatim in that specification:
    ///
    ///   pw "Hello world!", salt "saltstringsaltst", rounds 10000
    ///   => $5$rounds=10000$saltstringsaltst$3xv.VbSHBb41AL9AvLeujZkZRBAwqFMz2.opqey6IcA
    ///
    /// Independently re-derived here with libxcrypt (Python's <c>crypt</c>
    /// module — a completely separate implementation from this repo's
    /// hand-written <see cref="Sha256Crypt"/>), which returns exactly the same
    /// string. The vector is NOT copied from this repo's own output, so a bug in
    /// Sha256Crypt cannot make it pass.
    /// </summary>
    private const string DrepperPassword = "Hello world!";
    private const string DrepperSalt = "saltstringsaltst";
    private const int DrepperRounds = 10_000;
    private const string DrepperExpected =
        "$5$rounds=10000$saltstringsaltst$3xv.VbSHBb41AL9AvLeujZkZRBAwqFMz2.opqey6IcA";

    private readonly PasswordService _passwordService;

    public KoreaChallengeCryptTests()
    {
        var options = Options.Create(new PasswordHasherOptions());
        _passwordService = new PasswordService(new PasswordHasher<string>(options));
    }

    [Test]
    public async Task ComputeRaw_MatchesCanonicalDrepperVector()
    {
        // THE known-answer test. A fixed salt is injected so the output is
        // deterministic; KoreaChallengeCrypt.Compute cannot take one (it always
        // generates a fresh salt), so the vector is driven through the same
        // public primitives it calls: ComputeRaw then FormatEncoded.
        Span<byte> raw = stackalloc byte[32];
        Sha256Crypt.ComputeRaw(DrepperPassword, DrepperSalt, DrepperRounds, raw);

        var encoded = Sha256Crypt.FormatEncoded(raw, DrepperSalt, DrepperRounds);

        await Assert.That(encoded).IsEqualTo(DrepperExpected)
            .Because("sha256_crypt output must match the published Drepper specification vector");
    }

    [Test]
    public async Task ComputeRaw_ChangesTheKeyWithTheRoundsCount()
    {
        // The stretch count is not decorative: the client derives its AES-256
        // key from this hash, so a rounds mix-up desynchronises key material
        // and locks out every Korea account at verification time.
        Span<byte> low = stackalloc byte[32];
        Span<byte> high = stackalloc byte[32];
        Sha256Crypt.ComputeRaw(DrepperPassword, DrepperSalt, MinRounds, low);
        Sha256Crypt.ComputeRaw(DrepperPassword, DrepperSalt, DrepperRounds, high);

        await Assert.That(high.ToArray()).IsNotEquivalentTo(low.ToArray())
            .Because("a different round count must produce different key material");
    }

    [Test]
    public async Task ComputeRaw_ChangesTheKeyWithTheSalt()
    {
        // Per-account random salts are the stated anti-rainbow-table property
        // (see Sha256Crypt remarks); a fixed salt would make identical
        // passwords produce identical stored hashes and identical AES keys.
        Span<byte> saltA = stackalloc byte[32];
        Span<byte> saltB = stackalloc byte[32];
        Sha256Crypt.ComputeRaw(DrepperPassword, "aaaaaaaaaaaaaaaa", MinRounds, saltA);
        Sha256Crypt.ComputeRaw(DrepperPassword, "bbbbbbbbbbbbbbbb", MinRounds, saltB);

        await Assert.That(saltB.ToArray()).IsNotEquivalentTo(saltA.ToArray())
            .Because("the salt must participate in the hash, not just be decoration in the envelope");
    }

    [Test]
    public async Task Compute_ReturnsWellFormedSha256CryptEnvelope()
    {
        var encoded = KoreaChallengeCrypt.Compute("KrTesting", MinRounds);

        // The stored column is parsed back by Sha256Crypt.Parse, so the
        // envelope shape IS the contract: $5$rounds=N$salt$hash.
        await Assert.That(encoded).StartsWith($"$5$rounds={MinRounds}$")
            .Because("the korea_challenge_hash column is parsed as a sha256_crypt envelope");
        var parts = encoded.Split('$');
        await Assert.That(parts.Length).IsEqualTo(5)
            .Because("sha256_crypt is exactly $5$rounds=N$salt$checksum");
        await Assert.That(parts[3]).IsNotEmpty()
            .Because("a hash without a salt is not re-verifiable");
        await Assert.That(parts[4].Length).IsEqualTo(43)
            .Because("the SHA-256 crypt checksum is 43 base64-ish characters");
    }

    [Test]
    public async Task Compute_UsesAFreshSaltPerCall()
    {
        var first = KoreaChallengeCrypt.Compute("KrTesting", MinRounds);
        var second = KoreaChallengeCrypt.Compute("KrTesting", MinRounds);

        await Assert.That(second).IsNotEqualTo(first)
            .Because("each account must get a unique salt; identical passwords must not collide in the DB");
    }

    [Test]
    public async Task Verify_AcceptsTheHashComputeProduced()
    {
        // The round-trip is the property the login path depends on: the server
        // stores Compute(...) at enrolment and later runs Verify(stored, pw).
        // A constant-returning Compute satisfies nothing here.
        var encoded = KoreaChallengeCrypt.Compute("KrTesting", MinRounds);

        await Assert.That(KoreaChallengeCrypt.Verify(encoded, "KrTesting")).IsTrue()
            .Because("a hash this implementation produced must verify against its own plaintext");
    }

    [Test]
    public async Task Verify_AcceptsTheCanonicalDrepperVector()
    {
        // Parse-then-Verify against the published vector: proves the stored
        // column produced by any spec-compliant client is readable here, which
        // is what an existing production database depends on.
        await Assert.That(KoreaChallengeCrypt.Verify(DrepperExpected, DrepperPassword)).IsTrue()
            .Because("a spec-compliant stored hash must verify, not just one we produced");
    }

    [Test]
    public async Task Verify_RejectsAWrongPassword()
    {
        // Without this, a Verify that returned true unconditionally would pass
        // the round-trip tests above.
        var encoded = KoreaChallengeCrypt.Compute("KrTesting", MinRounds);

        await Assert.That(KoreaChallengeCrypt.Verify(encoded, "WrongTesting")).IsFalse()
            .Because("challenge verification must fail closed on a wrong password");
    }

    [Test]
    public async Task Compute_And_PBKDF2ProduceDifferentStorableForms()
    {
        // The Korea row carries BOTH hashes: korea_challenge_hash (sha256_crypt)
        // and password (Identity PBKDF2). Swapping one for the other must be
        // detectable, or an enrolment writing the wrong column locks the
        // account out of one of the two paths.
        var koreaHash = KoreaChallengeCrypt.Compute("KrTesting", MinRounds);
        var pbkdf2 = _passwordService.HashForStorage(Password.FromPlaintext("KrTesting"));

        await Assert.That(koreaHash).IsNotEqualTo(pbkdf2)
            .Because("the two stored credential forms must not be interchangeable");
        await Assert.That(pbkdf2).IsNotNull();
    }
}