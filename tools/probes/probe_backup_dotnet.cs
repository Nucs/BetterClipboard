#:package BouncyCastle.Cryptography@2.7.0
#:package SharpCompress@1.0.0
#:package ZstdSharp.Port@0.8.8
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Probe for the cloud backup's design (docs/website-src/guides/cloud-backup.md, CLAUDE.md section 2.68): the .NET side.
//
// 1. Keys: can a password deterministically become a post-quantum key pair on this PC, and do two independent ML-KEM
//    implementations (Windows CNG through .NET 10, and BouncyCastle, for Windows versions without CNG's ML-KEM) derive
//    the same key from the same seed and open each other's encapsulations? What do Argon2id, ML-KEM-1024, X25519 and
//    AES-256-GCM cost here?
// 2. Compressors (optional): can a managed library reproduce probe_backup_compression.py's winners? Compresses that
//    probe's dumped streams (--dump DIR: every *.bin file there) with SharpCompress's PPMd (variant I) and LZMA
//    encoders and ZstdSharp's zstd (the port BetterClipboard already ships), and checks every round trip.
//
// PRIVACY: only BC-TEST inputs and the public streams of the compression probe; prints facts, sizes and timings.
// Run:     dotnet run -c Release tools/probes/probe_backup_dotnet.cs [-- <dump folder>]
// Verified 2026-11-21 on Windows 11 26200, .NET 10.0.1 (see CLAUDE.md section 2.68 for the numbers).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.PPMd;
using ZstdSharp;
using ZstdSharp.Unsafe;

return BackupProbe.Run(args);

/// <summary>
/// The probe's checks, in the order the report prints them.
/// </summary>
internal static class BackupProbe
{
    /// <summary>The HKDF label of the design's key derivation; a change would rebuild every user's keys differently.</summary>
    private static readonly byte[] SeedLabel = Encoding.ASCII.GetBytes("BetterClipboard backup v1 key seeds");

    /// <summary>
    /// Runs every check and prints the report.
    /// </summary>
    /// <param name="args">Optional: the folder probe_backup_compression.py --dump wrote (its <c>*.bin</c> streams);
    /// without it the compressor section is skipped.</param>
    /// <returns>0 when every check held; 1 when a key derivation, an interop check or a round trip failed, or the dump
    /// folder held no stream.</returns>
    /// <exception cref="DirectoryNotFoundException">The dump folder does not exist (the probe ends with the
    /// exception's message: a mistyped folder is the caller's to fix, not a failed check).</exception>
    public static int Run(string[] args)
    {
        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
                          $"OS {Environment.OSVersion.VersionString}, {Environment.ProcessorCount} logical processors");
        Console.WriteLine($"MLKem.IsSupported (Windows CNG): {MLKem.IsSupported}; SHA3_256: {SHA3_256.IsSupported}; " +
                          $"AesGcm: {AesGcm.IsSupported}");
        bool ok = CheckKeys();
        if (args.Length > 0)
        {
            ok &= CheckCompressors(args[0]);
        }

        Console.WriteLine(ok ? "All checks held." : "A CHECK FAILED.");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// The key half: Argon2id costs, the derivation, its determinism, CNG against BouncyCastle, and the per-step costs.
    /// </summary>
    /// <returns>True when the derivation is deterministic and both implementations agree both ways.</returns>
    private static bool CheckKeys()
    {
        byte[] salt = Enumerable.Range(0xA0, 32).Select(i => (byte)i).ToArray(); // Fixed: runs compare.
        byte[] password = Encoding.UTF8.GetBytes("BC-TEST correct horse battery staple".Normalize(NormalizationForm.FormC));

        foreach (var (mib, passes, lanes) in new[] { (64, 3, 4), (256, 3, 4) })
        {
            foreach (bool parallel in new[] { false, true })
            {
                var watch = Stopwatch.StartNew();
                Argon2id(password, salt, mib, passes, lanes, parallel);
                Console.WriteLine($"Argon2id {mib} MiB, {passes} passes, {lanes} lanes, {(parallel ? "parallel" : "serial  ")}: " +
                                  $"{watch.ElapsedMilliseconds,6} ms (BouncyCastle)");
            }
        }

        byte[] seeds = DeriveSeeds(password, salt);
        var bcPrivate = MLKemPrivateKeyParameters.FromSeed(MLKemParameters.ml_kem_1024, seeds[..64]);
        byte[] bcPublic = bcPrivate.GetPublicKeyEncoded();
        byte[] again = MLKemPrivateKeyParameters.FromSeed(MLKemParameters.ml_kem_1024, DeriveSeeds(password, salt)[..64])
            .GetPublicKeyEncoded();
        bool deterministic = bcPublic.AsSpan().SequenceEqual(again);
        Console.WriteLine($"ML-KEM-1024 encapsulation key {bcPublic.Length} bytes; derived twice, the same key: {deterministic}");

        bool agree = true;
        if (MLKem.IsSupported)
        {
            using MLKem cng = MLKem.ImportPrivateSeed(MLKemAlgorithm.MLKem1024, seeds[..64]);
            bool sameKey = cng.ExportEncapsulationKey().AsSpan().SequenceEqual(bcPublic);

            // CNG encapsulates to BouncyCastle's public key; BouncyCastle decapsulates.
            using MLKem cngPublic = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem1024, bcPublic);
            cngPublic.Encapsulate(out byte[] ciphertext1, out byte[] secret1);
            var decapsulator = new MLKemDecapsulator(MLKemParameters.ml_kem_1024);
            decapsulator.Init(bcPrivate);
            byte[] secret1b = new byte[decapsulator.SecretLength];
            decapsulator.Decapsulate(ciphertext1, secret1b);

            // BouncyCastle encapsulates; CNG decapsulates.
            var encapsulator = NewEncapsulator(bcPublic);
            byte[] ciphertext2 = new byte[encapsulator.EncapsulationLength], secret2 = new byte[encapsulator.SecretLength];
            encapsulator.Encapsulate(ciphertext2, secret2);
            bool oneWay = secret1.AsSpan().SequenceEqual(secret1b), otherWay = secret2.AsSpan().SequenceEqual(cng.Decapsulate(ciphertext2));
            Console.WriteLine($"CNG and BouncyCastle from one seed: same key {sameKey}; CNG→BouncyCastle {oneWay}; " +
                              $"BouncyCastle→CNG {otherWay} (ciphertext {ciphertext1.Length} bytes)");
            agree = sameKey && oneWay && otherWay;
        }

        var x25519 = new X25519PrivateKeyParameters(seeds[64..]);
        byte[] xPublic = x25519.GeneratePublicKey().GetEncoded();
        Time("ML-KEM-1024 key pair from a seed (BouncyCastle)", () =>
            MLKemPrivateKeyParameters.FromSeed(MLKemParameters.ml_kem_1024, seeds[..64]).GetPublicKeyEncoded());
        var timedEncapsulator = NewEncapsulator(bcPublic);
        byte[] ct = new byte[timedEncapsulator.EncapsulationLength], ss = new byte[timedEncapsulator.SecretLength];
        Time("ML-KEM-1024 encapsulation (BouncyCastle)", () => timedEncapsulator.Encapsulate(ct, ss));
        var timedDecapsulator = new MLKemDecapsulator(MLKemParameters.ml_kem_1024);
        timedDecapsulator.Init(bcPrivate);
        byte[] ssd = new byte[timedDecapsulator.SecretLength];
        Time("ML-KEM-1024 decapsulation (BouncyCastle)", () => timedDecapsulator.Decapsulate(ct, ssd));
        Time("X25519 agreement with a fresh ephemeral key (BouncyCastle)", () =>
        {
            byte[] secret = new byte[32];
            new X25519PrivateKeyParameters(new SecureRandom()).GenerateSecret(new X25519PublicKeyParameters(xPublic), secret);
        });

        byte[] chunk = RandomNumberGenerator.GetBytes(1 << 20), output = new byte[1 << 20], tag = new byte[16], nonce = new byte[12];
        using (var gcm = new AesGcm(RandomNumberGenerator.GetBytes(32), 16))
        {
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 256; i++)
            {
                BitConverter.TryWriteBytes(nonce, i);
                gcm.Encrypt(nonce, chunk, output, tag);
            }

            Console.WriteLine($"AES-256-GCM: {256 / watch.Elapsed.TotalSeconds:0} MiB/s (1 MiB chunks)");
        }

        return deterministic && agree;
    }

    /// <summary>
    /// The design's derivation: Argon2id (256 MiB, 3 passes, 4 lanes) to 32 bytes, then HKDF-SHA-512 to 96 bytes:
    /// the 64-byte ML-KEM seed (d and z of FIPS 203) and the 32-byte X25519 private key.
    /// </summary>
    /// <param name="password">The password as UTF-8 bytes of its NFC form.</param>
    /// <param name="salt">The key's 32-byte salt.</param>
    /// <returns>96 bytes of key seeds.</returns>
    private static byte[] DeriveSeeds(byte[] password, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, Argon2id(password, salt, 256, 3, 4, parallel: true), 96, salt: null, info: SeedLabel);

    /// <summary>
    /// Argon2id version 1.3 (RFC 9106) with BouncyCastle.
    /// </summary>
    /// <param name="password">The password bytes.</param>
    /// <param name="salt">The salt.</param>
    /// <param name="mib">Memory in MiB.</param>
    /// <param name="passes">Passes over the memory.</param>
    /// <param name="lanes">Lanes (parallelism).</param>
    /// <param name="parallel">Compute the lanes on several threads (BouncyCastle's TaskFactory constructor) or on one.</param>
    /// <returns>32 bytes.</returns>
    private static byte[] Argon2id(byte[] password, byte[] salt, int mib, int passes, int lanes, bool parallel)
    {
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13).WithSalt(salt).WithMemoryAsKB(mib * 1024)
            .WithIterations(passes).WithParallelism(lanes).Build();
        var generator = parallel ? new Argon2BytesGenerator(Task.Factory) : new Argon2BytesGenerator();
        generator.Init(parameters);
        byte[] output = new byte[32];
        generator.GenerateBytes(password, output);
        return output;
    }

    /// <summary>
    /// A BouncyCastle ML-KEM-1024 encapsulator for a public key.
    /// </summary>
    /// <param name="publicKey">The encapsulation key's 1,568 bytes.</param>
    /// <returns>The initialized encapsulator (fresh randomness for each encapsulation).</returns>
    private static MLKemEncapsulator NewEncapsulator(byte[] publicKey)
    {
        var encapsulator = new MLKemEncapsulator(MLKemParameters.ml_kem_1024);
        encapsulator.Init(new ParametersWithRandom(MLKemPublicKeyParameters.FromEncoding(MLKemParameters.ml_kem_1024, publicKey),
            new SecureRandom()));
        return encapsulator;
    }

    /// <summary>
    /// Prints an action's average time over 200 runs, after one warm-up run.
    /// </summary>
    /// <param name="what">The report's label.</param>
    /// <param name="action">The action.</param>
    private static void Time(string what, Action action)
    {
        action();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
        {
            action();
        }

        Console.WriteLine($"{what}: {watch.Elapsed.TotalMilliseconds / 200:0.000} ms");
    }

    /// <summary>
    /// The compressor half: SharpCompress's PPMd and LZMA on every stream the compression probe dumped.
    /// </summary>
    /// <param name="folder">The dump folder: each <c>*.bin</c> file in it is measured, in name order.</param>
    /// <returns>True when every round trip gave its input back (and the folder held at least one stream).</returns>
    /// <remarks>SharpCompress 1.0.0's PPMd (variant I) throws IndexOutOfRangeException at model orders 24 and 32 (found
    /// by this probe), so orders 8 and 16 are measured; its LZMA2 encoder is not implemented (NotImplementedException),
    /// so plain LZMA is. A stream is held in memory whole, so the folder's largest file decides the memory needed (the
    /// picture streams are about 200 to 250 MB).</remarks>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    private static bool CheckCompressors(string folder)
    {
        // Name order, so two runs print their streams in the same order; the probe's own names sort text first.
        string[] streams = Directory.GetFiles(folder, "*.bin").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        if (streams.Length == 0)
        {
            Console.WriteLine($"No *.bin streams in {folder}: run probe_backup_compression.py --dump {folder} first.");
            return false;
        }

        bool ok = true;
        foreach (string path in streams)
        {
            byte[] input = File.ReadAllBytes(path);
            Console.WriteLine($"{Path.GetFileName(path)}: {input.Length / 1e6:0.00} MB");
            foreach (var (order, memory) in new[] { (8, 256), (16, 64), (16, 256) })
            {
                ok &= Measure($"  PPMd order {order}, {memory} MB (SharpCompress)", input,
                    data => PpmdCompress(data, order, memory), PpmdDecompress);
            }

            ok &= Measure("  LZMA, 64 MiB dictionary, 273 fast bytes (SharpCompress)", input, LzmaCompress, LzmaDecompress);
            foreach (int workers in new[] { 0, 4 })
            {
                ok &= Measure($"  zstd -19, long-range matching, {(workers == 0 ? "one thread" : $"{workers} threads")} (ZstdSharp)",
                    input, data => ZstdCompress(data, 19, workers), ZstdDecompress);
            }
        }

        return ok;
    }

    /// <summary>
    /// Compresses, decompresses, checks the round trip and prints one line.
    /// </summary>
    /// <param name="label">The report's label.</param>
    /// <param name="input">The data.</param>
    /// <param name="compress">Data to (compressed bytes, decoder properties).</param>
    /// <param name="decompress">(compressed bytes, properties) and the original length to the data.</param>
    /// <returns>True when the round trip gave the input back.</returns>
    private static bool Measure(string label, byte[] input, Func<byte[], (byte[] Packed, byte[] Properties)> compress,
        Func<(byte[] Packed, byte[] Properties), int, byte[]> decompress)
    {
        var watch = Stopwatch.StartNew();
        var packed = compress(input);
        double compressSeconds = watch.Elapsed.TotalSeconds;
        watch.Restart();
        byte[] back = decompress(packed, input.Length);
        double decompressSeconds = watch.Elapsed.TotalSeconds;
        bool same = back.AsSpan().SequenceEqual(input);
        Console.WriteLine($"{label,-60}{packed.Packed.Length / 1e6,8:0.00} MB {100.0 * packed.Packed.Length / input.Length,6:0.0} %  " +
                          $"compress {input.Length / 1e6 / compressSeconds,6:0.0} MB/s  restore {input.Length / 1e6 / decompressSeconds,6:0.0} MB/s  " +
                          $"round trip {(same ? "ok" : "FAILED")}");
        return same;
    }

    /// <summary>PPMd variant I compression with SharpCompress.</summary>
    /// <param name="input">The data.</param>
    /// <param name="order">The model order (at most 16 in SharpCompress 1.0.0).</param>
    /// <param name="memoryMb">The model memory in MB (the decoder needs the same).</param>
    /// <returns>The compressed bytes and the properties the decoder needs.</returns>
    private static (byte[], byte[]) PpmdCompress(byte[] input, int order, int memoryMb)
    {
        var properties = new PpmdProperties(memoryMb << 20, order);
        using var output = new MemoryStream();
        using (var ppmd = new PpmdStream(properties, output, compress: true))
        {
            ppmd.Write(input, 0, input.Length);
        }

        return (output.ToArray(), properties.Properties);
    }

    /// <summary>PPMd decompression with SharpCompress.</summary>
    /// <param name="packed">The compressed bytes and their properties.</param>
    /// <param name="length">The original length.</param>
    /// <returns>The data.</returns>
    private static byte[] PpmdDecompress((byte[] Packed, byte[] Properties) packed, int length)
    {
        using var ppmd = new PpmdStream(new PpmdProperties(packed.Properties), new MemoryStream(packed.Packed), compress: false);
        return ReadExactly(ppmd, length);
    }

    /// <summary>LZMA compression with SharpCompress (64 MiB dictionary, 273 fast bytes, an end marker).</summary>
    /// <param name="input">The data.</param>
    /// <returns>The compressed bytes and the properties the decoder needs.</returns>
    private static (byte[], byte[]) LzmaCompress(byte[] input)
    {
        using var output = new MemoryStream();
        byte[] properties;
        using (var lzma = new LzmaStream(new LzmaEncoderProperties(eos: true, dictionary: 64 << 20, numFastBytes: 273), isLzma2: false, output))
        {
            lzma.Write(input, 0, input.Length);
            properties = lzma.Properties;
        }

        return (output.ToArray(), properties);
    }

    /// <summary>LZMA decompression with SharpCompress.</summary>
    /// <param name="packed">The compressed bytes and their properties.</param>
    /// <param name="length">The original length.</param>
    /// <returns>The data.</returns>
    private static byte[] LzmaDecompress((byte[] Packed, byte[] Properties) packed, int length)
    {
        using var lzma = new LzmaStream(packed.Properties, new MemoryStream(packed.Packed), packed.Packed.Length, length, null, isLzma2: false);
        return ReadExactly(lzma, length);
    }

    /// <summary>zstd compression with ZstdSharp, the managed zstd port BetterClipboard already ships (it reads Codex's
    /// compressed session files).</summary>
    /// <param name="input">The data.</param>
    /// <param name="level">The compression level (19: the strongest below zstd's memory-hungry "ultra" levels).</param>
    /// <param name="workers">Worker threads: 0 compresses on the calling thread; more split the input into jobs that
    /// compress in parallel (zstd's own multithreading, which keeps long-range matching across the jobs).</param>
    /// <returns>The compressed frame, and no properties (a zstd frame describes itself).</returns>
    /// <exception cref="ZstdException">ZstdSharp refused a parameter or failed to compress.</exception>
    private static (byte[], byte[]) ZstdCompress(byte[] input, int level, int workers)
    {
        using var compressor = new Compressor(level);
        // A 128 MiB window with long-range matching, the setting probe_backup_compression.py measures as
        // zstd -19 --long=27: it finds repeats far back in the stream, such as a picture that resembles an earlier one.
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, 27);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, workers);
        return (compressor.Wrap(input).ToArray(), Array.Empty<byte>());
    }

    /// <summary>zstd decompression with ZstdSharp.</summary>
    /// <param name="packed">ZstdCompress's frame (its properties are empty).</param>
    /// <param name="length">The original length: the most the frame may give back.</param>
    /// <returns>The data.</returns>
    /// <exception cref="ZstdException">The frame is damaged, larger than the length, or asks for a window above
    /// 128 MiB.</exception>
    private static byte[] ZstdDecompress((byte[] Packed, byte[] Properties) packed, int length)
    {
        using var decompressor = new Decompressor();
        // 2^27 is zstd's default limit too; said here, so the decoder's memory bound is visible next to the encoder's.
        decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 27);
        return decompressor.Unwrap(packed.Packed, length).ToArray();
    }

    /// <summary>Reads up to a length from a stream (fewer bytes when it ends early, which the round trip then reports).</summary>
    /// <param name="stream">The decompressing stream.</param>
    /// <param name="length">The expected length.</param>
    /// <returns>What was read.</returns>
    private static byte[] ReadExactly(Stream stream, int length)
    {
        byte[] result = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = stream.Read(result, read, length - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return result[..read];
    }
}
