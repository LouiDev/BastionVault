using BastionVault.Core.Tests.Vault;

namespace BastionVault.Core.Tests.Session;

/// <summary>
/// The stream from <see cref="IVaultSession.OpenReadAsync"/> is seekable: a container parser that jumps to
/// an index at the end of a file (an MP4 <c>moov</c> box, say) must get authenticated bytes from any offset
/// without reading everything before it, and the bytes must be the same ones a forward read would give.
/// </summary>
public sealed class SeekableReadTests : IDisposable
{
    /// <summary>Three chunks and a bit, so seeks cross chunk boundaries in both directions.</summary>
    private const int Length = (3 * 1024 * 1024) + 12_345;

    private const int Chunk = 1024 * 1024;

    private readonly VaultTestContext _context = new();

    public void Dispose() => _context.Dispose();

    private async Task<(IVaultSession Session, EntryId File, byte[] Plain)> ImportAsync()
    {
        byte[] plain = VaultTestContext.Bytes(Length, seed: 4711);
        string path = _context.WriteSourceFile("clip.bin", plain);
        IVaultSession session = await _context.CreateAsync();
        ImportResult result = await session.ImportAsync(EntryId.Root, [path], new ImportOptions(), null, CancellationToken.None);
        return (session, result.Imported[0], plain);
    }

    [Fact]
    public async Task The_stream_reports_itself_seekable_with_the_plaintext_length()
    {
        (IVaultSession session, EntryId file, _) = await ImportAsync();
        await using (session)
        {
            await using Stream stream = await session.OpenReadAsync(file, CancellationToken.None);

            Assert.True(stream.CanSeek);
            Assert.True(stream.CanRead);
            Assert.False(stream.CanWrite);
            Assert.Equal(Length, stream.Length);
            Assert.Equal(0, stream.Position);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData((2 * Chunk) + 777)]
    [InlineData(Length - 1)]
    public async Task Seeking_to_an_offset_yields_the_same_bytes_a_forward_read_would(long offset)
    {
        (IVaultSession session, EntryId file, byte[] plain) = await ImportAsync();
        await using (session)
        {
            await using Stream stream = await session.OpenReadAsync(file, CancellationToken.None);

            Assert.Equal(offset, stream.Seek(offset, SeekOrigin.Begin));
            Assert.Equal(offset, stream.Position);

            int expected = (int)Math.Min(4096, Length - offset);
            byte[] buffer = new byte[expected];
            int read = await stream.ReadAtLeastAsync(buffer, expected, throwOnEndOfStream: false);

            Assert.Equal(expected, read);
            Assert.True(plain.AsSpan((int)offset, expected).SequenceEqual(buffer), "the bytes after the seek differ from the source");
            Assert.Equal(offset + expected, stream.Position);
        }
    }

    [Fact]
    public async Task Seeks_from_current_and_from_end_are_honoured()
    {
        (IVaultSession session, EntryId file, byte[] plain) = await ImportAsync();
        await using (session)
        {
            await using Stream stream = await session.OpenReadAsync(file, CancellationToken.None);

            byte[] one = new byte[1];
            stream.Seek(100, SeekOrigin.Begin);
            stream.Seek(-50, SeekOrigin.Current);
            Assert.Equal(50, stream.Position);
            Assert.Equal(1, stream.Read(one));
            Assert.Equal(plain[50], one[0]);

            stream.Seek(-1, SeekOrigin.End);
            Assert.Equal(Length - 1, stream.Position);
            Assert.Equal(1, stream.Read(one));
            Assert.Equal(plain[^1], one[0]);
            Assert.Equal(0, stream.Read(one));

            stream.Position = Chunk + 5;
            Assert.Equal(1, stream.Read(one));
            Assert.Equal(plain[Chunk + 5], one[0]);
        }
    }

    [Fact]
    public async Task A_seek_at_or_past_the_end_reads_nothing_and_a_negative_one_is_refused()
    {
        (IVaultSession session, EntryId file, _) = await ImportAsync();
        await using (session)
        {
            await using Stream stream = await session.OpenReadAsync(file, CancellationToken.None);

            stream.Seek(Length, SeekOrigin.Begin);
            Assert.Equal(0, stream.Read(new byte[16]));

            stream.Seek(Length + 4096, SeekOrigin.Begin);
            Assert.Equal(Length + 4096, stream.Position);
            Assert.Equal(0, stream.Read(new byte[16]));

            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Begin));
            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -5);

            // The refused seeks did not move the cursor.
            Assert.Equal(Length + 4096, stream.Position);
        }
    }

    [Fact]
    public async Task Random_access_reads_match_the_source_everywhere()
    {
        (IVaultSession session, EntryId file, byte[] plain) = await ImportAsync();
        await using (session)
        {
            await using Stream stream = await session.OpenReadAsync(file, CancellationToken.None);
            var random = new Random(97);
            byte[] buffer = new byte[64 * 1024];

            for (int i = 0; i < 200; i++)
            {
                int offset = random.Next(Length);
                int count = random.Next(1, Math.Min(buffer.Length, Length - offset) + 1);

                stream.Position = offset;
                int read = stream.ReadAtLeast(buffer.AsSpan(0, count), count, throwOnEndOfStream: false);

                Assert.Equal(count, read);
                Assert.True(plain.AsSpan(offset, count).SequenceEqual(buffer.AsSpan(0, count)), $"mismatch at offset {offset}, count {count}");
            }
        }
    }

    [Fact]
    public async Task Seeking_straight_into_a_tampered_chunk_still_fails_authentication()
    {
        using TamperVault vault = await TamperVault.CreateAsync();
        using VaultImage image = vault.Image();

        // Damage the middle chunk of the three-chunk file, leaving the first one intact.
        (long offset, long length) = image.ChunkRange(TamperVault.BigFile, 1);
        image.Flip(offset + (length / 2));
        vault.Write(image.Bytes);

        await using IVaultSession session = await vault.OpenTargetAsync();
        EntryInfo entry = VaultTestContext.Entry(session, TamperVault.BigFile);
        await using Stream stream = await session.OpenReadAsync(entry.Id, CancellationToken.None);

        // The first chunk reads fine; landing inside the damaged one throws from the seek itself.
        Assert.Equal(16, stream.Read(new byte[16]));
        var ex = Assert.Throws<VaultIntegrityException>(() => stream.Seek(Chunk + 500, SeekOrigin.Begin));
        Assert.Equal(VaultErrorCode.DataCorrupt, ex.Code);
    }
}
