using Cuepoint.Audio;

namespace Cuepoint.Tests.Audio;

public class MixAheadBufferTests
{
    [Fact]
    public void What_s_written_is_read_back_in_order_around_the_end()
    {
        var buffer = new MixAheadBuffer(8);
        var output = new float[8];

        Assert.Equal(6, buffer.Write([1, 2, 3, 4, 5, 6]));
        Assert.Equal(4, buffer.Read(output.AsSpan(0, 4)));
        // Past the end of the array, and round to its start.
        Assert.Equal(6, buffer.Write([7, 8, 9, 10, 11, 12]));
        Assert.Equal(8, buffer.Available);
        Assert.Equal(8, buffer.Read(output));

        Assert.Equal(new float[] { 5, 6, 7, 8, 9, 10, 11, 12 }, output);
        Assert.Equal(0, buffer.Available);
    }

    [Fact]
    public void It_takes_only_what_there_s_room_for_and_gives_only_what_s_there()
    {
        var buffer = new MixAheadBuffer(4);
        var output = new float[6];

        Assert.Equal(4, buffer.Write([1, 2, 3, 4, 5, 6]));
        Assert.Equal(0, buffer.Free);
        Assert.Equal(4, buffer.Read(output));
        Assert.Equal(0, buffer.Read(output));
        Assert.Equal(new float[] { 1, 2, 3, 4, 0, 0 }, output);
    }

    [Fact]
    public void A_writer_and_a_reader_at_once_lose_and_reorder_nothing()
    {
        const int TOTAL = 200_000;
        var buffer = new MixAheadBuffer(1000);
        var writer = Task.Run(() =>
        {
            var chunk = new float[137];
            var next = 0;
            while (next < TOTAL)
            {
                var count = Math.Min(chunk.Length, Math.Min(TOTAL - next, buffer.Free));
                for (var i = 0; i < count; i++)
                    chunk[i] = next + i;
                next += buffer.Write(chunk.AsSpan(0, count));
            }
        });

        var expected = 0;
        var output = new float[211];
        while (expected < TOTAL)
        {
            var count = buffer.Read(output);
            for (var i = 0; i < count; i++)
                Assert.Equal(expected++, output[i]);
        }
        writer.Wait();
    }
}
